import { describe, expect, it, vi } from "vitest";
import type { DriverCachePartition } from "../cache/driver-stops-cache";
import type { DriverStop, DriverStopList } from "../contracts/driver-stop";
import { stopList, withStopDetails } from "../contracts/driver-stop.fixtures";
import type { DriverOfflineQueue } from "./driver-offline-queue";
import {
  DriverSyncApiError,
  type DriverCodReceipt,
  type DriverSyncApi,
} from "./driver-sync-api";
import { DriverSyncScheduler } from "./driver-sync-scheduler";
import {
  createDriverOfflineOperation,
  migrateDriverOfflineOperationRecord,
  parseDriverOfflineOperation,
  type DriverOfflineOperation,
  type DriverProofBlobRecord,
} from "./operation-contract";

/**
 * UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: the cash collected at the door is
 * a queued operation that records COD with one Idempotency-Key, reaches the
 * server before the delivery of the same order and never lets the delivery go
 * first.
 */
const partition: DriverCachePartition = { key: "E".repeat(43) };
const orderId = "22222222-2222-4222-8222-222222222222";
const codOperationId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const deliveryOperationId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
const now = new Date("2026-10-11T20:05:42.000Z");
const amountCents = 125_000;

describe("cash collection sync", () => {
  it("records the exact cash first, then sends the delivery, each once", async () => {
    const queue = new MemoryQueue([codOperation(), deliveryOperation()]);
    queue.proofs.set(deliveryOperationId, proofFor(deliveryOperationId));
    let recorded = false;
    let delivered = false;
    const calls: string[] = [];
    const recordCodCollection = vi.fn(async (operation: DriverOfflineOperation) => {
      calls.push(`cod:${operation.cod?.idempotencyKey}`);
      recorded = true;
      return receipt();
    });
    const transitionOrder = vi.fn(async (operation: DriverOfflineOperation) => {
      calls.push(`transition:${operation.kind}`);
      expect(recorded).toBe(true);
      delivered = true;
      return { id: orderId, status: "DELIVERED" as const, version: 10 };
    });
    const scheduler = schedulerFor(queue, {
      recordCodCollection,
      transitionOrder,
      createProofUploadSession: vi.fn().mockResolvedValue(grant()),
      finalizeProof: vi.fn(async (operation: DriverOfflineOperation) => ({
        id: "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
        proofType: "DELIVERY_PHOTO" as const,
        sha256: "a".repeat(64),
        capturedAt: operation.clientOccurredAt,
      })),
    }, async () => stopList(delivered ? [] : [deliveringStop(recorded)]));

    await scheduler.requestSync(true);

    expect(calls).toEqual([
      `cod:drv2-${codOperationId}-cod`,
      "transition:DELIVERY_PROOF",
    ]);
    expect(recordCodCollection).toHaveBeenCalledOnce();
    expect(queue.operations).toEqual([]);
    await scheduler.dispose();
  });

  it("never sends the delivery while the cash collection waits to be retried", async () => {
    const queue = new MemoryQueue([codOperation(), deliveryOperation()]);
    queue.proofs.set(deliveryOperationId, proofFor(deliveryOperationId));
    const recordCodCollection = vi
      .fn()
      .mockRejectedValue(new DriverSyncApiError("recoverable"));
    const transitionOrder = vi.fn();
    const createProofUploadSession = vi.fn();
    const scheduler = schedulerFor(queue, {
      recordCodCollection,
      transitionOrder,
      createProofUploadSession,
    });

    await scheduler.requestSync(true);

    expect(recordCodCollection).toHaveBeenCalledOnce();
    expect(transitionOrder).not.toHaveBeenCalled();
    expect(createProofUploadSession).not.toHaveBeenCalled();
    expect(queue.operations.map((operation) => operation.status)).toEqual([
      "RETRY_WAIT",
      "PENDING",
    ]);
    await scheduler.dispose();
  });

  it("replays a lost 201 with the same key and payload", async () => {
    const queue = new MemoryQueue([codOperation()]);
    let recorded = false;
    const sent: { key: string | undefined; reference: string | undefined }[] = [];
    const recordCodCollection = vi.fn(async (operation: DriverOfflineOperation) => {
      sent.push({
        key: operation.cod?.idempotencyKey,
        reference: operation.cod?.reference,
      });
      recorded = true;
      if (sent.length === 1) throw new DriverSyncApiError("recoverable");
      return receipt();
    });
    // REST still shows the cash pending after the lost response, so the same
    // key is sent again and the server replays the stored 201.
    const scheduler = schedulerFor(
      queue,
      { recordCodCollection },
      async () => stopList([deliveringStop(sent.length > 1 && recorded)]),
    );

    await scheduler.requestSync(true);
    await scheduler.requestSync(true);

    expect(sent).toHaveLength(2);
    expect(sent[0]).toEqual(sent[1]);
    expect(sent[0].key).toBe(`drv2-${codOperationId}-cod`);
    expect(queue.operations).toEqual([]);
    await scheduler.dispose();
  });

  it("drops the collection without sending when REST already shows the cash recorded", async () => {
    const queue = new MemoryQueue([codOperation()]);
    const recordCodCollection = vi.fn();
    const scheduler = schedulerFor(
      queue,
      { recordCodCollection },
      async () => stopList([deliveringStop(true)]),
    );

    await scheduler.requestSync(true);

    expect(recordCodCollection).not.toHaveBeenCalled();
    expect(queue.operations).toEqual([]);
    await scheduler.dispose();
  });

  it("accepts COD_ALREADY_RECORDED only when REST confirms exactly this cash", async () => {
    const queue = new MemoryQueue([codOperation()]);
    let checks = 0;
    const scheduler = schedulerFor(
      queue,
      {
        recordCodCollection: vi
          .fn()
          .mockRejectedValue(
            new DriverSyncApiError("conflict", "COD_ALREADY_RECORDED"),
          ),
      },
      async () => {
        checks += 1;
        return stopList([deliveringStop(checks > 1)]);
      },
    );

    await scheduler.requestSync(true);

    expect(queue.operations).toEqual([]);
    await scheduler.dispose();
  });

  it.each([
    ["COD_AMOUNT_MISMATCH", "COD_AMOUNT_MISMATCH"],
    ["COD_ALREADY_RECORDED", "COD_ALREADY_RECORDED"],
    ["ORDER_STATE_CONFLICT", "ORDER_STATE_CONFLICT"],
    ["NOT_A_FINANCE_CODE", null],
  ] as const)(
    "holds a refused collection for attention with %s and blocks the delivery behind it",
    async (code, kept) => {
      const queue = new MemoryQueue([codOperation(), deliveryOperation()]);
      queue.proofs.set(deliveryOperationId, proofFor(deliveryOperationId));
      const transitionOrder = vi.fn();
      const scheduler = schedulerFor(queue, {
        recordCodCollection: vi
          .fn()
          .mockRejectedValue(new DriverSyncApiError("conflict", code)),
        transitionOrder,
      });

      await scheduler.requestSync(true);

      expect(transitionOrder).not.toHaveBeenCalled();
      expect(queue.operations[0]).toMatchObject({
        id: codOperationId,
        status: "NEEDS_ATTENTION",
        safeError: "VERSION_CONFLICT",
        rejectionCode: kept,
      });
      expect(queue.operations[1]).toMatchObject({
        id: deliveryOperationId,
        status: "BLOCKED",
      });
      await scheduler.dispose();
    },
  );

  it("keeps the ORD-002 rule code of a refused delivery so the driver reads it", async () => {
    const queue = new MemoryQueue([deliveryOperation()]);
    queue.proofs.set(deliveryOperationId, proofFor(deliveryOperationId));
    // REST shows no cash to collect, so nothing waits for "Recibí": the
    // server's refusal is a conflict the driver reads.
    const scheduler = schedulerFor(
      queue,
      {
        createProofUploadSession: vi.fn().mockResolvedValue(grant()),
        finalizeProof: finalizeProof(),
        transitionOrder: vi
          .fn()
          .mockRejectedValue(new DriverSyncApiError("conflict", "COD_NOT_RECORDED")),
      },
      async () => stopList([withStopDetails({ ...deliveringStop(false), cod: null })]),
    );

    await scheduler.requestSync(true);

    expect(queue.operations[0]).toMatchObject({
      status: "NEEDS_ATTENTION",
      safeError: "VERSION_CONFLICT",
      rejectionCode: "COD_NOT_RECORDED",
    });
    await scheduler.dispose();
  });

  it("keeps a delivery with no collection saved waiting for the cash, unsent", async () => {
    const queue = new MemoryQueue([deliveryOperation()]);
    queue.proofs.set(deliveryOperationId, proofFor(deliveryOperationId));
    const transitionOrder = vi.fn();
    const createProofUploadSession = vi.fn();
    const refreshStops = vi.fn(async () => stopList([deliveringStop(false)]));
    const scheduler = schedulerFor(
      queue,
      { transitionOrder, createProofUploadSession },
      refreshStops,
    );

    await scheduler.requestSync(true);

    // One read per pass: the waiting delivery is not tried again in a loop.
    expect(refreshStops).toHaveBeenCalledOnce();
    expect(createProofUploadSession).not.toHaveBeenCalled();
    expect(transitionOrder).not.toHaveBeenCalled();
    expect(queue.operations).toEqual([deliveryOperation()]);
    expect(queue.proofs.has(deliveryOperationId)).toBe(true);
    await scheduler.dispose();
  });

  it("sends a collection saved again after a discard first and the waiting delivery right after it", async () => {
    // The held collection was discarded, so the delivery waited; "Recibí"
    // saved a new collection, newer than the delivery.
    const queue = new MemoryQueue([deliveryOperation()]);
    queue.proofs.set(deliveryOperationId, proofFor(deliveryOperationId));
    const harness = deliveryHarness(queue);

    await harness.scheduler.requestSync(true);
    expect(harness.calls).toEqual([]);

    queue.operations.push(codOperation(new Date(now.getTime() + 120_000), secondCodOperationId));
    await harness.scheduler.requestSync(true);

    expect(harness.calls).toEqual([
      `cod:drv2-${secondCodOperationId}-cod`,
      "transition:DELIVERY_PROOF",
    ]);
    expect(queue.operations).toEqual([]);
    await harness.scheduler.dispose();
  });

  it("keeps a delivery saved before collections existed (migrated in place) waiting for the cash", async () => {
    // A schema 1 record: no cash member and no kept rejection code.
    const legacy: Record<string, unknown> = { ...deliveryOperation(), schemaVersion: 1 };
    delete legacy.cod;
    delete legacy.rejectionCode;
    const migrated = parseDriverOfflineOperation(
      migrateDriverOfflineOperationRecord(legacy),
      partition.key,
    );
    expect(migrated).toEqual(deliveryOperation());
    const queue = new MemoryQueue([migrated]);
    queue.proofs.set(deliveryOperationId, proofFor(deliveryOperationId));
    const harness = deliveryHarness(queue);

    await harness.scheduler.requestSync(true);
    expect(harness.calls).toEqual([]);
    expect(queue.operations).toEqual([migrated]);

    queue.operations.push(codOperation(new Date(now.getTime() + 120_000), secondCodOperationId));
    await harness.scheduler.requestSync(true);

    expect(harness.calls).toEqual([
      `cod:drv2-${secondCodOperationId}-cod`,
      "transition:DELIVERY_PROOF",
    ]);
    expect(queue.operations).toEqual([]);
    await harness.scheduler.dispose();
  });

  it("puts a delivery refused with COD_NOT_RECORDED back to wait for the cash", async () => {
    const queue = new MemoryQueue([deliveryOperation()]);
    queue.proofs.set(deliveryOperationId, proofFor(deliveryOperationId));
    let reads = 0;
    const transitionOrder = vi
      .fn()
      .mockRejectedValue(new DriverSyncApiError("conflict", "COD_NOT_RECORDED"));
    // The first read predates the cash on the order; the server knows it.
    const scheduler = schedulerFor(
      queue,
      {
        createProofUploadSession: vi.fn().mockResolvedValue(grant()),
        finalizeProof: finalizeProof(),
        transitionOrder,
      },
      async () => {
        reads += 1;
        return stopList([
          reads === 1
            ? withStopDetails({ ...deliveringStop(false), cod: null })
            : deliveringStop(false),
        ]);
      },
    );

    await scheduler.requestSync(true);

    expect(transitionOrder).toHaveBeenCalledOnce();
    expect(queue.operations).toEqual([
      expect.objectContaining({
        id: deliveryOperationId,
        status: "PENDING",
        safeError: null,
        rejectionCode: null,
        proofId: "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
      }),
    ]);
    await scheduler.dispose();
  });

  it("removes the collection without sending once the stop is no longer assigned", async () => {
    const queue = new MemoryQueue([codOperation()]);
    const recordCodCollection = vi.fn();
    const scheduler = schedulerFor(
      queue,
      { recordCodCollection },
      async () => stopList([]),
    );

    await scheduler.requestSync(true);

    expect(recordCodCollection).not.toHaveBeenCalled();
    expect(queue.operations).toEqual([]);
    await scheduler.dispose();
  });

  it("rejects a receipt for another order or amount", async () => {
    const queue = new MemoryQueue([codOperation()]);
    const scheduler = schedulerFor(queue, {
      recordCodCollection: vi
        .fn()
        .mockResolvedValue({ ...receipt(), amountCents: amountCents - 1 }),
    });

    await scheduler.requestSync(true);

    expect(queue.operations[0]).toMatchObject({ status: "NEEDS_ATTENTION" });
    await scheduler.dispose();
  });
});

