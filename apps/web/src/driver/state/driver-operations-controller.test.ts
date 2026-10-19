import { describe, expect, it, vi } from "vitest";
import type { DriverStopsApi } from "../api/driver-stops-api";
import type {
  DriverCachePartition,
  DriverStopsCache,
  DriverStopsSnapshot,
} from "../cache/driver-stops-cache";
import type { DriverStop } from "../contracts/driver-stop";
import type { DriverOfflineQueue } from "../offline/driver-offline-queue";
import type { DriverSyncApi } from "../offline/driver-sync-api";
import {
  DriverSyncScheduler,
  type DriverSyncSchedulerOptions,
} from "../offline/driver-sync-scheduler";
import {
  createDriverOfflineOperation,
  type DriverOfflineOperation,
  type DriverProofBlobRecord,
} from "../offline/operation-contract";
import { projectDriverOperations } from "../offline/operation-projection";
import type { ValidatedDriverProof } from "../proofs/proof-file";
import { stopList, withStopDetails } from "../contracts/driver-stop.fixtures";
import type {
  DriverStopsRealtimeCallbacks,
  DriverStopsRealtimeFactory,
} from "../realtime/driver-stops-realtime";
import type { DriverSession } from "../session/driver-session";
import {
  DriverOperationsController,
  driverOperationExpiredMessage,
  driverSessionUnconfirmedSyncMessage,
} from "./driver-operations-controller";
import { DriverStopsController } from "./driver-stops-controller";

const partitionKey = "E".repeat(43);
const orderId = "11111111-1111-4111-8111-111111111111";
const operationId = "22222222-2222-4222-8222-222222222222";
const replacementId = "33333333-3333-4333-8333-333333333333";
const session: DriverSession = {
  organizationId: "44444444-4444-4444-8444-444444444444",
  cacheNamespace: "opaque-controller-test",
  getAccessToken: () => "memory-only",
};

describe("a remembered session (DRV-SESSION-EXPIRY-OFFLINE-2026-10-19, owner answer E3)", () => {
  it("opens the queue and takes new actions, but starts no sync, so nothing is sent", async () => {
    const queued = operation("CHECK_IN", 3);
    const queue = new MemoryQueue([queued]);
    const schedulerFactory = vi.fn();
    const transitionOrder = vi.fn();
    const controller = new DriverOperationsController({
      session: { ...session, remembered: true },
      queue,
      api: {
        transitionOrder,
        createProofUploadSession: vi.fn(),
        finalizeProof: vi.fn(),
        recordCodCollection: vi.fn(),
        openFailedDeliveryIncident: vi.fn(),
      } as DriverSyncApi,
      refreshStops: async () => stopList([]),
      onAccessRevoked: vi.fn(),
      schedulerFactory,
      sync: false,
      now: () => new Date("2026-07-26T12:01:00.000Z"),
      randomUuid: () => replacementId,
    });

    await controller.start();
    expect(controller.current.operations.map((candidate) => candidate.id)).toEqual([queued.id]);
    expect(schedulerFactory).not.toHaveBeenCalled();

    await expect(
      controller.enqueue({
        orderId: "55555555-5555-4555-8555-555555555555",
        kind: "CHECK_IN",
        projectedStatus: "ASSIGNED",
        projectedVersion: 2,
      }),
    ).resolves.toBe(true);
    expect(queue.operations).toHaveLength(2);
    expect(controller.current.message).toBe("Acción guardada. Se sincronizará cuando haya conexión.");

    await controller.syncNow();
    expect(controller.current.message).toBe(driverSessionUnconfirmedSyncMessage);
    expect(transitionOrder).not.toHaveBeenCalled();
    await controller.dispose();
  });
});

