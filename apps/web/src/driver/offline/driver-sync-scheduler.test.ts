import { describe, expect, it, vi } from "vitest";
import type { DriverCachePartition } from "../cache/driver-stops-cache";
import type { DriverStop } from "../contracts/driver-stop";
import type { DriverOfflineQueue } from "./driver-offline-queue";
import {
  DriverSyncApiError,
  type DriverSyncApi,
} from "./driver-sync-api";
import {
  createDriverOfflineOperation,
  type DriverOfflineOperation,
  type DriverProofBlobRecord,
} from "./operation-contract";
import {
  DriverSyncBackoffMilliseconds,
  DriverSyncScheduler,
  driverProofSizeBucket,
  selectRunnableOperation,
} from "./driver-sync-scheduler";
import { OfflineOperationMaximumAgeMilliseconds } from "./offline-operation-age";

const partition: DriverCachePartition = { key: "D".repeat(43) };
const orderA = "11111111-1111-4111-8111-111111111111";
const orderB = "22222222-2222-4222-8222-222222222222";

describe("driver sync scheduler", () => {
  it("uses the bounded canonical retry schedule", () => {
    expect(DriverSyncBackoffMilliseconds).toEqual([
      1_000, 2_000, 5_000, 10_000, 30_000,
    ]);
  });

  it.each([
    [0, "0-256KB"],
    [256 * 1024, "0-256KB"],
    [256 * 1024 + 1, "256KB-1MB"],
    [1024 * 1024, "256KB-1MB"],
    [1024 * 1024 + 1, "1-5MB"],
    [5 * 1024 * 1024, "1-5MB"],
    [5 * 1024 * 1024 + 1, "5-10MB"],
    [10 * 1024 * 1024, "5-10MB"],
  ])("reports only the bounded proof size bucket for %i bytes", (size, bucket) => {
    expect(driverProofSizeBucket(size)).toBe(bucket);
  });

  it("does not let one waiting order block another order", () => {
    const now = new Date("2026-07-26T12:00:00.000Z");
    const waiting = {
      ...operation("CHECK_IN", orderA, 1, 1),
      status: "WAITING_VALIDATION" as const,
      nextAttemptAt: "2026-07-26T12:00:30.000Z",
    };
    const ready = operation("START_DELIVERY", orderB, 7, 2);
    expect(selectRunnableOperation([waiting, ready], now, false)?.id).toBe(
      ready.id,
    );
    expect(selectRunnableOperation([waiting, ready], now, true)?.id).toBe(
      waiting.id,
    );
  });

  it("checks REST before resending a transition after a lost response", async () => {
    const queued = operation("CHECK_IN", orderA, 3, 1);
    const queue = new MemoryQueue([queued]);
    const transitionOrder = vi.fn();
    const stop: DriverStop = {
      order_id: orderA,
      aggregate_version: 4,
      order_public_id: "ORD_SYNTHETIC",
      stop_type: "PICKUP",
      status: "AT_PICKUP",
      address_summary: "Zona sintética",
    };
    const scheduler = new DriverSyncScheduler({
      partition,
      queue,
      api: {
        transitionOrder,
        createProofUploadSession: vi.fn(),
        finalizeProof: vi.fn(),
      } as DriverSyncApi,
      refreshStops: async () => [stop],
      onQueueChanged: vi.fn(),
      onAccessRevoked: vi.fn(),
      now: () => new Date("2026-07-26T12:00:00.000Z"),
      randomUuid: () => "99999999-9999-4999-8999-999999999999",
    });
    await scheduler.requestSync(true);
    expect(transitionOrder).not.toHaveBeenCalled();
    expect(queue.operations).toEqual([]);
    await scheduler.dispose();
  });

  it("polls REST with backoff without resending an accepted transition", async () => {
    const queued = {
      ...operation("CHECK_IN", orderA, 3, 1),
      status: "AWAITING_REST_CONFIRMATION" as const,
    };
    const queue = new MemoryQueue([queued]);
    const transitionOrder = vi.fn();
    const scheduler = schedulerFor(queue, { transitionOrder });

    await scheduler.requestSync(true);

    expect(transitionOrder).not.toHaveBeenCalled();
    expect(queue.operations[0]).toMatchObject({
      status: "AWAITING_REST_CONFIRMATION",
      attemptCount: 1,
      nextAttemptAt: "2026-07-26T12:00:01.000Z",
    });
    await scheduler.dispose();
  });

  it.each([
    ["unauthorized", "unauthorized"],
    ["forbidden", "forbidden"],
  ] as const)("clears the active partition on %s", async (category, revoked) => {
    const queued = operation("CHECK_IN", orderA, 3, 1);
    const queue = new MemoryQueue([queued]);
    const onAccessRevoked = vi.fn();
    const scheduler = schedulerFor(queue, {
      transitionOrder: vi
        .fn()
        .mockRejectedValue(new DriverSyncApiError(category)),
    }, onAccessRevoked);

    await scheduler.requestSync(true);

    expect(queue.operations).toEqual([]);
    expect(queue.clearCount).toBe(1);
    expect(onAccessRevoked).toHaveBeenCalledWith(revoked);
    await scheduler.dispose();
  });

  it("purges a missing resource without sending or probing another API", async () => {
    const queued = operation("DELIVERY_PROOF", orderA, 8, 1);
    const queue = new MemoryQueue([queued]);
    const transitionOrder = vi.fn();
    const scheduler = schedulerFor(
      queue,
      { transitionOrder },
      vi.fn(),
      async () => [],
    );

    await scheduler.requestSync(true);

    expect(queue.operations).toEqual([]);
    expect(transitionOrder).not.toHaveBeenCalled();
    await scheduler.dispose();
  });

  it("defers recoverable failures with the first bounded delay", async () => {
    const queued = operation("CHECK_IN", orderA, 3, 1);
    const queue = new MemoryQueue([queued]);
    const scheduler = schedulerFor(queue, {
      transitionOrder: vi
        .fn()
        .mockRejectedValue(new DriverSyncApiError("recoverable")),
    });

    await scheduler.requestSync(true);

    expect(queue.operations[0]).toMatchObject({
      status: "RETRY_WAIT",
      attemptCount: 1,
      nextAttemptAt: "2026-07-26T12:00:01.000Z",
      safeError: "NETWORK",
    });
    await scheduler.dispose();
  });

  it("marks a conflict and blocks only dependent operations", async () => {
    const first = operation("CHECK_IN", orderA, 3, 1);
    const dependent = operation("PICKUP_PROOF", orderA, 4, 2);
    const independent = operation("START_DELIVERY", orderB, 8, 3);
    const queue = new MemoryQueue([first, dependent, independent]);
    let orderBConfirmed = false;
    const transitionOrder = vi
      .fn()
      .mockRejectedValueOnce(new DriverSyncApiError("conflict"))
      .mockImplementation(async () => {
        orderBConfirmed = true;
        return {
          id: orderB,
          status: "DELIVERING",
          version: 9,
        };
      });
    const scheduler = schedulerFor(
      queue,
      { transitionOrder },
      vi.fn(),
      async () => [
        {
          order_id: orderA,
          aggregate_version: 3,
          order_public_id: "ORD_A",
          stop_type: "PICKUP",
          status: "ASSIGNED",
          address_summary: "Zona A",
        },
        {
          order_id: orderB,
          aggregate_version: orderBConfirmed ? 9 : 8,
          order_public_id: "ORD_B",
          stop_type: "DELIVERY",
          status: orderBConfirmed ? "DELIVERING" : "IN_TRANSIT",
          address_summary: "Zona B",
        },
      ],
    );

    await scheduler.requestSync(true);

    expect(queue.operations.find((candidate) => candidate.id === first.id))
      .toMatchObject({ status: "NEEDS_ATTENTION" });
    expect(queue.operations.find((candidate) => candidate.id === dependent.id))
      .toMatchObject({ status: "BLOCKED" });
    expect(transitionOrder).toHaveBeenCalledTimes(2);
    await scheduler.dispose();
  });

  it("honors an existing lease and never sends from a second tab", async () => {
    const queue = new MemoryQueue([operation("CHECK_IN", orderA, 3, 1)]);
    queue.leaseAvailable = false;
    const transitionOrder = vi.fn();
    const scheduler = schedulerFor(queue, { transitionOrder });

    await scheduler.requestSync(true);

    expect(transitionOrder).not.toHaveBeenCalled();
    expect(queue.operations).toHaveLength(1);
    await scheduler.dispose();
  });

  it("renews the partition lease while network work remains active", async () => {
    vi.useFakeTimers();
    try {
      const queue = new MemoryQueue([operation("CHECK_IN", orderA, 3, 1)]);
      const transitionOrder = vi.fn(
        (_operation: DriverOfflineOperation, signal?: AbortSignal) =>
          new Promise<never>((_resolve, reject) => {
            signal?.addEventListener(
              "abort",
              () => reject(new DriverSyncApiError("cancelled")),
              { once: true },
            );
          }),
      );
      const scheduler = new DriverSyncScheduler({
        partition,
        queue,
        api: {
          transitionOrder,
          createProofUploadSession: vi.fn(),
          finalizeProof: vi.fn(),
        },
        refreshStops: async () => [{
          order_id: orderA,
          aggregate_version: 3,
          order_public_id: "ORD_A",
          stop_type: "PICKUP",
          status: "ASSIGNED",
          address_summary: "Zona A",
        }],
        onQueueChanged: vi.fn(),
        onAccessRevoked: vi.fn(),
        now: () => new Date("2026-07-26T12:00:00.000Z"),
        randomUuid: () => "99999999-9999-4999-8999-999999999999",
        leaseMilliseconds: 3_000,
      });

      const active = scheduler.requestSync(true);
      await vi.advanceTimersByTimeAsync(1_000);

      expect(transitionOrder).toHaveBeenCalledOnce();
      expect(queue.renewCount).toBeGreaterThanOrEqual(2);
      await scheduler.dispose();
      await active;
      expect(queue.releaseCount).toBeGreaterThanOrEqual(1);
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("OPS-003 offline operation expiry", () => {
  const captured = "2026-07-26T12:00:01.000Z";
  const justInside = new Date(
    Date.parse(captured) + OfflineOperationMaximumAgeMilliseconds,
  );
  const justOutside = new Date(justInside.getTime() + 1);

  it("replays the same capture instant on every attempt", async () => {
    const queue = new MemoryQueue([operation("CHECK_IN", orderA, 3, 1)]);
    const seen: string[] = [];
    const transitionOrder = vi.fn(async (value: DriverOfflineOperation) => {
      seen.push(value.clientOccurredAt);
      throw new DriverSyncApiError("recoverable");
    });
    const scheduler = expiryScheduler(queue, { transitionOrder }, justInside);

    await scheduler.requestSync(true);
    await scheduler.requestSync(true);

    expect(seen).toEqual([captured, captured]);
    expect(queue.operations[0]).toMatchObject({
      clientOccurredAt: captured,
      status: "RETRY_WAIT",
    });
    await scheduler.dispose();
  });

  it("still sends an operation exactly at the maximum age", async () => {
    const queue = new MemoryQueue([operation("CHECK_IN", orderA, 3, 1)]);
    const transitionOrder = vi
      .fn()
      .mockRejectedValue(new DriverSyncApiError("recoverable"));
    const onOperationExpired = vi.fn();
    const scheduler = expiryScheduler(
      queue,
      { transitionOrder },
      justInside,
      onOperationExpired,
    );

    await scheduler.requestSync(true);

    expect(transitionOrder).toHaveBeenCalledOnce();
    expect(onOperationExpired).not.toHaveBeenCalled();
    expect(queue.operations).toHaveLength(1);
    await scheduler.dispose();
  });

  it("drops an operation past the maximum age locally without sending it", async () => {
    const queued = operation("CHECK_IN", orderA, 3, 1);
    const queue = new MemoryQueue([queued]);
    const transitionOrder = vi.fn();
    const onOperationExpired = vi.fn();
    const scheduler = expiryScheduler(
      queue,
      { transitionOrder },
      justOutside,
      onOperationExpired,
    );

    await scheduler.requestSync(true);

    expect(transitionOrder).not.toHaveBeenCalled();
    expect(queue.operations).toEqual([]);
    expect(onOperationExpired).toHaveBeenCalledExactlyOnceWith(
      expect.objectContaining({ id: queued.id }),
    );
    await scheduler.dispose();
  });

  it("confirms by REST before expiring an action the server already applied", async () => {
    const queue = new MemoryQueue([operation("CHECK_IN", orderA, 3, 1)]);
    const onOperationExpired = vi.fn();
    const scheduler = expiryScheduler(
      queue,
      {},
      justOutside,
      onOperationExpired,
      async () => [stopA("AT_PICKUP", 4)],
    );

    await scheduler.requestSync(true);

    expect(queue.operations).toEqual([]);
    expect(onOperationExpired).not.toHaveBeenCalled();
    await scheduler.dispose();
  });

  it("drops the operation permanently on 409 OFFLINE_OPERATION_EXPIRED and never retries it", async () => {
    const queued = operation("CHECK_IN", orderA, 3, 1);
    const queue = new MemoryQueue([queued]);
    const transitionOrder = vi
      .fn()
      .mockRejectedValue(
        new DriverSyncApiError("conflict", "OFFLINE_OPERATION_EXPIRED"),
      );
    const onOperationExpired = vi.fn();
    const scheduler = expiryScheduler(
      queue,
      { transitionOrder },
      justInside,
      onOperationExpired,
    );

    await scheduler.requestSync(true);
    await scheduler.requestSync(true);

    expect(queue.operations).toEqual([]);
    expect(transitionOrder).toHaveBeenCalledOnce();
    expect(onOperationExpired).toHaveBeenCalledExactlyOnceWith(
      expect.objectContaining({ id: queued.id }),
    );
    await scheduler.dispose();
  });

  it("holds the dependent chain of an expired operation for attention", async () => {
    const expired = operation("CHECK_IN", orderA, 3, 1);
    const dependent = operation("START_TRANSIT", orderA, 4, 2);
    const later = operation("START_DELIVERY", orderA, 5, 3);
    const independent = operation("START_DELIVERY", orderB, 8, 4);
    const queue = new MemoryQueue([expired, dependent, later, independent]);
    const transitionOrder = vi
      .fn()
      .mockRejectedValueOnce(
        new DriverSyncApiError("conflict", "OFFLINE_OPERATION_EXPIRED"),
      )
      .mockRejectedValue(new DriverSyncApiError("recoverable"));
    const onOperationExpired = vi.fn();
    const scheduler = expiryScheduler(
      queue,
      { transitionOrder },
      justInside,
      onOperationExpired,
    );

    await scheduler.requestSync(true);

    expect(queue.operations.map((value) => [value.id, value.status])).toEqual([
      [dependent.id, "NEEDS_ATTENTION"],
      [later.id, "BLOCKED"],
      [independent.id, "RETRY_WAIT"],
    ]);
    expect(queue.operations[0].safeError).toBe("VERSION_CONFLICT");
    expect(onOperationExpired).toHaveBeenCalledExactlyOnceWith(
      expect.objectContaining({ id: expired.id }),
    );
    expect(transitionOrder).toHaveBeenCalledTimes(2);
    await scheduler.dispose();
  });

  it("also drops dependents that are themselves past the maximum age", async () => {
    const expired = operation("CHECK_IN", orderA, 3, 1);
    const dependent = operation("START_TRANSIT", orderA, 4, 2);
    const queue = new MemoryQueue([expired, dependent]);
    const onOperationExpired = vi.fn();
    const scheduler = expiryScheduler(
      queue,
      {},
      new Date(Date.parse(dependent.clientOccurredAt) + OfflineOperationMaximumAgeMilliseconds + 1),
      onOperationExpired,
    );

    await scheduler.requestSync(true);

    expect(queue.operations).toEqual([]);
    expect(onOperationExpired.mock.calls.map(([value]) => value.id)).toEqual([
      expired.id,
      dependent.id,
    ]);
    await scheduler.dispose();
  });

  it("discards an action held for attention once it outlives the window", async () => {
    const held = {
      ...operation("CHECK_IN", orderA, 3, 1),
      status: "NEEDS_ATTENTION" as const,
      safeError: "VERSION_CONFLICT" as const,
    };
    const blocked = {
      ...operation("START_TRANSIT", orderA, 4, 2),
      status: "BLOCKED" as const,
    };
    const inside = new MemoryQueue([held, blocked]);
    const insideScheduler = expiryScheduler(inside, {}, justInside);
    await insideScheduler.requestSync(true);
    expect(inside.operations.map((value) => value.status)).toEqual([
      "NEEDS_ATTENTION",
      "BLOCKED",
    ]);
    await insideScheduler.dispose();

    const outside = new MemoryQueue([held, blocked]);
    const onOperationExpired = vi.fn();
    const transitionOrder = vi.fn();
    const outsideScheduler = expiryScheduler(
      outside,
      { transitionOrder },
      justOutside,
      onOperationExpired,
    );
    await outsideScheduler.requestSync(true);

    expect(outside.operations.map((value) => [value.id, value.status])).toEqual([
      [blocked.id, "NEEDS_ATTENTION"],
    ]);
    expect(onOperationExpired).toHaveBeenCalledExactlyOnceWith(
      expect.objectContaining({ id: held.id }),
    );
    expect(transitionOrder).not.toHaveBeenCalled();
    await outsideScheduler.dispose();
  });

  it.each(["createProofUploadSession", "finalizeProof"] as const)(
    "drops a proof operation and its local photo when %s answers OFFLINE_OPERATION_EXPIRED",
    async (step) => {
      const queued = operation("PICKUP_PROOF", orderA, 4, 1);
      const queue = new MemoryQueue([queued]);
      queue.proofs.set(queued.id, proofFor(queued));
      const expiredError = new DriverSyncApiError(
        "conflict",
        "OFFLINE_OPERATION_EXPIRED",
      );
      const api = {
        createProofUploadSession:
          step === "createProofUploadSession"
            ? vi.fn().mockRejectedValue(expiredError)
            : vi.fn().mockResolvedValue(grant()),
        finalizeProof: vi.fn().mockRejectedValue(expiredError),
        transitionOrder: vi.fn(),
      };
      const onOperationExpired = vi.fn();
      const scheduler = expiryScheduler(
        queue,
        api,
        justInside,
        onOperationExpired,
        async () => [stopA("AT_PICKUP", 4)],
      );

      await scheduler.requestSync(true);

      expect(queue.operations).toEqual([]);
      expect(queue.proofs.size).toBe(0);
      expect(api.transitionOrder).not.toHaveBeenCalled();
      expect(api.finalizeProof).toHaveBeenCalledTimes(
        step === "finalizeProof" ? 1 : 0,
      );
      expect(onOperationExpired).toHaveBeenCalledExactlyOnceWith(
        expect.objectContaining({ id: queued.id }),
      );
      await scheduler.dispose();
    },
  );

  it("keeps a transition 409 without code as a conflict for attention (clock ahead or state)", async () => {
    const queue = new MemoryQueue([operation("CHECK_IN", orderA, 3, 1)]);
    const onOperationExpired = vi.fn();
    const scheduler = expiryScheduler(
      queue,
      {
        transitionOrder: vi
          .fn()
          .mockRejectedValue(new DriverSyncApiError("conflict")),
      },
      justInside,
      onOperationExpired,
    );

    await scheduler.requestSync(true);

    expect(queue.operations[0]).toMatchObject({
      status: "NEEDS_ATTENTION",
      safeError: "VERSION_CONFLICT",
    });
    expect(onOperationExpired).not.toHaveBeenCalled();
    await scheduler.dispose();
  });

  it("keeps a proof 409 INVALID_REQUEST as rejected evidence (clock ahead)", async () => {
    const queued = operation("PICKUP_PROOF", orderA, 4, 1);
    const queue = new MemoryQueue([queued]);
    queue.proofs.set(queued.id, proofFor(queued));
    const onOperationExpired = vi.fn();
    const scheduler = expiryScheduler(
      queue,
      {
        createProofUploadSession: vi
          .fn()
          .mockRejectedValue(
            new DriverSyncApiError("conflict", "INVALID_REQUEST"),
          ),
      },
      justInside,
      onOperationExpired,
      async () => [stopA("AT_PICKUP", 4)],
    );

    await scheduler.requestSync(true);

    expect(queue.operations[0]).toMatchObject({
      id: queued.id,
      status: "NEEDS_ATTENTION",
      safeError: "EVIDENCE_REJECTED",
    });
    expect(queue.proofs.has(queued.id)).toBe(true);
    expect(onOperationExpired).not.toHaveBeenCalled();
    await scheduler.dispose();
  });
});

function expiryScheduler(
  queue: MemoryQueue,
  apiOverrides: Partial<DriverSyncApi>,
  now: Date,
  onOperationExpired: (value: DriverOfflineOperation) => void = vi.fn(),
  refreshStops: () => Promise<readonly DriverStop[]> = async () => [
    stopA("ASSIGNED", 3),
    {
      order_id: orderB,
      aggregate_version: 8,
      order_public_id: "ORD_B",
      stop_type: "DELIVERY",
      status: "IN_TRANSIT",
      address_summary: "Zona B",
    },
  ],
): DriverSyncScheduler {
  return new DriverSyncScheduler({
    partition,
    queue,
    api: {
      transitionOrder: vi.fn(),
      createProofUploadSession: vi.fn(),
      finalizeProof: vi.fn(),
      ...apiOverrides,
    },
    refreshStops,
    onQueueChanged: vi.fn(),
    onAccessRevoked: vi.fn(),
    onOperationExpired,
    now: () => now,
    randomUuid: () => "99999999-9999-4999-8999-999999999999",
    upload: vi.fn().mockResolvedValue(undefined),
  });
}

function stopA(
  status: DriverStop["status"],
  aggregateVersion: number,
): DriverStop {
  return {
    order_id: orderA,
    aggregate_version: aggregateVersion,
    order_public_id: "ORD_A",
    stop_type: "PICKUP",
    status,
    address_summary: "Zona A",
  };
}

function proofFor(value: DriverOfflineOperation): DriverProofBlobRecord {
  return {
    schemaVersion: 1,
    partitionKey: partition.key,
    operationId: value.id,
    blob: new Blob([new Uint8Array([1])], { type: "image/png" }),
    contentType: "image/png",
    sizeBytes: 1,
    sha256: "a".repeat(64),
  };
}

function grant() {
  return {
    id: "44444444-4444-4444-8444-444444444444",
    status: "CREATED",
    uploadUrl: "https://objects.synthetic.test/quarantine",
    expiresAt: "2026-07-30T00:00:00.000Z",
    requiredHeaders: { "Content-Type": "image/png" },
  };
}

function schedulerFor(
  queue: MemoryQueue,
  apiOverrides: Partial<DriverSyncApi>,
  onAccessRevoked = vi.fn(),
  refreshStops: () => Promise<readonly DriverStop[]> = async () => [
    {
      order_id: orderA,
      aggregate_version: 3,
      order_public_id: "ORD_A",
      stop_type: "PICKUP",
      status: "ASSIGNED",
      address_summary: "Zona A",
    },
    {
      order_id: orderB,
      aggregate_version: 8,
      order_public_id: "ORD_B",
      stop_type: "DELIVERY",
      status: "IN_TRANSIT",
      address_summary: "Zona B",
    },
  ],
): DriverSyncScheduler {
  const api: DriverSyncApi = {
    transitionOrder: vi.fn(),
    createProofUploadSession: vi.fn(),
    finalizeProof: vi.fn(),
    ...apiOverrides,
  };
  return new DriverSyncScheduler({
    partition,
    queue,
    api,
    refreshStops,
    onQueueChanged: vi.fn(),
    onAccessRevoked,
    now: () => new Date("2026-07-26T12:00:00.000Z"),
    randomUuid: () => "99999999-9999-4999-8999-999999999999",
  });
}

function operation(
  kind: Parameters<typeof createDriverOfflineOperation>[0]["kind"],
  orderId: string,
  expectedVersion: number,
  idTail: number,
): DriverOfflineOperation {
  return createDriverOfflineOperation({
    partitionKey: partition.key,
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
    now: () => new Date(`2026-07-26T12:00:0${idTail}.000Z`),
    randomUuid: () =>
      `00000000-0000-4000-8000-${idTail.toString().padStart(12, "0")}`,
  });
}

class MemoryQueue implements DriverOfflineQueue {
  public operations: DriverOfflineOperation[];
  public clearCount = 0;
  public leaseAvailable = true;
  public renewCount = 0;
  public releaseCount = 0;

  public constructor(operations: DriverOfflineOperation[]) {
    this.operations = [...operations];
  }

  public async listOperations(): Promise<readonly DriverOfflineOperation[]> {
    return this.operations;
  }

  public readonly proofs = new Map<string, DriverProofBlobRecord>();

  public async readProof(
    _partition: DriverCachePartition,
    operationId: string,
  ): Promise<DriverProofBlobRecord | null> {
    return this.proofs.get(operationId) ?? null;
  }

  public async enqueue(
    _partition: DriverCachePartition,
    operation: DriverOfflineOperation,
  ): Promise<void> {
    this.operations.push(operation);
  }

  public async replaceOperation(
    _partition: DriverCachePartition,
    operation: DriverOfflineOperation,
  ): Promise<void> {
    this.operations = this.operations.map((candidate) =>
      candidate.id === operation.id ? operation : candidate,
    );
  }

  public async replaceOperationAndDeleteProof(
    partitionValue: DriverCachePartition,
    operationValue: DriverOfflineOperation,
  ): Promise<void> {
    await this.replaceOperation(partitionValue, operationValue);
    this.proofs.delete(operationValue.id);
  }

  public async deleteOperation(
    _partition: DriverCachePartition,
    operationId: string,
  ): Promise<void> {
    this.operations = this.operations.filter(
      (candidate) => candidate.id !== operationId,
    );
    this.proofs.delete(operationId);
  }

  public async rebuildOperation(
    _partition: DriverCachePartition,
    replacedOperationId: string,
    operationValue: DriverOfflineOperation,
  ): Promise<void> {
    await this.deleteOperation(partition, replacedOperationId);
    this.operations.push(operationValue);
  }

  public async clearPartition(): Promise<void> {
    this.clearCount += 1;
    this.operations = [];
  }

  public async acquireLease(): Promise<boolean> {
    return this.leaseAvailable;
  }

  public async renewLease(): Promise<boolean> {
    this.renewCount += 1;
    return true;
  }

  public async releaseLease(): Promise<void> {
    this.releaseCount += 1;
    return;
  }
}