function codOperation(
  at: Date = now,
  id: string = codOperationId,
): DriverOfflineOperation {
  return createDriverOfflineOperation({
    partitionKey: partition.key,
    orderId,
    kind: "COD_COLLECTION",
    expectedVersion: 9,
    cod: { amountCents, reference: "ORD_SYNTHETIC 2026-10-11T13:05:42-07:00" },
    now: () => at,
    randomUuid: () => id,
  });
}

const secondCodOperationId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";

function finalizeProof() {
  return vi.fn(async (operation: DriverOfflineOperation) => ({
    id: "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
    proofType: "DELIVERY_PHOTO" as const,
    sha256: "a".repeat(64),
    capturedAt: operation.clientOccurredAt,
  }));
}

/**
 * A scheduler whose server records the cash, then accepts the delivery only
 * once the cash is recorded, and lists the calls it received in order.
 */
function deliveryHarness(queue: MemoryQueue) {
  let recorded = false;
  let delivered = false;
  const calls: string[] = [];
  const scheduler = schedulerFor(
    queue,
    {
      recordCodCollection: vi.fn(async (operation: DriverOfflineOperation) => {
        calls.push(`cod:${operation.cod?.idempotencyKey}`);
        recorded = true;
        return receipt();
      }),
      createProofUploadSession: vi.fn().mockResolvedValue(grant()),
      finalizeProof: finalizeProof(),
      transitionOrder: vi.fn(async (operation: DriverOfflineOperation) => {
        calls.push(`transition:${operation.kind}`);
        if (!recorded) {
          throw new DriverSyncApiError("conflict", "COD_NOT_RECORDED");
        }
        delivered = true;
        return { id: orderId, status: "DELIVERED" as const, version: 10 };
      }),
    },
    async () => stopList(delivered ? [] : [deliveringStop(recorded)]),
  );
  return { scheduler, calls };
}