describe("driver operations conflict resolution", () => {
  it("creates an explicit upload-session attempt without changing capture identity", async () => {
    const original = {
      ...operation("PICKUP_PROOF", 4),
      status: "NEEDS_ATTENTION" as const,
      safeError: "SESSION_EXPIRED" as const,
      uploadSessionId: "55555555-5555-4555-8555-555555555555",
      uploadSessionExpiresAt: "2026-07-26T11:00:00.000Z",
      uploadAccepted: true,
    };
    const harness = await createHarness(original);

    await harness.controller.createNewSession(original.id);

    expect(harness.queue.operations[0]).toMatchObject({
      id: original.id,
      clientOccurredAt: original.clientOccurredAt,
      sessionAttempt: 2,
      status: "PENDING",
      uploadSessionId: null,
      uploadSessionExpiresAt: null,
      uploadAccepted: false,
    });
    await harness.controller.dispose();
  });

  it("retries the same operation only against the unchanged confirmed version", async () => {
    const original = {
      ...operation("CHECK_IN", 3),
      status: "NEEDS_ATTENTION" as const,
      safeError: "VERSION_CONFLICT" as const,
    };
    const harness = await createHarness(original);

    await harness.controller.retrySame(original.id, "ASSIGNED", 4);
    expect(harness.queue.operations[0].status).toBe("NEEDS_ATTENTION");
    expect(harness.controller.current.message).toContain("versión confirmada cambió");

    await harness.controller.retrySame(original.id, "ASSIGNED", 3);
    expect(harness.queue.operations[0]).toMatchObject({
      id: original.id,
      status: "PENDING",
      expectedVersion: 3,
      safeError: null,
    });
    await harness.controller.dispose();
  });

  it("rebuilds explicitly with a new operation id and current version", async () => {
    const original = {
      ...operation("CHECK_IN", 3),
      status: "NEEDS_ATTENTION" as const,
      safeError: "VERSION_CONFLICT" as const,
    };
    const harness = await createHarness(original);

    await harness.controller.rebuildForCurrentVersion(
      original.id,
      "ASSIGNED",
      5,
    );

    expect(harness.queue.operations).toHaveLength(1);
    expect(harness.queue.operations[0]).toMatchObject({
      id: replacementId,
      expectedVersion: 5,
      status: "PENDING",
    });
    expect(harness.queue.operations[0].id).not.toBe(original.id);
    await harness.controller.dispose();
  });

  it("discards the operation and its local proof together", async () => {
    const original = {
      ...operation("PICKUP_PROOF", 4),
      status: "NEEDS_ATTENTION" as const,
      safeError: "EVIDENCE_REJECTED" as const,
    };
    const harness = await createHarness(original, true);

    await harness.controller.discard(original.id);

    expect(harness.queue.operations).toEqual([]);
    expect(harness.queue.proofs.size).toBe(0);
    await harness.controller.dispose();
  });

  it("unblocks only a dependent chain that remains valid after discard", async () => {
    const conflict = {
      ...operation("CHECK_IN", 3),
      status: "NEEDS_ATTENTION" as const,
      safeError: "VERSION_CONFLICT" as const,
    };
    const dependent = {
      ...createDriverOfflineOperation({
        partitionKey,
        orderId,
        kind: "PICKUP_PROOF",
        expectedVersion: 4,
        proof: {
          contentType: "image/png",
          sizeBytes: 1,
          sha256: "a".repeat(64),
        },
        now: () => new Date("2026-07-26T12:00:01.000Z"),
        randomUuid: () => replacementId,
      }),
      status: "BLOCKED" as const,
    };
    const harness = await createHarness([conflict, dependent]);

    await harness.controller.discard(conflict.id, "AT_PICKUP", 4);

    expect(harness.queue.operations).toHaveLength(1);
    expect(harness.queue.operations[0]).toMatchObject({
      id: dependent.id,
      status: "PENDING",
      safeError: null,
    });
    await harness.controller.dispose();
  });
});

describe("driver operations enqueue result (photo preview)", () => {
  const pickupProof = {
    orderId,
    kind: "PICKUP_PROOF" as const,
    projectedStatus: "AT_PICKUP" as const,
    projectedVersion: 4,
  };

  it("resolves true once the action and the same photo Blob are stored", async () => {
    const harness = await createHarness([]);
    const photo = new Blob([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], {
      type: "image/png",
    });

    await expect(harness.controller.enqueue(pickupProof, photo)).resolves.toBe(true);

    expect(harness.queue.operations).toHaveLength(1);
    expect(harness.queue.operations[0]).toMatchObject({
      kind: "PICKUP_PROOF",
      expectedVersion: 4,
      contentType: "image/png",
      sizeBytes: 4,
    });
    expect(harness.queue.proofs.get(harness.queue.operations[0].id)?.blob).toBe(photo);
    expect(harness.controller.current.message).toBe(
      "Acción guardada. Se sincronizará cuando haya conexión.",
    );
    await harness.controller.dispose();
  });

  it("resolves false and stores nothing when the queue refuses the photo", async () => {
    const harness = await createHarness([]);
    const gif = new Blob([new Uint8Array([0x47, 0x49, 0x46])], { type: "image/gif" });

    await expect(harness.controller.enqueue(pickupProof, gif)).resolves.toBe(false);

    expect(harness.queue.operations).toEqual([]);
    expect(harness.queue.proofs.size).toBe(0);
    expect(harness.controller.current).toMatchObject({
      mutating: false,
      message:
        "No fue posible guardar la foto. Verifica tipo, tamaño y espacio disponible.",
    });
    await harness.controller.dispose();
  });

  it("keeps a stored action as saved and deduplicated when the queue cannot be read back", async () => {
    const harness = await createHarness([]);
    const listOperations = vi
      .spyOn(harness.queue, "listOperations")
      .mockRejectedValueOnce(new Error("synthetic read failure"));
    const photo = new Blob([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], {
      type: "image/png",
    });

    await expect(harness.controller.enqueue(pickupProof, photo)).resolves.toBe(true);

    expect(listOperations).toHaveBeenCalled();
    expect(harness.queue.operations).toHaveLength(1);
    expect(harness.controller.current).toMatchObject({
      mutating: false,
      message: "Acción guardada. Se sincronizará cuando haya conexión.",
    });
    expect(harness.controller.current.operations.map((value) => value.id)).toEqual([
      harness.queue.operations[0].id,
    ]);
    expect(harness.scheduler.requestSync).toHaveBeenCalledWith(false);

    // The same step again is recognised as already queued: no second operation.
    await expect(harness.controller.enqueue(pickupProof, photo)).resolves.toBe(false);
    expect(harness.queue.operations).toHaveLength(1);
    await harness.controller.dispose();
  });

  it("resolves false when the step no longer matches the stop", async () => {
    const harness = await createHarness([]);

    await expect(
      harness.controller.enqueue(
        { ...pickupProof, projectedStatus: "ASSIGNED" },
        new Blob([new Uint8Array([1])], { type: "image/png" }),
      ),
    ).resolves.toBe(false);

    expect(harness.queue.operations).toEqual([]);
    await harness.controller.dispose();
  });
});

