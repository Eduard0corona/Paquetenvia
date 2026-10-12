import { describe, expect, it, vi } from "vitest";
import {
  createDriverCachePartition,
  DriverDatabaseBlockedError,
} from "../cache/driver-stops-cache";
import { stopList } from "../contracts/driver-stop.fixtures";
import type { DriverStopCod } from "../contracts/driver-stop";
import type { DriverFailedDeliveryReason } from "../contracts/failed-delivery";
import type { DriverSyncApi } from "../offline/driver-sync-api";
import type { DriverSyncScheduler } from "../offline/driver-sync-scheduler";
import {
  MemoryQueue,
  amountCents,
  collectionOperation,
  deliveryStop,
  failedDeliveryOperation,
  orderId,
  reportId,
  uuid,
} from "../offline/failed-delivery.fixtures";
import type {
  DriverOfflineOperation,
  DriverOperationalStatus,
} from "../offline/operation-contract";
import {
  driverDispatchRoleReportMessage,
  driverFailedDeliveryAvailability,
  driverRoleLoadingReportMessage,
  driverRoleUnknownReportMessage,
} from "../offline/failed-delivery-policy";
import { driverReportRoleOf } from "../contracts/organization-roles";
import { projectDriverOperations } from "../offline/operation-projection";
import type { DriverSession } from "../session/driver-session";
import {
  DriverOperationsController,
  driverCashRecordedRefusalMessage,
  driverDatabaseBlockedMessage,
  driverMessageCallsDispatch,
  driverOperationExpiredMessage,
} from "./driver-operations-controller";
import {
  myOrganizationRolesAnswer,
  olderApiAnswer,
  startedRolesController,
  type RolesAnswer,
} from "./driver-roles.fixtures";

/**
 * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: "Enviar reporte" queues one
 * report with its photo; the controller refuses it where the stop does not
 * offer it (owner answers "Solo si hay cobro" and "No, llama a despacho"),
 * rebuilds a held report without a second incident, and pauses the sync while
 * it discards. The person's roles always come from an answer of
 * getMyOrganizationRoles shaped like the API's, through the real client and
 * DriverRolesController (ORG-MY-ROLES-2026-10-12).
 */
const session: DriverSession = {
  organizationId: "44444444-4444-4444-8444-444444444444",
  cacheNamespace: "opaque-failed-delivery-controller",
  getAccessToken: () => "memory-only",
};
const newId = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
const cashPending: DriverStopCod = {
  expected: { currency: "MXN", amount_cents: amountCents },
  recorded: false,
};

function placePhoto(type = "image/png", size = 3): Blob {
  return new Blob([new Uint8Array(size).fill(9)], { type });
}

async function harness(
  initial: readonly DriverOfflineOperation[] = [],
  options: { readonly roles?: RolesAnswer | "pending" } = {},
) {
  const roles = await startedRolesController(session, options.roles ?? myOrganizationRolesAnswer("DRIVER"));
  const key = (await createDriverCachePartition(session)).key;
  const queue = new MemoryQueue(initial.map((operation) => ({ ...operation, partitionKey: key })));
  const scheduler = {
    start: vi.fn(),
    requestSync: vi.fn().mockResolvedValue(undefined),
    dispose: vi.fn().mockResolvedValue(undefined),
    pause: vi.fn().mockResolvedValue(undefined),
    resume: vi.fn(),
  };
  const controller = new DriverOperationsController({
    session,
    queue,
    api: {} as DriverSyncApi,
    refreshStops: async () => stopList([]),
    onAccessRevoked: vi.fn(),
    reportRole: () => driverReportRoleOf(roles.current),
    schedulerFactory: () => scheduler as unknown as DriverSyncScheduler,
    now: () => new Date("2026-10-12T18:45:00.000Z"),
    randomUuid: () => newId,
  });
  await controller.start();
  return { controller, queue, scheduler, key };
}

function report(
  reasonCode: DriverFailedDeliveryReason = "RECIPIENT_ABSENT",
  cod: DriverStopCod | null = null,
) {
  return {
    orderId,
    reasonCode,
    projectedStatus: "DELIVERING" as DriverOperationalStatus,
    projectedVersion: 8,
    confirmedStatus: "DELIVERING" as const,
    confirmedVersion: 8,
    cod,
  };
}