function deliveryOperation(): DriverOfflineOperation {
  return createDriverOfflineOperation({
    partitionKey: partition.key,
    orderId,
    kind: "DELIVERY_PROOF",
    expectedVersion: 9,
    proof: { contentType: "image/png", sizeBytes: 1, sha256: "a".repeat(64) },
    now: () => new Date(now.getTime() + 60_000),
    randomUuid: () => deliveryOperationId,
  });
}

function deliveringStop(recorded: boolean): DriverStop {
  return withStopDetails({
    order_id: orderId,
    aggregate_version: 9,
    order_public_id: "ORD_SYNTHETIC",
    stop_type: "DELIVERY",
    status: "DELIVERING",
    address_summary: "Zona sintética",
    cod: { expected: { currency: "MXN", amount_cents: amountCents }, recorded },
  });
}

function receipt(): DriverCodReceipt {
  return {
    id: "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
    orderId,
    amountCents,
    status: "RECORDED",
  };
}

function grant() {
  return {
    id: "44444444-4444-4444-8444-444444444444",
    status: "CREATED",
    uploadUrl: "https://objects.synthetic.test/quarantine",
    expiresAt: "2026-10-12T00:00:00.000Z",
    requiredHeaders: { "Content-Type": "image/png" },
  };
}

function proofFor(operationId: string): DriverProofBlobRecord {
  return {
    schemaVersion: 1,
    partitionKey: partition.key,
    operationId,
    blob: new Blob([new Uint8Array([1])], { type: "image/png" }),
    contentType: "image/png",
    sizeBytes: 1,
    sha256: "a".repeat(64),
  };
}

