import { describe, expect, it, vi } from "vitest";
import type { DriverCachePartition } from "../cache/driver-stops-cache";
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
import type { ValidatedDriverProof } from "../proofs/proof-file";
import type { DriverSession } from "../session/driver-session";
import {
  DriverOperationsController,
  driverOperationExpiredMessage,
} from "./driver-operations-controller";

const partitionKey = "E".repeat(43);
const orderId = "11111111-1111-4111-8111-111111111111";
const operationId = "22222222-2222-4222-8222-222222222222";
const replacementId = "33333333-3333-4333-8333-333333333333";
const session: DriverSession = {
  organizationId: "44444444-4444-4444-8444-444444444444",
  cacheNamespace: "opaque-controller-test",
  getAccessToken: () => "memory-only",
};

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
      } as DriverSyncApi,
      refreshStops: async () => [],
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
  } as unknown as DriverSyncScheduler;
  const controller = new DriverOperationsController({
    session,
    queue,
    api: {
      transitionOrder: vi.fn(),
      createProofUploadSession: vi.fn(),
      finalizeProof: vi.fn(),
    } as DriverSyncApi,
    refreshStops: async () => [],
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