describe("the stop screen while the previous action syncs (photo step kept)", () => {
  /**
   * What the stop detail renders through one sync pass of "Llegué a
   * recolección" (CHECK_IN, saved at version 3): the screen holds the stops of
   * the last REST read (DriverStopsController takes each confirmation read
   * before the scheduler sees it) and the queue the controller last
   * published, and projects them. The photo step of "Confirmar recolección" is
   * rendered for AT_PICKUP without attention (StopAction, keyed by the
   * projected status); any other value unmounts it and drops the photo the
   * driver is previewing (docs/development/driver-photo-selection-refresh.md).
   */
  async function shownDuringPass(): Promise<readonly string[]> {
    let server: { status: DriverStop["status"]; version: number } = {
      status: "ASSIGNED",
      version: 3,
    };
    const stopNow = () =>
      withStopDetails({
        order_id: orderId,
        aggregate_version: server.version,
        order_public_id: "ORD_SYNTHETIC_PHOTO_STEP",
        stop_type: "PICKUP",
        status: server.status,
        address_summary: "Zona sintética",
      });
    const queue = new MemoryQueue([operation("CHECK_IN", 3)]);
    const page = trackedPage();
    let shownStops: readonly DriverStop[] = [stopNow()];
    let shownOperations: readonly DriverOfflineOperation[] | null = null;
    const shown: string[] = [];
    const render = () => {
      if (shownOperations === null) return;
      const [stop] = projectDriverOperations(shownStops, shownOperations).stops;
      shown.push(`${stop.projectedStatus}/attention=${stop.attentionCount}`);
    };
    const controller = new DriverOperationsController({
      session,
      queue,
      api: {
        // The server applies the check-in: AT_PICKUP at version 4.
        transitionOrder: vi.fn(async () => {
          server = { status: "AT_PICKUP", version: 4 };
          return { id: orderId, status: "AT_PICKUP" as const, version: 4 };
        }),
        createProofUploadSession: vi.fn(),
        finalizeProof: vi.fn(),
        recordCodCollection: vi.fn(),
        openFailedDeliveryIncident: vi.fn(),
      } as DriverSyncApi,
      refreshStops: async () => {
        const list = stopList([stopNow()]);
        shownStops = list.stops;
        render();
        return list;
      },
      onAccessRevoked: vi.fn(),
      schedulerFactory: (options) =>
        new DriverSyncScheduler({ ...options, window: page.window }),
      now: () => new Date("2026-07-26T12:01:00.000Z"),
      randomUuid: () => replacementId,
    });
    controller.subscribe((state) => {
      if (state.loading) return;
      shownOperations = state.operations;
      render();
    });
    await controller.start();
    await vi.waitFor(() => expect(queue.operations).toEqual([]));
    await controller.dispose();
    return shown;
  }

  it("never shows the confirmed action as a conflict between the confirmation read and its removal", async () => {
    const shown = await shownDuringPass();

    // Saved, read before sending, read after the 200, removed: four renders
    // at least, and every one keeps "Confirmar recolección" and its photo.
    expect(shown.length).toBeGreaterThanOrEqual(4);
    expect(shown).toEqual(shown.map(() => "AT_PICKUP/attention=0"));
  });
});