const stored = { stored: true };
const refused = (message: string) => ({ stored: false, message });

describe("Enviar reporte", () => {
  it("queues one report with its photo, and says it will be sent with a connection", async () => {
    const { controller, queue, scheduler, key } = await harness();

    await expect(
      controller.enqueueFailedDelivery(report("ADDRESS_NOT_FOUND"), placePhoto()),
    ).resolves.toEqual(stored);

    const queued = queue.only();
    expect(queued).toMatchObject({
      id: newId,
      partitionKey: key,
      kind: "FAILED_DELIVERY",
      expectedVersion: 8,
      contentType: "image/png",
      sizeBytes: 3,
      failedDelivery: {
        reasonCode: "ADDRESS_NOT_FOUND",
        incidentIdempotencyKey: `drv2-${newId}-incident`,
        incidentRequested: false,
        incidentId: null,
      },
    });
    expect(queue.proofs.get(newId)?.sha256).toBe(queued.sha256);
    expect(controller.current.message).toBe("Reporte guardado. Se enviará cuando haya conexión.");
    expect(scheduler.requestSync).toHaveBeenCalledWith(false);
    // At most one report per order, and the form says so (MINOR 9).
    await expect(controller.enqueueFailedDelivery(report(), placePhoto())).resolves.toEqual(
      refused("Ya hay un reporte guardado en el teléfono para esta entrega."),
    );
    expect(queue.operations).toHaveLength(1);
    await controller.dispose();
  });

  it("offers \"No pagó el cobro\" only on a stop that collects cash", async () => {
    const without = await harness();
    await expect(
      without.controller.enqueueFailedDelivery(report("PAYMENT_UNAVAILABLE"), placePhoto()),
    ).resolves.toEqual(refused("Elige un motivo de la lista."));
    expect(without.queue.operations).toEqual([]);
    expect(without.controller.current.message).toBe("Elige un motivo de la lista.");

    const withCash = await harness();
    await expect(
      withCash.controller.enqueueFailedDelivery(report("PAYMENT_UNAVAILABLE", cashPending), placePhoto()),
    ).resolves.toEqual(stored);
    expect(withCash.queue.only().failedDelivery?.reasonCode).toBe("PAYMENT_UNAVAILABLE");
  });

  it.each<[string, Partial<ReturnType<typeof report>>, string]>([
    ["off the door", { projectedStatus: "IN_TRANSIT" }, "Actualiza la parada antes de reportar."],
    ["after a failed attempt", { projectedStatus: "FAILED_ATTEMPT" }, "Actualiza la parada antes de reportar."],
    ["with a reason outside the list", { reasonCode: "OTHER" as DriverFailedDeliveryReason }, "Elige un motivo de la lista."],
  ])("refuses a report %s, says why and stores nothing", async (_name, patch, message) => {
    const { controller, queue } = await harness();
    await expect(
      controller.enqueueFailedDelivery({ ...report(), ...patch }, placePhoto()),
    ).resolves.toEqual(refused(message));
    expect(queue.operations).toEqual([]);
    expect(queue.proofs.size).toBe(0);
  });

  it("asks to call dispatch when the cash is recorded and to discard a saved collection first", async () => {
    const callDispatch = refused(
      "El cobro ya está registrado. Llama a despacho: ellos deciden y coordinan la devolución del cobro.",
    );
    const recorded = await harness();
    await expect(
      recorded.controller.enqueueFailedDelivery(
        report("RECIPIENT_REFUSED", { ...cashPending, recorded: true }),
        placePhoto(),
      ),
    ).resolves.toEqual(callDispatch);
    expect(recorded.controller.current.message).toContain("Llama a despacho");
    // DRV-DISPATCH-CONTACT-2026-10-12: the form and the sync summary put "Llamar a despacho" next to this refusal.
    expect(recorded.controller.current.message).toBe(driverCashRecordedRefusalMessage);
    expect(driverMessageCallsDispatch(recorded.controller.current.message)).toBe(true);

    const accepted = await harness([collectionOperation({ status: "AWAITING_REST_CONFIRMATION" })]);
    await expect(
      accepted.controller.enqueueFailedDelivery(report("RECIPIENT_REFUSED", cashPending), placePhoto()),
    ).resolves.toEqual(callDispatch);

    const saved = await harness([collectionOperation()]);
    await expect(
      saved.controller.enqueueFailedDelivery(report("RECIPIENT_REFUSED", cashPending), placePhoto()),
    ).resolves.toEqual(
      refused("Hay un cobro guardado en el teléfono. Si fue un error, descártalo antes de reportar."),
    );
    expect(saved.queue.operations).toHaveLength(1);
  });

  it.each([[["DISPATCHER", "DRIVER"]], [["DRIVER", "PLATFORM_ADMIN"]]])(
    "refuses a report from a person whose roles answer also lists %j (owner answer \"Registrar desde Despacho\")",
    async (roles) => {
      const { controller, queue } = await harness([], { roles: myOrganizationRolesAnswer(...roles) });
      await expect(controller.enqueueFailedDelivery(report(), placePhoto())).resolves.toEqual(
        refused(driverDispatchRoleReportMessage),
      );
      expect(queue.operations).toEqual([]);
      expect(queue.proofs.size).toBe(0);
    },
  );

  it.each<[string, RolesAnswer]>([
    ["an older API without the operation (404)", olderApiAnswer],
    ["an answer that breaks the contract", { status: 200, body: JSON.stringify({ roles: [] }) }],
    ["an unavailable API", { status: 503, body: "{}" }],
  ])("refuses a report when the roles are unknown after %s (it fails closed)", async (_name, roles) => {
    const { controller, queue } = await harness([], { roles });
    await expect(controller.enqueueFailedDelivery(report(), placePhoto())).resolves.toEqual(
      refused(driverRoleUnknownReportMessage),
    );
    expect(queue.operations).toEqual([]);
    expect(queue.proofs.size).toBe(0);
  });

  it("refuses a report, without the unknown-role notice, while the first roles request is still on its way", async () => {
    const { controller, queue } = await harness([], { roles: "pending" });
    await expect(controller.enqueueFailedDelivery(report(), placePhoto())).resolves.toEqual(
      refused(driverRoleLoadingReportMessage),
    );
    expect(queue.operations).toEqual([]);
    expect(queue.proofs.size).toBe(0);
  });

  it("takes a driver whose other roles are not dispatch (DRIVER and VIEWER)", async () => {
    const { controller, queue } = await harness([], { roles: myOrganizationRolesAnswer("VIEWER", "DRIVER") });
    await expect(controller.enqueueFailedDelivery(report(), placePhoto())).resolves.toEqual(stored);
    expect(queue.only().kind).toBe("FAILED_DELIVERY");
  });

  it("takes a new report while an earlier one REST already confirms waits to leave the queue", async () => {
    // The earlier report was accepted at version 8; dispatch chose "Reintentar
    // hoy" and REST shows the stop at the door again at version 11 before the
    // sync removed the earlier report.
    const earlier = failedDeliveryOperation({ status: "AWAITING_REST_CONFIRMATION" });
    const atTheDoorAgain = deliveryStop({ aggregate_version: 11 });
    const projection = projectDriverOperations([atTheDoorAgain], [earlier]);
    expect(
      driverFailedDeliveryAvailability(projection.stops[0], projection.operations, "driver"),
    ).toEqual({ kind: "offered", paymentReason: false });
    const { controller, queue } = await harness([earlier]);

    await expect(
      controller.enqueueFailedDelivery(
        {
          ...report("ADDRESS_NOT_FOUND"),
          projectedVersion: 11,
          confirmedVersion: 11,
        },
        placePhoto(),
      ),
    ).resolves.toEqual(stored);

    expect(queue.operations.map((operation) => [operation.id, operation.expectedVersion])).toEqual([
      [earlier.id, 8],
      [newId, 11],
    ]);
  });

  it("still refuses a second report while the earlier one is not confirmed", async () => {
    // Accepted, but REST still shows the version it was sent for.
    const accepted = failedDeliveryOperation({ status: "AWAITING_REST_CONFIRMATION" });
    const waiting = await harness([accepted]);
    await expect(waiting.controller.enqueueFailedDelivery(report(), placePhoto())).resolves.toEqual(
      refused("Ya hay un reporte guardado en el teléfono para esta entrega."),
    );
    expect(waiting.queue.operations).toHaveLength(1);

    // Not accepted yet: a later version alone never settles it.
    const pending = await harness([failedDeliveryOperation()]);
    await expect(
      pending.controller.enqueueFailedDelivery(
        { ...report(), projectedVersion: 11, confirmedVersion: 11 },
        placePhoto(),
      ),
    ).resolves.toEqual(refused("Ya hay un reporte guardado en el teléfono para esta entrega."));
    expect(pending.queue.operations).toHaveLength(1);
  });

  it.each<[string, Blob | undefined]>([
    ["no photo", undefined],
    ["an empty photo", placePhoto("image/png", 0)],
    ["a GIF", placePhoto("image/gif")],
    ["a photo over 10 MiB", placePhoto("image/jpeg", 10 * 1024 * 1024 + 1)],
  ])("refuses %s and keeps the form", async (_name, blob) => {
    const { controller, queue } = await harness();
    const message = "No fue posible guardar el reporte. Verifica tipo, tamaño y espacio disponible.";
    await expect(controller.enqueueFailedDelivery(report(), blob)).resolves.toEqual(refused(message));
    expect(queue.operations).toEqual([]);
    expect(controller.current).toMatchObject({ mutating: false, message });
  });
});

