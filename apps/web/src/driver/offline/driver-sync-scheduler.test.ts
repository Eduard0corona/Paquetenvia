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

  public async readProof(): Promise<DriverProofBlobRecord | null> {
    return null;
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
  }

  public async deleteOperation(
    _partition: DriverCachePartition,
    operationId: string,
  ): Promise<void> {
    this.operations = this.operations.filter(
      (candidate) => candidate.id !== operationId,
    );
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
