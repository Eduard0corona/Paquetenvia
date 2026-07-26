import type { DriverCachePartition } from "../cache/driver-stops-cache";
import {
  DriverOfflineOperationsStoreName,
  DriverProofBlobsStoreName,
  DriverStopsStoreName,
  DriverSyncLeasesStoreName,
  driverDatabaseRequest,
  driverDatabaseTransactionCompleted,
  openDriverDatabase,
} from "../cache/driver-stops-cache";
import {
  DriverOfflineContractError,
  DriverOfflineOperationSchemaVersion,
  parseDriverOfflineOperation,
  parseDriverProofBlob,
  parseDriverSyncLease,
  type DriverOfflineOperation,
  type DriverProofBlobRecord,
  type DriverSyncLease,
} from "./operation-contract";
import {
  MaximumDriverOperationsPerPartition,
  MaximumQueuedProofBytes,
  validateDriverProof,
  type ValidatedDriverProof,
} from "../proofs/proof-file";

export type DriverOfflineQueueFailure =
  | "limit"
  | "quota"
  | "corrupt"
  | "unavailable";

export class DriverOfflineQueueError extends Error {
  public constructor(public readonly category: DriverOfflineQueueFailure) {
    super("No fue posible guardar la operación sin conexión.");
    this.name = "DriverOfflineQueueError";
  }
}

export interface DriverOfflineQueue {
  listOperations(
    partition: DriverCachePartition,
  ): Promise<readonly DriverOfflineOperation[]>;
  readProof(
    partition: DriverCachePartition,
    operationId: string,
  ): Promise<DriverProofBlobRecord | null>;
  enqueue(
    partition: DriverCachePartition,
    operation: DriverOfflineOperation,
    proof?: ValidatedDriverProof,
  ): Promise<void>;
  replaceOperation(
    partition: DriverCachePartition,
    operation: DriverOfflineOperation,
  ): Promise<void>;
  replaceOperationAndDeleteProof(
    partition: DriverCachePartition,
    operation: DriverOfflineOperation,
  ): Promise<void>;
  deleteOperation(
    partition: DriverCachePartition,
    operationId: string,
  ): Promise<void>;
  rebuildOperation(
    partition: DriverCachePartition,
    replacedOperationId: string,
    operation: DriverOfflineOperation,
  ): Promise<void>;
  clearPartition(partition: DriverCachePartition): Promise<void>;
  acquireLease(
    partition: DriverCachePartition,
    ownerId: string,
    now: Date,
    durationMilliseconds: number,
  ): Promise<boolean>;
  renewLease(
    partition: DriverCachePartition,
    ownerId: string,
    now: Date,
    durationMilliseconds: number,
  ): Promise<boolean>;
  releaseLease(
    partition: DriverCachePartition,
    ownerId: string,
  ): Promise<void>;
}

export class IndexedDbDriverOfflineQueue implements DriverOfflineQueue {
  public async listOperations(
    partition: DriverCachePartition,
  ): Promise<readonly DriverOfflineOperation[]> {
    const database = await openDriverDatabase();
    try {
      const transaction = database.transaction(
        DriverOfflineOperationsStoreName,
        "readonly",
      );
      const records = await driverDatabaseRequest<unknown[]>(
        transaction
          .objectStore(DriverOfflineOperationsStoreName)
          .index("by_partition")
          .getAll(IDBKeyRange.only(partition.key)),
      );
      return Object.freeze(
        records
          .map((record) => parseDriverOfflineOperation(record, partition.key))
          .sort(compareOperations),
      );
    } catch (error) {
      throw mapQueueError(error);
    } finally {
      database.close();
    }
  }

  public async readProof(
    partition: DriverCachePartition,
    operationId: string,
  ): Promise<DriverProofBlobRecord | null> {
    const database = await openDriverDatabase();
    try {
      const value = await driverDatabaseRequest<unknown>(
        database
          .transaction(DriverProofBlobsStoreName, "readonly")
          .objectStore(DriverProofBlobsStoreName)
          .get([partition.key, operationId]),
      );
      if (value === undefined) return null;
      const proof = parseDriverProofBlob(value, partition.key);
      const verified = await validateDriverProof(proof.blob);
      if (
        verified.contentType !== proof.contentType ||
        verified.sizeBytes !== proof.sizeBytes ||
        verified.sha256 !== proof.sha256
      ) {
        throw new DriverOfflineContractError();
      }
      return proof;
    } catch (error) {
      throw mapQueueError(error);
    } finally {
      database.close();
    }
  }