describe("a stale stop read that lands after the confirmation (photo step kept)", () => {
  /**
   * The residual risk of docs/development/driver-photo-selection-refresh.md:
   * a full read (here the REST resynchronization after a SignalR reconnect)
   * leaves while the stop is ASSIGNED at version 3 and the server answers it
   * then, but its answer reaches the phone only after the sync pass of "Llegué
   * a recolección" read AT_PICKUP at version 4 and removed the check-in. The
   * real DriverStopsController takes both reads; the screen projects its stops
   * with the queue the operations controller publishes, as StopAction does.
   */
  async function shownAroundStaleRead(): Promise<{
    readonly shown: readonly string[];
    readonly saved: readonly string[];
  }> {
    let server: { status: DriverStop["status"]; version: number } = {
      status: "ASSIGNED",
      version: 3,
    };
    const stopNow = () =>
      withStopDetails({
        order_id: orderId,
        aggregate_version: server.version,
        order_public_id: "ORD_SYNTHETIC_STALE_READ",
        stop_type: "PICKUP",
        status: server.status,
        address_summary: "Zona sintética",
      });
    let releaseFullReads: (() => void) | null = null;
    const fullReadsHeld = new Promise<void>((resolve) => {
      releaseFullReads = resolve;
    });
    let holdFullReads = false;
    const stopsApi: DriverStopsApi = {
      listStops: async (_signal, options) => {
        // The server answers when the request arrives; only the delivery of a
        // held full read waits.
        const answer = stopList([stopNow()]);
        if (options?.details !== false && holdFullReads) await fullReadsHeld;
        return answer;
      },
    };
    const realtime = new StopsRealtimeHarness();
    const stopsCache = new MemoryStopsCache();
    const stops = new DriverStopsController({
      baseUrl: "https://api.synthetic.test",
      session,
      api: stopsApi,
      cache: stopsCache,
      realtimeFactory: realtime,
      telemetry: {
        loadCompleted: vi.fn(),
        loadFailed: vi.fn(),
        realtimeStateChanged: vi.fn(),
      },
      now: () => new Date("2026-07-26T12:01:00.000Z"),
    });
    await stops.start();

    // The reconnect read leaves now, while the stop is ASSIGNED at version 3.
    holdFullReads = true;
    const staleRead = realtime.callbacks!.resynchronizeFromRest();

    const queue = new MemoryQueue([operation("CHECK_IN", 3)]);
    const page = trackedPage();
    let shownStops: readonly DriverStop[] = stops.current.stops;
    let shownOperations: readonly DriverOfflineOperation[] | null = null;
    const shown: string[] = [];
    const render = () => {
      if (shownOperations === null) return;
      const [stop] = projectDriverOperations(shownStops, shownOperations).stops;
      shown.push(`${stop.projectedStatus}/attention=${stop.attentionCount}`);
    };
    stops.subscribe((state) => {
      shownStops = state.stops;
      render();
    });
    const operations = new DriverOperationsController({
      session,
      queue,
      api: {
        // The server applies the check-in: AT_PICKUP at version 4.
        transitionOrder: vi.fn(async () => {
          server = { status: "AT_PICKUP", version: 4 };
          return { id: orderId, status: "AT_PICKUP" as const, version: 4 };
        }),
        createProofUploadSession: vi.fn(),
        finalizeProof: vi.fn(),
        recordCodCollection: vi.fn(),
        openFailedDeliveryIncident: vi.fn(),
      } as DriverSyncApi,
      refreshStops: (signal) => stops.refreshForSync(signal),
      onAccessRevoked: vi.fn(),
      schedulerFactory: (options) =>
        new DriverSyncScheduler({ ...options, window: page.window }),
      now: () => new Date("2026-07-26T12:01:00.000Z"),
      randomUuid: () => replacementId,
    });
    operations.subscribe((state) => {
      if (state.loading) return;
      shownOperations = state.operations;
      render();
    });
    await operations.start();
    await vi.waitFor(() => expect(queue.operations).toEqual([]));
    await vi.waitFor(() =>
      expect(shown.at(-1)).toBe("AT_PICKUP/attention=0"),
    );
    // "Confirmar recolección" is on screen: from here the driver may be
    // previewing the pickup photo. The stale read lands now.
    const fromPhotoStep = shown.length - 1;
    releaseFullReads!();
    await staleRead;
    const saved = stopsCache
      .onlySnapshot()!
      .stops.map((stop) => `${stop.status}/v${stop.aggregate_version}`);
    await operations.dispose();
    await stops.dispose();
    return { shown: shown.slice(fromPhotoStep), saved };
  }

  it("never takes the stop back to ASSIGNED once AT_PICKUP version 4 was read", async () => {
    const { shown, saved } = await shownAroundStaleRead();

    expect(shown).toEqual(shown.map(() => "AT_PICKUP/attention=0"));
    expect(saved).toEqual(["AT_PICKUP/v4"]);
  });
});

describe("driver operations OPS-003 expiry notice", () => {
  it("tells the driver in Spanish that the action expired after 72 hours", () => {
    expect(driverOperationExpiredMessage(["DELIVERY_PROOF"])).toBe(
      "La acción «Confirmar entrega» venció: pasaron más de 72 horas sin " +
        "conexión y ya no se enviará. Vuelve a registrarla o repórtala a despacho.",
    );
    expect(driverOperationExpiredMessage(["CHECK_IN", "START_TRANSIT"])).toBe(
      "2 acciones guardadas vencieron: pasaron más de 72 horas sin conexión " +
        "y ya no se enviarán. Vuelve a registrarlas o repórtalas a despacho.",
    );
  });

  it("shows the notice when the scheduler drops an operation, including after a manual sync", async () => {
    const expired = operation("DELIVERY_PROOF", 8);
    const queue = new MemoryQueue([expired]);
    let schedulerOptions: DriverSyncSchedulerOptions | null = null;
    const scheduler = {
      start: vi.fn(),
      requestSync: vi.fn(async () => {
        await queue.deleteOperation({ key: partitionKey }, expired.id);
        schedulerOptions?.onOperationExpired?.(expired);
      }),
      dispose: vi.fn().mockResolvedValue(undefined),
    } as unknown as DriverSyncScheduler;
    const controller = new DriverOperationsController({
      session,
      queue,
      api: {
        transitionOrder: vi.fn(),
        createProofUploadSession: vi.fn(),
        finalizeProof: vi.fn(),
        recordCodCollection: vi.fn(),
        openFailedDeliveryIncident: vi.fn(),
      } as DriverSyncApi,
      refreshStops: async () => stopList([]),
      onAccessRevoked: vi.fn(),
      schedulerFactory: (options) => {
        schedulerOptions = options;
        return scheduler;
      },
    });
    await controller.start();

    await controller.syncNow();

    expect(controller.current.operations).toEqual([]);
    expect(controller.current.message).toBe(
      driverOperationExpiredMessage(["DELIVERY_PROOF"]),
    );

    await controller.syncNow();
    expect(controller.current.message).toBe(
      driverOperationExpiredMessage(["DELIVERY_PROOF"]),
    );

    // Background passes (online, timer) report only their own expirations.
    const options = schedulerOptions as DriverSyncSchedulerOptions | null;
    options?.onSyncPassStarted?.();
    options?.onOperationExpired?.({ ...expired, kind: "CHECK_IN" });
    expect(controller.current.message).toBe(
      driverOperationExpiredMessage(["CHECK_IN"]),
    );
    options?.onSyncPassStarted?.();
    options?.onOperationExpired?.(expired);
    options?.onOperationExpired?.({ ...expired, kind: "START_TRANSIT" });
    expect(controller.current.message).toBe(
      driverOperationExpiredMessage(["DELIVERY_PROOF", "START_TRANSIT"]),
    );
    await controller.dispose();
  });
});