function schedulerFor(
  queue: MemoryQueue,
  apiOverrides: Partial<DriverSyncApi>,
  refreshStops: () => Promise<DriverStopList> = async () =>
    stopList([deliveringStop(false)]),
): DriverSyncScheduler {
  return new DriverSyncScheduler({
    partition,
    queue,
    api: {
      transitionOrder: vi.fn(),
      createProofUploadSession: vi.fn(),
      finalizeProof: vi.fn(),
      recordCodCollection: vi.fn(),
      openFailedDeliveryIncident: vi.fn(),
      ...apiOverrides,
    },
    refreshStops,
    onQueueChanged: vi.fn(),
    onAccessRevoked: vi.fn(),
    now: () => now,
    randomUuid: () => "99999999-9999-4999-8999-999999999999",
    upload: vi.fn().mockResolvedValue(undefined),
  });
}

class MemoryQueue implements DriverOfflineQueue {
  public operations: DriverOfflineOperation[];
  public readonly proofs = new Map<string, DriverProofBlobRecord>();

  public constructor(operations: DriverOfflineOperation[]) {
    this.operations = [...operations];
  }

  public async listOperations(): Promise<readonly DriverOfflineOperation[]> {
    return [...this.operations].sort(
      (left, right) =>
        left.createdAt.localeCompare(right.createdAt) ||
        left.id.localeCompare(right.id),
    );
  }

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
    operation: DriverOfflineOperation,
  ): Promise<void> {
    await this.replaceOperation(partitionValue, operation);
    this.proofs.delete(operation.id);
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

  public async rebuildOperation(): Promise<void> {
    throw new Error("not used");
  }

  public async clearPartition(): Promise<void> {
    this.operations = [];
  }

  public async acquireLease(): Promise<boolean> {
    return true;
  }

  public async renewLease(): Promise<boolean> {
    return true;
  }

  public async releaseLease(): Promise<void> {}
}