  public async enqueue(
    partition: DriverCachePartition,
    operation: DriverOfflineOperation,
    proof?: ValidatedDriverProof,
  ): Promise<void> {
    const validatedOperation = parseDriverOfflineOperation(
      operation,
      partition.key,
    );
    const expectsProof = validatedOperation.proofType !== null;
    if (
      expectsProof !== (proof !== undefined) ||
      (proof !== undefined &&
        (validatedOperation.contentType !== proof.contentType ||
          validatedOperation.sizeBytes !== proof.sizeBytes ||
          validatedOperation.sha256 !== proof.sha256 ||
          validatedOperation.capturedAt !==
            validatedOperation.clientOccurredAt))
    ) {
      throw new DriverOfflineQueueError("corrupt");
    }
    await ensureStorageEstimate(proof?.sizeBytes ?? 0);
    const database = await openDriverDatabase();
    try {
      const transaction = database.transaction(
        [DriverOfflineOperationsStoreName, DriverProofBlobsStoreName],
        "readwrite",
      );
      const operationStore = transaction.objectStore(
        DriverOfflineOperationsStoreName,
      );
      const blobStore = transaction.objectStore(DriverProofBlobsStoreName);
      const existingOperations = await driverDatabaseRequest<unknown[]>(
        operationStore
          .index("by_partition")
          .getAll(IDBKeyRange.only(partition.key)),
      );
      if (existingOperations.length >= MaximumDriverOperationsPerPartition) {
        transaction.abort();
        throw new DriverOfflineQueueError("limit");
      }
      if (
        existingOperations.some(
          (candidate) =>
            parseDriverOfflineOperation(candidate, partition.key).id ===
            validatedOperation.id,
        )
      ) {
        transaction.abort();
        throw new DriverOfflineQueueError("limit");
      }

      if (proof) {
        const blobs = await driverDatabaseRequest<unknown[]>(
          blobStore
            .index("by_partition")
            .getAll(IDBKeyRange.only(partition.key)),
        );
        const queuedBytes = blobs.reduce<number>(
          (total, candidate) =>
            total + parseDriverProofBlob(candidate, partition.key).sizeBytes,
          0,
        );
        if (queuedBytes + proof.sizeBytes > MaximumQueuedProofBytes) {
          transaction.abort();
          throw new DriverOfflineQueueError("limit");
        }
        blobStore.add({
          schemaVersion: DriverOfflineOperationSchemaVersion,
          partitionKey: partition.key,
          operationId: validatedOperation.id,
          blob: proof.blob,
          contentType: proof.contentType,
          sizeBytes: proof.sizeBytes,
          sha256: proof.sha256,
        } satisfies DriverProofBlobRecord);
      }
      operationStore.add({ ...validatedOperation });
      await driverDatabaseTransactionCompleted(transaction);
    } catch (error) {
      throw mapQueueError(error);
    } finally {
      database.close();
    }
  }

  public async replaceOperation(
    partition: DriverCachePartition,
    operation: DriverOfflineOperation,
  ): Promise<void> {
    const validated = parseDriverOfflineOperation(operation, partition.key);
    const database = await openDriverDatabase();
    try {
      const transaction = database.transaction(
        DriverOfflineOperationsStoreName,
        "readwrite",
      );
      transaction
        .objectStore(DriverOfflineOperationsStoreName)
        .put({ ...validated });
      await driverDatabaseTransactionCompleted(transaction);
    } catch (error) {
      throw mapQueueError(error);
    } finally {
      database.close();
    }
  }