function operation(
  kind: Parameters<typeof createDriverOfflineOperation>[0]["kind"],
  expectedVersion: number,
): DriverOfflineOperation {
  return createDriverOfflineOperation({
    partitionKey,
    orderId,
    kind,
    expectedVersion,
    proof: kind.endsWith("_PROOF")
      ? {
          contentType: "image/png",
          sizeBytes: 1,
          sha256: "a".repeat(64),
        }
      : undefined,
    now: () => new Date("2026-07-26T12:00:00.000Z"),
    randomUuid: () => operationId,
  });
}

describe("cash collection at the door (UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11)", () => {
  const collection = {
    orderId,
    orderPublicId: "ORD_abcdefghijklmnopqrstuv",
    amountCents: 125_000,
    projectedStatus: "DELIVERING" as const,
    projectedVersion: 9,
  };

  it("queues exactly the stop's amount with the tracking number and phone time as reference", async () => {
    const harness = await createHarness([]);

    await expect(harness.controller.enqueueCodCollection(collection)).resolves.toBe(true);

    expect(harness.queue.operations).toHaveLength(1);
    const [operation] = harness.queue.operations;
    expect(operation).toMatchObject({
      kind: "COD_COLLECTION",
      orderId,
      expectedVersion: 9,
      clientOccurredAt: "2026-07-26T12:01:00.000Z",
      cod: {
        amountCents: 125_000,
        idempotencyKey: `drv2-${replacementId}-cod`,
      },
    });
    expect(operation.cod?.reference).toMatch(
      /^ORD_abcdefghijklmnopqrstuv \d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2}$/,
    );
    expect(harness.controller.current.message).toBe(
      "Cobro guardado. Se registrará cuando haya conexión.",
    );
    expect(harness.scheduler.requestSync).toHaveBeenCalledWith(false);
    await harness.controller.dispose();
  });

  it("never queues a second collection for the same order", async () => {
    const harness = await createHarness([]);
    await harness.controller.enqueueCodCollection(collection);

    await expect(harness.controller.enqueueCodCollection(collection)).resolves.toBe(false);

    expect(harness.queue.operations).toHaveLength(1);
    await harness.controller.dispose();
  });

  it("refuses a stop that is not at the door yet", async () => {
    const harness = await createHarness([]);

    await expect(
      harness.controller.enqueueCodCollection({ ...collection, projectedStatus: "IN_TRANSIT" }),
    ).resolves.toBe(false);

    expect(harness.queue.operations).toEqual([]);
    expect(harness.controller.current.message).toBe(
      "Actualiza la parada antes de registrar el cobro.",
    );
    await harness.controller.dispose();
  });

  it("retries a held collection while the stop is still at the door, whatever its version", async () => {
    const held = {
      ...createDriverOfflineOperation({
        partitionKey,
        orderId,
        kind: "COD_COLLECTION",
        expectedVersion: 9,
        cod: { amountCents: 125_000, reference: "ORD_X 2026-07-26T05:00:00-07:00" },
        now: () => new Date("2026-07-26T12:00:00.000Z"),
        randomUuid: () => operationId,
      }),
      status: "NEEDS_ATTENTION" as const,
      safeError: "VERSION_CONFLICT" as const,
      rejectionCode: "CONFLICT" as const,
    };
    const harness = await createHarness(held);

    await harness.controller.retrySame(operationId, "DELIVERING", 12);

    expect(harness.queue.operations[0]).toMatchObject({
      id: operationId,
      status: "PENDING",
      safeError: null,
      rejectionCode: null,
      cod: held.cod,
    });
    await harness.controller.rebuildForCurrentVersion(operationId, "DELIVERING", 12);
    expect(harness.queue.operations.map((value) => value.id)).toEqual([operationId]);
    expect(harness.controller.current.message).toBe(
      "Reintenta el cobro o descártalo y vuelve a registrarlo.",
    );
    await harness.controller.dispose();
  });

  it("after discarding a held collection the delivery waits for the cash and a new collection goes first", async () => {
    const held = {
      ...createDriverOfflineOperation({
        partitionKey,
        orderId,
        kind: "COD_COLLECTION",
        expectedVersion: 9,
        cod: { amountCents: 125_000, reference: "ORD_X 2026-07-26T05:00:00-07:00" },
        now: () => new Date("2026-07-26T12:00:00.000Z"),
        randomUuid: () => operationId,
      }),
      status: "NEEDS_ATTENTION" as const,
      safeError: "VERSION_CONFLICT" as const,
      rejectionCode: "COD_AMOUNT_MISMATCH" as const,
    };
    const deliveryId = "66666666-6666-4666-8666-666666666666";
    const delivery = {
      ...createDriverOfflineOperation({
        partitionKey,
        orderId,
        kind: "DELIVERY_PROOF",
        expectedVersion: 9,
        proof: { contentType: "image/png", sizeBytes: 1, sha256: "a".repeat(64) },
        now: () => new Date("2026-07-26T12:00:30.000Z"),
        randomUuid: () => deliveryId,
      }),
      status: "BLOCKED" as const,
    };
    const atTheDoor = withStopDetails({
      order_id: orderId,
      aggregate_version: 9,
      order_public_id: collection.orderPublicId,
      stop_type: "DELIVERY",
      status: "DELIVERING",
      address_summary: "Zona sintética",
      cod: { expected: { currency: "MXN", amount_cents: 125_000 }, recorded: false },
    });
    const harness = await createHarness([held, delivery]);

    await harness.controller.discard(operationId, "DELIVERING", 9);

    expect(harness.queue.operations).toEqual([
      expect.objectContaining({ id: deliveryId, status: "PENDING", safeError: null }),
    ]);
    const waiting = projectDriverOperations([atTheDoor], harness.queue.operations);
    expect(waiting.stops[0]).toMatchObject({
      projectedStatus: "DELIVERING",
      projectedVersion: 9,
      attentionCount: 0,
      codCollection: "pending",
      deliveryAwaitingCash: true,
    });

    // "Recibí" again: the collection is newer than the delivery but goes first.
    await expect(
      harness.controller.enqueueCodCollection({
        ...collection,
        projectedStatus: waiting.stops[0].projectedStatus,
        projectedVersion: waiting.stops[0].projectedVersion,
      }),
    ).resolves.toBe(true);
    const ready = projectDriverOperations([atTheDoor], harness.queue.operations);
    expect(ready.stops[0]).toMatchObject({
      projectedStatus: "DELIVERED",
      projectedVersion: 10,
      codCollection: "queued",
      deliveryAwaitingCash: false,
    });
    expect(ready.operations.map((value) => value.kind)).toEqual([
      "COD_COLLECTION",
      "DELIVERY_PROOF",
    ]);
    await harness.controller.dispose();
  });
});