describe("a held report", () => {
  it.each<[string, Partial<DriverOfflineOperation>]>([
    ["before its incident (the attempt moved on: report again with a new reason and photo)", {}],
    [
      "after its incident (it belongs to that attempt, and may already justify a failed attempt)",
      {
        uploadSessionId: uuid(0x500),
        uploadAccepted: true,
        proofId: uuid(0x600),
        failedDelivery: {
          reasonCode: "SECURITY_RISK",
          incidentIdempotencyKey: `drv2-${reportId}-incident`,
          incidentRequested: true,
          incidentId: uuid(0x700),
        },
      },
    ],
  ])("is never rebuilt for another version %s", async (_name, patch) => {
    const held = failedDeliveryOperation({
      status: "NEEDS_ATTENTION",
      safeError: "VERSION_CONFLICT",
      rejectionCode: "VERSION_CONFLICT",
      ...patch,
    });
    const { controller, queue, scheduler } = await harness([held]);

    await controller.rebuildForCurrentVersion(held.id, "DELIVERING", 10);

    expect(queue.only()).toEqual({ ...held, partitionKey: queue.only().partitionKey });
    expect(controller.current.message).toBe("Este reporte no se puede rehacer para otra versión.");
    expect(scheduler.requestSync).not.toHaveBeenCalledWith(false);
  });

  it("lets the driver discard what is left of an order dispatch moved on (MAJOR 5)", async () => {
    // Offline: "Recibí" and "Confirmar entrega"; dispatch then marked the
    // failed attempt, the sync held the collection and blocked the delivery.
    const collection = collectionOperation({ status: "NEEDS_ATTENTION", safeError: "VERSION_CONFLICT" });
    const delivery = {
      ...failedDeliveryOperation(),
      id: "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee",
      kind: "DELIVERY_PROOF" as const,
      targetStatus: "DELIVERED" as const,
      reason: "DRIVER_DELIVERY_CONFIRMED",
      transitionIdempotencyKey: "drv2-eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee-transition",
      sessionIdempotencyKey: "drv2-eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee-session-1",
      finalizeIdempotencyKey: "drv2-eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee-finalize-1",
      failedDelivery: null,
      status: "BLOCKED" as const,
    };
    const { controller, queue } = await harness([collection, delivery]);

    // FAILED_ATTEMPT is no step the phone acts on: the stop passes no status.
    await controller.discard(collection.id, null, 9);

    expect(queue.only()).toMatchObject({
      id: delivery.id,
      status: "NEEDS_ATTENTION",
      safeError: "VERSION_CONFLICT",
    });
    await controller.discard(delivery.id, null, 9);
    expect(queue.operations).toEqual([]);
  });

  it("releases the actions blocked behind one retried as it is", async () => {
    const collection = collectionOperation({ status: "NEEDS_ATTENTION", safeError: "VERSION_CONFLICT" });
    const delivery = {
      ...failedDeliveryOperation(),
      id: "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee",
      kind: "DELIVERY_PROOF" as const,
      targetStatus: "DELIVERED" as const,
      reason: "DRIVER_DELIVERY_CONFIRMED",
      failedDelivery: null,
      status: "BLOCKED" as const,
    };
    const { controller, queue } = await harness([collection, delivery]);

    await controller.retrySame(collection.id, "DELIVERING", 8);

    expect(queue.operations.map((operation) => [operation.kind, operation.status])).toEqual([
      ["COD_COLLECTION", "PENDING"],
      ["DELIVERY_PROOF", "PENDING"],
    ]);
  });

  it("is sent again once the collection that held it is discarded, with the sync paused meanwhile", async () => {
    const held = failedDeliveryOperation({ status: "NEEDS_ATTENTION", safeError: "COD_COLLECTED" });
    const collection = collectionOperation({ status: "BLOCKED" });
    const { controller, queue, scheduler } = await harness([held, collection]);
    const order: string[] = [];
    scheduler.pause.mockImplementation(async () => {
      order.push("pause");
    });
    scheduler.resume.mockImplementation(() => {
      order.push("resume");
    });

    await controller.discard(collection.id, "DELIVERING", 8);

    expect(order).toEqual(["pause", "resume"]);
    expect(queue.only()).toMatchObject({ kind: "FAILED_DELIVERY", status: "PENDING", safeError: null });
    expect(scheduler.requestSync).toHaveBeenCalledWith(false);
  });
});