  public async replaceOperationAndDeleteProof(
    partition: DriverCachePartition,
    operation: DriverOfflineOperation,
  ): Promise<void> {
    const validated = parseDriverOfflineOperation(operation, partition.key);
    const database = await openDriverDatabase();
    try {
      const transaction = database.transaction(
        [DriverOfflineOperationsStoreName, DriverProofBlobsStoreName],
        "readwrite",
      );
      transaction
        .objectStore(DriverOfflineOperationsStoreName)
        .put({ ...validated });
      transaction
        .objectStore(DriverProofBlobsStoreName)
        .delete([partition.key, validated.id]);
      await driverDatabaseTransactionCompleted(transaction);
    } catch (error) {
      throw mapQueueError(error);
    } finally {
      database.close();
    }
  }

  public async deleteOperation(
    partition: DriverCachePartition,
    operationId: string,
  ): Promise<void> {
    const database = await openDriverDatabase();
    try {
      const transaction = database.transaction(
        [DriverOfflineOperationsStoreName, DriverProofBlobsStoreName],
        "readwrite",
      );
      transaction
        .objectStore(DriverOfflineOperationsStoreName)
        .delete([partition.key, operationId]);
      transaction
        .objectStore(DriverProofBlobsStoreName)
        .delete([partition.key, operationId]);
      await driverDatabaseTransactionCompleted(transaction);
    } catch (error) {
      throw mapQueueError(error);
    } finally {
      database.close();
    }
  }

  public async rebuildOperation(
    partition: DriverCachePartition,
    replacedOperationId: string,
    operation: DriverOfflineOperation,
  ): Promise<void> {
    const validated = parseDriverOfflineOperation(operation, partition.key);
    const database = await openDriverDatabase();
    try {
      const transaction = database.transaction(
        [DriverOfflineOperationsStoreName, DriverProofBlobsStoreName],
        "readwrite",
      );
      const operations = transaction.objectStore(
        DriverOfflineOperationsStoreName,
      );
      const blobs = transaction.objectStore(DriverProofBlobsStoreName);
      const proofCandidate = await driverDatabaseRequest<unknown>(
        blobs.get([partition.key, replacedOperationId]),
      );
      operations.delete([partition.key, replacedOperationId]);
      blobs.delete([partition.key, replacedOperationId]);
      operations.add({ ...validated });
      if (proofCandidate !== undefined) {
        const proof = parseDriverProofBlob(proofCandidate, partition.key);
        blobs.add({
          ...proof,
          operationId: validated.id,
        } satisfies DriverProofBlobRecord);
      }
      await driverDatabaseTransactionCompleted(transaction);
    } catch (error) {
      throw mapQueueError(error);
    } finally {
      database.close();
    }
  }

  public async clearPartition(
    partition: DriverCachePartition,
  ): Promise<void> {
    const database = await openDriverDatabase();
    try {
      const transaction = database.transaction(
        [
          DriverStopsStoreName,
          DriverOfflineOperationsStoreName,
          DriverProofBlobsStoreName,
          DriverSyncLeasesStoreName,
        ],
        "readwrite",
      );
      transaction
        .objectStore(DriverStopsStoreName)
        .delete(partition.key);
      await deletePartitionRecords(
        transaction.objectStore(DriverOfflineOperationsStoreName),
        partition.key,
      );
      await deletePartitionRecords(
        transaction.objectStore(DriverProofBlobsStoreName),
        partition.key,
      );
      transaction
        .objectStore(DriverSyncLeasesStoreName)
        .delete(partition.key);
      await driverDatabaseTransactionCompleted(transaction);
    } catch (error) {
      throw mapQueueError(error);
    } finally {
      database.close();
    }
  }

  public async acquireLease(
    partition: DriverCachePartition,
    ownerId: string,
    now: Date,
    durationMilliseconds: number,
  ): Promise<boolean> {
    return this.writeLease(
      partition,
      ownerId,
      now,
      durationMilliseconds,
      false,
    );
  }

  public async renewLease(
    partition: DriverCachePartition,
    ownerId: string,
    now: Date,
    durationMilliseconds: number,
  ): Promise<boolean> {
    return this.writeLease(
      partition,
      ownerId,
      now,
      durationMilliseconds,
      true,
    );
  }