describe("dispose while start() still waits (session changed or ended, React StrictMode)", () => {
  // Only the timers are fake: promises, setImmediate and the partition key's
  // digest run for real.
  const useFakeTimers = () =>
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval"] });

  it("stops after the partition key: no queue read, scheduler, listener, timer, lease or expiry", async () => {
    useFakeTimers();
    try {
      const harness = startHarness();
      // start() runs until its first wait (the partition key); dispose comes
      // right then, as when the first StrictMode mount is unmounted.
      const starting = harness.controller.start();
      await harness.controller.dispose();
      await starting;
      await harness.settle();

      expect.soft(harness.listOperations).not.toHaveBeenCalled();
      harness.expectNothingOutlivedDispose();
    } finally {
      vi.useRealTimers();
    }
  });

  it("stops after the queue read: no scheduler, listener, timer, lease or expiry", async () => {
    useFakeTimers();
    try {
      const harness = startHarness();
      const reading = deferred();
      const release = deferred();
      harness.listOperations.mockImplementationOnce(async () => {
        reading.resolve();
        await release.promise;
        return Object.freeze([...harness.queue.operations]);
      });
      const starting = harness.controller.start();
      await reading.promise;
      await harness.controller.dispose();
      release.resolve();
      await starting;
      await harness.settle();

      expect.soft(harness.listOperations).toHaveBeenCalledOnce();
      harness.expectNothingOutlivedDispose();
    } finally {
      vi.useRealTimers();
    }
  });

  it("still starts, syncs and disposes as before when dispose comes after start", async () => {
    useFakeTimers();
    try {
      const harness = startHarness();
      await harness.controller.start();
      await harness.settle();

      // The same setup does all of it while alive: one scheduler, its two
      // listeners, the lease, the 72-hour rule (with the notice) and the
      // backoff timer of the other order's action.
      expect(harness.schedulerFactory).toHaveBeenCalledOnce();
      expect(harness.page.liveListeners()).toEqual(["online", "visibilitychange"]);
      expect(harness.acquireLease).toHaveBeenCalled();
      expect(harness.queue.operations).toEqual([harness.retry]);
      expect(harness.controller.current.message).toBe(
        driverOperationExpiredMessage(["CHECK_IN"]),
      );
      expect(vi.getTimerCount()).toBe(1);

      await harness.controller.dispose();
      expect(harness.page.liveListeners()).toEqual([]);
      expect(vi.getTimerCount()).toBe(0);
    } finally {
      vi.useRealTimers();
    }
  });
});

