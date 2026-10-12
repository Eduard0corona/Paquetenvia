import { describe, expect, it, vi } from "vitest";
import { createDriverCachePartition } from "../cache/driver-stops-cache";
import { stopList } from "../contracts/driver-stop.fixtures";
import type { DriverStopCod } from "../contracts/driver-stop";
import type { DriverFailedDeliveryReason } from "../contracts/failed-delivery";
import type { DriverSyncApi } from "../offline/driver-sync-api";
import type { DriverSyncScheduler } from "../offline/driver-sync-scheduler";
import {
  MemoryQueue,
  amountCents,
  collectionOperation,
  failedDeliveryOperation,
  orderId,
  reportId,
  uuid,
} from "../offline/failed-delivery.fixtures";
import type {
  DriverOfflineOperation,
  DriverOperationalStatus,
} from "../offline/operation-contract";
import type { DriverSession } from "../session/driver-session";
import { DriverOperationsController } from "./driver-operations-controller";

/**
 * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: "Enviar reporte" queues one
 * report with its photo; the controller refuses it where the stop does not
 * offer it (owner answers "Solo si hay cobro" and "No, llama a despacho"),
 * rebuilds a held report without a second incident, and pauses the sync while
 * it discards.
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

async function harness(initial: readonly DriverOfflineOperation[] = []) {
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
    cod,
  };
}

describe("Enviar reporte", () => {
  it("queues one report with its photo, and says it will be sent with a connection", async () => {
    const { controller, queue, scheduler, key } = await harness();

    await expect(controller.enqueueFailedDelivery(report("ADDRESS_NOT_FOUND"), placePhoto())).resolves.toBe(true);

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
        incidentId: null,
      },
    });
    expect(queue.proofs.get(newId)?.sha256).toBe(queued.sha256);
    expect(controller.current.message).toBe("Reporte guardado. Se enviará cuando haya conexión.");
    expect(scheduler.requestSync).toHaveBeenCalledWith(false);
    // At most one report per order.
    await expect(controller.enqueueFailedDelivery(report(), placePhoto())).resolves.toBe(false);
    expect(queue.operations).toHaveLength(1);
    await controller.dispose();
  });

  it("offers \"No pagó el cobro\" only on a stop that collects cash", async () => {
    const without = await harness();
    await expect(
      without.controller.enqueueFailedDelivery(report("PAYMENT_UNAVAILABLE"), placePhoto()),
    ).resolves.toBe(false);
    expect(without.queue.operations).toEqual([]);
    expect(without.controller.current.message).toBe("Elige un motivo de la lista.");

    const withCash = await harness();
    await expect(
      withCash.controller.enqueueFailedDelivery(report("PAYMENT_UNAVAILABLE", cashPending), placePhoto()),
    ).resolves.toBe(true);
    expect(withCash.queue.only().failedDelivery?.reasonCode).toBe("PAYMENT_UNAVAILABLE");
  });

  it.each<[string, Partial<ReturnType<typeof report>>]>([
    ["off the door", { projectedStatus: "IN_TRANSIT" }],
    ["after a failed attempt", { projectedStatus: "FAILED_ATTEMPT" }],
    ["with a reason outside the list", { reasonCode: "OTHER" as DriverFailedDeliveryReason }],
  ])("refuses a report %s and stores nothing", async (_name, patch) => {
    const { controller, queue } = await harness();
    await expect(controller.enqueueFailedDelivery({ ...report(), ...patch }, placePhoto())).resolves.toBe(false);
    expect(queue.operations).toEqual([]);
    expect(queue.proofs.size).toBe(0);
  });

  it("asks to call dispatch when the cash is recorded and to discard a saved collection first", async () => {
    const recorded = await harness();
    await expect(
      recorded.controller.enqueueFailedDelivery(
        report("RECIPIENT_REFUSED", { ...cashPending, recorded: true }),
        placePhoto(),
      ),
    ).resolves.toBe(false);
    expect(recorded.controller.current.message).toContain("Llama a despacho");

    const accepted = await harness([collectionOperation({ status: "AWAITING_REST_CONFIRMATION" })]);
    await expect(
      accepted.controller.enqueueFailedDelivery(report("RECIPIENT_REFUSED", cashPending), placePhoto()),
    ).resolves.toBe(false);
    expect(accepted.controller.current.message).toContain("Llama a despacho");

    const saved = await harness([collectionOperation()]);
    await expect(
      saved.controller.enqueueFailedDelivery(report("RECIPIENT_REFUSED", cashPending), placePhoto()),
    ).resolves.toBe(false);
    expect(saved.controller.current.message).toBe(
      "Hay un cobro guardado en el teléfono. Si fue un error, descártalo antes de reportar.",
    );
    expect(saved.queue.operations).toHaveLength(1);
  });

  it.each<[string, Blob | undefined]>([
    ["no photo", undefined],
    ["an empty photo", placePhoto("image/png", 0)],
    ["a GIF", placePhoto("image/gif")],
    ["a photo over 10 MiB", placePhoto("image/jpeg", 10 * 1024 * 1024 + 1)],
  ])("refuses %s and keeps the form", async (_name, blob) => {
    const { controller, queue } = await harness();
    await expect(controller.enqueueFailedDelivery(report(), blob)).resolves.toBe(false);
    expect(queue.operations).toEqual([]);
    expect(controller.current).toMatchObject({
      mutating: false,
      message: "No fue posible guardar el reporte. Verifica tipo, tamaño y espacio disponible.",
    });
  });
});

describe("a held report", () => {
  it("is rebuilt for the current version with the same incident, photo proof and capture instant", async () => {
    const held = failedDeliveryOperation({
      status: "NEEDS_ATTENTION",
      safeError: "VERSION_CONFLICT",
      rejectionCode: "VERSION_CONFLICT",
      uploadSessionId: uuid(0x500),
      uploadAccepted: true,
      proofId: uuid(0x600),
      failedDelivery: {
        reasonCode: "SECURITY_RISK",
        incidentIdempotencyKey: `drv2-${reportId}-incident`,
        incidentId: uuid(0x700),
      },
    });
    const { controller, queue } = await harness([held]);

    await controller.rebuildForCurrentVersion(held.id, "DELIVERING", 10);

    expect(queue.only()).toMatchObject({
      id: newId,
      status: "PENDING",
      expectedVersion: 10,
      transitionIdempotencyKey: `drv2-${newId}-transition`,
      clientOccurredAt: held.clientOccurredAt,
      capturedAt: held.capturedAt,
      proofId: uuid(0x600),
      failedDelivery: held.failedDelivery,
    });
  });

  it("keeps the incident key and the photo when rebuilt before its proof existed", async () => {
    const held = failedDeliveryOperation({ status: "NEEDS_ATTENTION", safeError: "VERSION_CONFLICT" });
    const { controller, queue, key } = await harness([held]);
    queue.proofs.set(held.id, {
      schemaVersion: 1,
      partitionKey: key,
      operationId: held.id,
      blob: placePhoto(),
      contentType: "image/png",
      sizeBytes: 3,
      sha256: held.sha256!,
    });

    await controller.rebuildForCurrentVersion(held.id, "DELIVERING", 10);

    expect(queue.only()).toMatchObject({
      id: newId,
      sessionIdempotencyKey: `drv2-${newId}-session-1`,
      failedDelivery: { incidentIdempotencyKey: `drv2-${reportId}-incident`, incidentId: null },
    });
    expect(queue.proofs.has(newId)).toBe(true);
    expect(queue.proofs.has(held.id)).toBe(false);
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