  public async releaseLease(
    partition: DriverCachePartition,
    ownerId: string,
  ): Promise<void> {
    const database = await openDriverDatabase();
    try {
      const transaction = database.transaction(
        DriverSyncLeasesStoreName,
        "readwrite",
      );
      const store = transaction.objectStore(DriverSyncLeasesStoreName);
      const candidate = await driverDatabaseRequest<unknown>(
        store.get(partition.key),
      );
      if (
        candidate !== undefined &&
        parseDriverSyncLease(candidate, partition.key).ownerId === ownerId
      ) {
        store.delete(partition.key);
      }
      await driverDatabaseTransactionCompleted(transaction);
    } catch (error) {
      throw mapQueueError(error);
    } finally {
      database.close();
    }
  }

  private async writeLease(
    partition: DriverCachePartition,
    ownerId: string,
    now: Date,
    durationMilliseconds: number,
    requireOwner: boolean,
  ): Promise<boolean> {
    if (
      !Number.isSafeInteger(durationMilliseconds) ||
      durationMilliseconds < 1
    ) {
      throw new DriverOfflineQueueError("unavailable");
    }
    const database = await openDriverDatabase();
    try {
      const transaction = database.transaction(
        DriverSyncLeasesStoreName,
        "readwrite",
      );
      const store = transaction.objectStore(DriverSyncLeasesStoreName);
      const candidate = await driverDatabaseRequest<unknown>(
        store.get(partition.key),
      );
      if (candidate !== undefined) {
        const current = parseDriverSyncLease(candidate, partition.key);
        const active = Date.parse(current.expiresAt) > now.getTime();
        if (
          (requireOwner && current.ownerId !== ownerId) ||
          (!requireOwner && active && current.ownerId !== ownerId)
        ) {
          transaction.abort();
          return false;
        }
      } else if (requireOwner) {
        transaction.abort();
        return false;
      }
      const lease: DriverSyncLease = {
        schemaVersion: DriverOfflineOperationSchemaVersion,
        partitionKey: partition.key,
        ownerId,
        updatedAt: now.toISOString(),
        expiresAt: new Date(
          now.getTime() + durationMilliseconds,
        ).toISOString(),
      };
      parseDriverSyncLease(lease, partition.key);
      store.put(lease);
      await driverDatabaseTransactionCompleted(transaction);
      return true;
    } catch (error) {
      if (error instanceof DriverOfflineQueueError) throw error;
      throw mapQueueError(error);
    } finally {
      database.close();
    }
  }
}

async function deletePartitionRecords(
  store: IDBObjectStore,
  partitionKey: string,
): Promise<void> {
  await new Promise<void>((resolve, reject) => {
    const cursor = store
      .index("by_partition")
      .openKeyCursor(IDBKeyRange.only(partitionKey));
    cursor.onsuccess = () => {
      if (!cursor.result) {
        resolve();
        return;
      }
      store.delete(cursor.result.primaryKey);
      cursor.result.continue();
    };
    cursor.onerror = () => reject(new DriverOfflineQueueError("unavailable"));
  });
}

async function ensureStorageEstimate(additionalBytes: number): Promise<void> {
  if (
    additionalBytes < 1 ||
    typeof navigator === "undefined" ||
    !navigator.storage?.estimate
  ) {
    return;
  }
  const estimate = await navigator.storage.estimate();
  if (
    typeof estimate.quota === "number" &&
    typeof estimate.usage === "number" &&
    estimate.quota - estimate.usage < additionalBytes
  ) {
    throw new DriverOfflineQueueError("quota");
  }
}

function compareOperations(
  left: DriverOfflineOperation,
  right: DriverOfflineOperation,
): number {
  return (
    left.createdAt.localeCompare(right.createdAt) ||
    left.id.localeCompare(right.id)
  );
}

function mapQueueError(error: unknown): DriverOfflineQueueError {
  if (error instanceof DriverOfflineQueueError) return error;
  if (error instanceof DriverOfflineContractError) {
    return new DriverOfflineQueueError("corrupt");
  }
  if (
    error instanceof DOMException &&
    (error.name === "QuotaExceededError" ||
      error.name === "ConstraintError")
  ) {
    return new DriverOfflineQueueError(
      error.name === "QuotaExceededError" ? "quota" : "limit",
    );
  }
  return new DriverOfflineQueueError("unavailable");
}