function deferred(): { readonly promise: Promise<void>; resolve(): void } {
  let resolve: () => void = () => undefined;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve: () => resolve() };
}

/**
 * A controller over a queue holding an action held for the driver past the
 * OPS-003 window (a live scheduler takes the lease and drops it on its first
 * pass) and, on another order, an action backing off (a live scheduler keeps
 * a timer for it), with a real DriverSyncScheduler on a page whose listeners
 * are counted.
 */
function startHarness() {
  const page = trackedPage();
  const stale = {
    ...operation("CHECK_IN", 3),
    status: "NEEDS_ATTENTION" as const,
    safeError: "VERSION_CONFLICT" as const,
  };
  const retry = {
    ...createDriverOfflineOperation({
      partitionKey,
      orderId: "77777777-7777-4777-8777-777777777777",
      kind: "CHECK_IN",
      expectedVersion: 2,
      now: () => new Date("2026-07-30T11:00:00.000Z"),
      randomUuid: () => replacementId,
    }),
    status: "RETRY_WAIT" as const,
    attemptCount: 1,
    nextAttemptAt: "2026-07-30T12:05:00.000Z",
  };
  const queue = new MemoryQueue([stale, retry]);
  const listOperations = vi.spyOn(queue, "listOperations");
  const acquireLease = vi.spyOn(queue, "acquireLease");
  const deleteOperation = vi.spyOn(queue, "deleteOperation");
  const refreshStops = vi.fn(async () => stopList([]));
  const schedulerFactory = vi.fn(
    (options: DriverSyncSchedulerOptions) =>
      new DriverSyncScheduler({ ...options, window: page.window }),
  );
  const controller = new DriverOperationsController({
    session,
    queue,
    api: {
      transitionOrder: vi.fn(),
      createProofUploadSession: vi.fn(),
      finalizeProof: vi.fn(),
      recordCodCollection: vi.fn(),
      openFailedDeliveryIncident: vi.fn(),
    } as DriverSyncApi,
    refreshStops,
    onAccessRevoked: vi.fn(),
    schedulerFactory,
    // Four days after the held action was captured.
    now: () => new Date("2026-07-30T12:00:00.000Z"),
    randomUuid: () => replacementId,
  });
  /** Lets anything still alive react: a macrotask, the connection and the page coming back, and 20 s of timers. */
  const settle = async () => {
    await new Promise<void>((done) => setImmediate(done));
    page.wake();
    await new Promise<void>((done) => setImmediate(done));
    await vi.advanceTimersByTimeAsync(20_000);
    await new Promise<void>((done) => setImmediate(done));
  };
  /** Soft: a leak reports every effect it had, not only the first. */
  const expectNothingOutlivedDispose = () => {
    expect.soft(schedulerFactory).not.toHaveBeenCalled();
    expect.soft(page.liveListeners()).toEqual([]);
    expect.soft(vi.getTimerCount()).toBe(0);
    expect.soft(acquireLease).not.toHaveBeenCalled();
    expect.soft(deleteOperation).not.toHaveBeenCalled();
    expect.soft(refreshStops).not.toHaveBeenCalled();
    expect.soft(queue.operations).toEqual([stale, retry]);
  };
  return {
    controller,
    queue,
    page,
    retry,
    listOperations,
    acquireLease,
    schedulerFactory,
    settle,
    expectNothingOutlivedDispose,
  };
}

interface TrackedPage {
  readonly window: NonNullable<DriverSyncSchedulerOptions["window"]>;
  /** The event types that still have a listener, on the window or its document. */
  liveListeners(): string[];
  /** `online` on the window and `visibilitychange` on the (visible) document. */
  wake(): void;
}

function trackedPage(): TrackedPage {
  const live: { readonly target: EventTarget; readonly type: string; readonly listener: unknown }[] = [];
  const tracked = <T extends EventTarget>(target: T) =>
    Object.assign(target, {
      addEventListener(type: string, listener: EventListenerOrEventListenerObject | null) {
        live.push({ target, type, listener });
        EventTarget.prototype.addEventListener.call(target, type, listener);
      },
      removeEventListener(type: string, listener: EventListenerOrEventListenerObject | null) {
        const index = live.findIndex(
          (entry) => entry.target === target && entry.type === type && entry.listener === listener,
        );
        if (index >= 0) live.splice(index, 1);
        EventTarget.prototype.removeEventListener.call(target, type, listener);
      },
    });
  const document = tracked(
    Object.assign(new EventTarget(), { visibilityState: "visible" as DocumentVisibilityState }),
  );
  const page = tracked(
    Object.assign(new EventTarget(), { navigator: { onLine: true }, document }),
  );
  return {
    window: page as unknown as TrackedPage["window"],
    liveListeners: () => live.map((entry) => entry.type).sort(),
    wake() {
      page.dispatchEvent(new Event("online"));
      document.dispatchEvent(new Event("visibilitychange"));
    },
  };
}

