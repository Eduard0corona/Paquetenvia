import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  DriverDatabaseVersion,
  DriverOfflineOperationsStoreName,
  DriverProofBlobsStoreName,
  DriverStopsDatabaseName,
  DriverSyncLeasesStoreName,
  openDriverDatabase,
} from "../cache/driver-stops-cache";
import { FakeIndexedDb, fakeIdbKeyRange } from "../cache/fake-indexed-db.fixtures";
import {
  MaximumDriverOperationsPerPartition,
  MaximumDriverProofBytes,
  validateDriverProof,
} from "../proofs/proof-file";
import {
  DriverOfflineQueueError,
  IndexedDbDriverOfflineQueue,
} from "./driver-offline-queue";
import { orderId, partition, reportedAt, uuid } from "./failed-delivery.fixtures";
import {
  createDriverOfflineOperation,
  type DriverOfflineOperation,
} from "./operation-contract";

/**
 * DRV-002 queue with the UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12 report:
 * the operation and its photo are written in one transaction or not at all,
 * the per-partition limits hold, the lease admits one scheduler, and a record
 * the app cannot read fails the whole queue closed without deleting anything.
 * The real browser runs in the Playwright driver suite.
 */
let idb: FakeIndexedDb;
const queue = new IndexedDbDriverOfflineQueue();