describe("a database another tab keeps open", () => {
  it("asks to close the other Paquetenvia tabs instead of a bare \"not available\"", async () => {
    const queue = new MemoryQueue([]);
    queue.listOperations = async () => {
      throw new DriverDatabaseBlockedError();
    };
    const controller = new DriverOperationsController({
      session,
      queue,
      api: {} as DriverSyncApi,
      refreshStops: async () => stopList([]),
      onAccessRevoked: vi.fn(),
    });

    await controller.start();

    expect(controller.current.message).toBe(driverDatabaseBlockedMessage);
    expect(driverDatabaseBlockedMessage).toContain("Ciérrala y vuelve a cargar esta página");
    await controller.dispose();
  });
});

describe("an expired report (MINOR 8)", () => {
  it("is never presented as unsent nor to register again once its incident is or may be open", () => {
    const open = driverOperationExpiredMessage([{ kind: "FAILED_DELIVERY", incident: "open" }]);
    expect(open).toContain("ya quedó abierto como incidencia");
    expect(open).toContain("despacho decide");
    expect(open).not.toMatch(/ya no se enviará|Vuelve a registrar/);

    const maybe = driverOperationExpiredMessage([{ kind: "FAILED_DELIVERY", incident: "maybe" }]);
    expect(maybe).toContain("puede que ya haya quedado abierto como incidencia");
    expect(maybe).not.toMatch(/Vuelve a registrar/);

    // An unsent report and the other actions keep the OPS-003 notice.
    expect(driverOperationExpiredMessage([{ kind: "FAILED_DELIVERY", incident: "none" }])).toContain(
      "ya no se enviará. Vuelve a registrarla",
    );
    expect(driverOperationExpiredMessage(["CHECK_IN"])).toContain("«Llegué a recolección» venció");
    const mixed = driverOperationExpiredMessage([
      { kind: "CHECK_IN", incident: "none" },
      { kind: "FAILED_DELIVERY", incident: "open" },
    ]);
    expect(mixed).toContain("«Llegué a recolección» venció");
    expect(mixed).toContain("ya quedó abierto como incidencia");
  });
});