async function createHarness(
  initial: DriverOfflineOperation | readonly DriverOfflineOperation[],
  withProof = false,
) {
  const initialOperations = Array.isArray(initial) ? initial : [initial];
  const queue = new MemoryQueue(initialOperations);
  if (withProof) {
    const proofOperation = initialOperations[0];
    queue.proofs.set(proofOperation.id, {
      schemaVersion: 1,
      partitionKey,
      operationId: proofOperation.id,
      blob: new Blob([new Uint8Array([1])], { type: "image/png" }),
      contentType: "image/png",
      sizeBytes: 1,
      sha256: "a".repeat(64),
    });
  }
  const scheduler = {
    start: vi.fn(),
    requestSync: vi.fn().mockResolvedValue(undefined),
    dispose: vi.fn().mockResolvedValue(undefined),
    pause: vi.fn().mockResolvedValue(undefined),
    resume: vi.fn(),
  } as unknown as DriverSyncScheduler;
  const controller = new DriverOperationsController({
    session,
    queue,
    api: {
      transitionOrder: vi.fn(),
      createProofUploadSession: vi.fn(),
      finalizeProof: vi.fn(),
      recordCodCollection: vi.fn(),
      openFailedDeliveryIncident: vi.fn(),
    } as DriverSyncApi,
    refreshStops: async () => stopList([]),
    onAccessRevoked: vi.fn(),
    schedulerFactory: () => scheduler,
    now: () => new Date("2026-07-26T12:01:00.000Z"),
    randomUuid: () => replacementId,
  });
  await controller.start();
  return { controller, queue, scheduler };
}

class MemoryQueue implements DriverOfflineQueue {
  public operations: DriverOfflineOperation[];
  public proofs = new Map<string, DriverProofBlobRecord>();

  public constructor(operations: DriverOfflineOperation[]) {
    this.operations = [...operations];
  }

  public async listOperations(): Promise<readonly DriverOfflineOperation[]> {
    // A fresh frozen list, like IndexedDbDriverOfflineQueue: no aliasing with the store.
    return Object.freeze([...this.operations]);
  }

  public async readProof(
    _partition: DriverCachePartition,
    operationIdValue: string,
  ): Promise<DriverProofBlobRecord | null> {
    return this.proofs.get(operationIdValue) ?? null;
  }

  public async enqueue(
    partition: DriverCachePartition,
    value: DriverOfflineOperation,
    proof?: ValidatedDriverProof,
  ): Promise<void> {
    this.operations.push(value);
    if (proof) {
      this.proofs.set(value.id, {
        schemaVersion: 1,
        partitionKey: partition.key,
        operationId: value.id,
        blob: proof.blob,
        contentType: proof.contentType,
        sizeBytes: proof.sizeBytes,
        sha256: proof.sha256,
      });
    }
  }

  public async replaceOperation(
    _partition: DriverCachePartition,
    value: DriverOfflineOperation,
  ): Promise<void> {
    this.operations = this.operations.map((candidate) =>
      candidate.id === value.id ? value : candidate,
    );
  }

  public async replaceOperationAndDeleteProof(
    partition: DriverCachePartition,
    value: DriverOfflineOperation,
  ): Promise<void> {
    await this.replaceOperation(partition, value);
    this.proofs.delete(value.id);
  }

  public async deleteOperation(
    _partition: DriverCachePartition,
    operationIdValue: string,
  ): Promise<void> {
    this.operations = this.operations.filter(
      (candidate) => candidate.id !== operationIdValue,
    );
    this.proofs.delete(operationIdValue);
  }

  public async rebuildOperation(
    _partition: DriverCachePartition,
    replacedOperationId: string,
    value: DriverOfflineOperation,
  ): Promise<void> {
    const proof = this.proofs.get(replacedOperationId);
    await this.deleteOperation({ key: partitionKey }, replacedOperationId);
    this.operations.push(value);
    if (proof) {
      this.proofs.set(value.id, { ...proof, operationId: value.id });
    }
  }

  public async clearPartition(): Promise<void> {
    this.operations = [];
    this.proofs.clear();
  }

  public async acquireLease(): Promise<boolean> {
    return true;
  }

  public async renewLease(): Promise<boolean> {
    return true;
  }

  public async releaseLease(): Promise<void> {
    return;
  }
}

class MemoryStopsCache implements DriverStopsCache {
  private readonly records = new Map<string, DriverStopsSnapshot>();

  public onlySnapshot(): DriverStopsSnapshot | null {
    return [...this.records.values()][0] ?? null;
  }

  public async readSnapshot(
    partition: DriverCachePartition,
  ): Promise<DriverStopsSnapshot | null> {
    return this.records.get(partition.key) ?? null;
  }

  public async replaceSnapshot(
    partition: DriverCachePartition,
    snapshot: DriverStopsSnapshot,
  ): Promise<void> {
    this.records.set(partition.key, structuredClone(snapshot));
  }

  public async clearPartition(partition: DriverCachePartition): Promise<void> {
    this.records.delete(partition.key);
  }
}

class StopsRealtimeHarness implements DriverStopsRealtimeFactory {
  public callbacks: DriverStopsRealtimeCallbacks | null = null;

  public create(
    _baseUrl: string,
    _session: DriverSession,
    callbacks: DriverStopsRealtimeCallbacks,
  ) {
    this.callbacks = callbacks;
    return { start: async () => undefined, stop: async () => undefined };
  }
}