beforeEach(() => {
  idb = new FakeIndexedDb();
  vi.stubGlobal("indexedDB", idb);
  vi.stubGlobal("IDBKeyRange", fakeIdbKeyRange);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function photo(size: number): Blob {
  return new Blob([new Uint8Array(size).fill(7)], { type: "image/png" });
}

async function report(id: string, blob: Blob) {
  const proof = await validateDriverProof(blob);
  const operation = createDriverOfflineOperation({
    partitionKey: partition.key,
    orderId,
    kind: "FAILED_DELIVERY",
    expectedVersion: 8,
    proof,
    failedDelivery: { reasonCode: "RECIPIENT_ABSENT" },
    now: () => reportedAt,
    randomUuid: () => id,
  });
  return { operation, proof };
}

function checkIn(id: string, partitionKey = partition.key): DriverOfflineOperation {
  return createDriverOfflineOperation({
    partitionKey,
    orderId,
    kind: "CHECK_IN",
    expectedVersion: 1,
    now: () => reportedAt,
    randomUuid: () => id,
  });
}

function stored(store: string): unknown[] {
  return idb.records(DriverStopsDatabaseName, store);
}

describe("offline queue with a failed-delivery report", () => {
  it("stores the report and its photo together and reads both back intact", async () => {
    const { operation, proof } = await report(uuid(0x101), photo(3));

    await queue.enqueue(partition, operation, proof);

    expect(await queue.listOperations(partition)).toEqual([operation]);
    const saved = await queue.readProof(partition, operation.id);
    expect(saved).toMatchObject({ operationId: operation.id, sizeBytes: 3, sha256: proof.sha256 });
    expect(idb.version(DriverStopsDatabaseName)).toBe(DriverDatabaseVersion);
  });

  it("writes nothing when the photo limit of the partition would be passed", async () => {
    const first = await report(uuid(0x101), photo(MaximumDriverProofBytes));
    const second = await report(uuid(0x102), photo(MaximumDriverProofBytes));
    await queue.enqueue(partition, first.operation, first.proof);
    await queue.enqueue(partition, second.operation, second.proof);
    const third = await report(uuid(0x103), photo(6 * 1024 * 1024));

    await expect(queue.enqueue(partition, third.operation, third.proof)).rejects.toMatchObject({
      category: "limit",
    });

    expect(stored(DriverOfflineOperationsStoreName)).toHaveLength(2);
    expect(stored(DriverProofBlobsStoreName)).toHaveLength(2);
  });

  it("writes nothing when the partition already holds the maximum number of actions", async () => {
    await queue.listOperations(partition);
    for (let index = 0; index < MaximumDriverOperationsPerPartition; index += 1) {
      idb.seed(DriverStopsDatabaseName, DriverOfflineOperationsStoreName, checkIn(uuid(0x1000 + index)));
    }
    const { operation, proof } = await report(uuid(0x101), photo(3));

    await expect(queue.enqueue(partition, operation, proof)).rejects.toBeInstanceOf(
      DriverOfflineQueueError,
    );

    expect(stored(DriverOfflineOperationsStoreName)).toHaveLength(MaximumDriverOperationsPerPartition);
    expect(stored(DriverProofBlobsStoreName)).toHaveLength(0);
  });

  it("refuses the same report twice and a photo that does not match it", async () => {
    const { operation, proof } = await report(uuid(0x101), photo(3));
    await queue.enqueue(partition, operation, proof);
    await expect(queue.enqueue(partition, operation, proof)).rejects.toMatchObject({ category: "limit" });
    const other = await validateDriverProof(photo(4));
    const { operation: mismatched } = await report(uuid(0x102), photo(3));
    await expect(queue.enqueue(partition, mismatched, other)).rejects.toMatchObject({ category: "corrupt" });
    await expect(queue.enqueue(partition, mismatched)).rejects.toMatchObject({ category: "corrupt" });
    expect(stored(DriverOfflineOperationsStoreName)).toHaveLength(1);
    expect(stored(DriverProofBlobsStoreName)).toHaveLength(1);
  });

  it("drops the photo with the progress once the proof exists, and moves it on a rebuild", async () => {
    const { operation, proof } = await report(uuid(0x101), photo(3));
    await queue.enqueue(partition, operation, proof);
    const rebuilt = { ...(await report(uuid(0x102), photo(3))).operation, failedDelivery: operation.failedDelivery };

    await queue.rebuildOperation(partition, operation.id, rebuilt);

    expect((await queue.listOperations(partition)).map((value) => value.id)).toEqual([rebuilt.id]);
    expect(await queue.readProof(partition, operation.id)).toBeNull();
    expect(await queue.readProof(partition, rebuilt.id)).toMatchObject({ operationId: rebuilt.id });

    await queue.replaceOperationAndDeleteProof(partition, { ...rebuilt, proofId: uuid(0x600) });

    expect((await queue.listOperations(partition))[0].proofId).toBe(uuid(0x600));
    expect(await queue.readProof(partition, rebuilt.id)).toBeNull();
  });

  it("fails closed on a record it cannot read, deleting nothing", async () => {
    await queue.listOperations(partition);
    const newer = { ...checkIn(uuid(0x201)), schemaVersion: 4, kind: "FAILED_PICKUP" };
    idb.seed(DriverStopsDatabaseName, DriverOfflineOperationsStoreName, newer);
    const { operation, proof } = await report(uuid(0x101), photo(3));

    await expect(queue.listOperations(partition)).rejects.toMatchObject({ category: "corrupt" });
    await expect(queue.enqueue(partition, operation, proof)).rejects.toMatchObject({ category: "corrupt" });

    expect(stored(DriverOfflineOperationsStoreName)).toEqual([newer]);
    expect(stored(DriverProofBlobsStoreName)).toHaveLength(0);
  });

  it("clears only the active partition", async () => {
    const otherPartition = { key: "K".repeat(43) };
    const { operation, proof } = await report(uuid(0x101), photo(3));
    await queue.enqueue(partition, operation, proof);
    await queue.enqueue(otherPartition, checkIn(uuid(0x301), otherPartition.key));

    await queue.clearPartition(partition);

    expect(await queue.listOperations(partition)).toEqual([]);
    expect(await queue.listOperations(otherPartition)).toHaveLength(1);
    expect(stored(DriverProofBlobsStoreName)).toHaveLength(0);
  });
});

describe("sync lease", () => {
  it("admits one owner until it expires and lets only the owner renew or release it", async () => {
    const owner = uuid(0x401);
    const other = uuid(0x402);
    const now = new Date("2026-10-12T19:00:00.000Z");

    expect(await queue.acquireLease(partition, owner, now, 15_000)).toBe(true);
    expect(await queue.acquireLease(partition, other, new Date(now.getTime() + 1_000), 15_000)).toBe(false);
    expect(await queue.renewLease(partition, other, now, 15_000)).toBe(false);
    expect(await queue.renewLease(partition, owner, new Date(now.getTime() + 5_000), 15_000)).toBe(true);
    await queue.releaseLease(partition, other);
    expect(stored(DriverSyncLeasesStoreName)).toHaveLength(1);
    // An abandoned lease can be taken over once it expired.
    expect(await queue.acquireLease(partition, other, new Date(now.getTime() + 60_000), 15_000)).toBe(true);
    await queue.releaseLease(partition, other);
    expect(stored(DriverSyncLeasesStoreName)).toHaveLength(0);
  });
});

describe("IndexedDB version 4", () => {
  it("rewrites a queued schema 2 action as schema 3 when the app opens the version 3 database", async () => {
    const schema2 = { ...checkIn(uuid(0x501)), schemaVersion: 2 } as Record<string, unknown>;
    delete schema2.failedDelivery;
    await new Promise<void>((resolve, reject) => {
      const open = idb.open(DriverStopsDatabaseName, 3);
      open.onupgradeneeded = () => {
        const database = open.result!;
        database.createObjectStore("snapshots", { keyPath: "partitionKey" });
        database
          .createObjectStore("operations", { keyPath: ["partitionKey", "id"] })
          .createIndex("by_partition", "partitionKey");
        database
          .createObjectStore("proof_blobs", { keyPath: ["partitionKey", "operationId"] })
          .createIndex("by_partition", "partitionKey");
        database.createObjectStore("sync_leases", { keyPath: "partitionKey" });
      };
      open.onsuccess = () => resolve();
      open.onerror = () => reject(open.error);
    });
    idb.seed(DriverStopsDatabaseName, DriverOfflineOperationsStoreName, schema2);

    const [migrated] = await queue.listOperations(partition);

    expect(idb.version(DriverStopsDatabaseName)).toBe(4);
    expect(migrated).toMatchObject({ id: uuid(0x501), schemaVersion: 3, failedDelivery: null });
  });

  it("cannot be opened by an app built for version 3, so it never misreads a newer action", async () => {
    const database = await openDriverDatabase();
    database.close();
    const error = await new Promise<DOMException | null>((resolve) => {
      const open = idb.open(DriverStopsDatabaseName, 3);
      open.onsuccess = () => resolve(null);
      open.onerror = () => resolve(open.error);
    });
    expect(error?.name).toBe("VersionError");
  });
});
