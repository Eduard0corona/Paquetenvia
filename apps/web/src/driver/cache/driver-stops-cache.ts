import {
  DriverStopsContractError,
  parseDriverStops,
  type DriverStop,
} from "../contracts/driver-stop";
import type { DriverSession } from "../session/driver-session";

export const DriverStopsDatabaseName = "paquetenvia-driver-stops-v1";
export const DriverStopsStoreName = "snapshots";
export const DriverDatabaseVersion = 2 as const;
export const DriverStopsSnapshotSchemaVersion = 1 as const;
/** @deprecated Use DriverStopsSnapshotSchemaVersion for persisted snapshots. */
export const DriverStopsSchemaVersion = DriverStopsSnapshotSchemaVersion;
export const DriverOfflineOperationsStoreName = "operations";
export const DriverProofBlobsStoreName = "proof_blobs";
export const DriverSyncLeasesStoreName = "sync_leases";

export interface DriverCachePartition {
  readonly key: string;
}

export interface DriverStopsSnapshot {
  readonly schemaVersion: 1;
  readonly synchronizedAt: string;
  readonly stops: readonly DriverStop[];
}

export interface DriverStopsCache {
  readSnapshot(
    partition: DriverCachePartition,
  ): Promise<DriverStopsSnapshot | null>;
  replaceSnapshot(
    partition: DriverCachePartition,
    snapshot: DriverStopsSnapshot,
  ): Promise<void>;
  clearPartition(partition: DriverCachePartition): Promise<void>;
}

interface PersistedSnapshot extends DriverStopsSnapshot {
  readonly partitionKey: string;
}

export class DriverStopsCacheError extends Error {
  public constructor() {
    super("La caché de paradas no está disponible.");
    this.name = "DriverStopsCacheError";
  }
}

export async function createDriverCachePartition(
  session: DriverSession,
): Promise<DriverCachePartition> {
  const input = new TextEncoder().encode(
    `${session.cacheNamespace}\u0000${session.organizationId}`,
  );
  const digest = await crypto.subtle.digest("SHA-256", input);
  const key = base64Url(new Uint8Array(digest));
  return Object.freeze({ key });
}

export class IndexedDbDriverStopsCache implements DriverStopsCache {
  public async readSnapshot(
    partition: DriverCachePartition,
  ): Promise<DriverStopsSnapshot | null> {
    const database = await openDriverDatabase();
    try {
      const record = await driverDatabaseRequest<PersistedSnapshot | undefined>(
        database
          .transaction(DriverStopsStoreName, "readonly")
          .objectStore(DriverStopsStoreName)
          .get(partition.key),
      );
      if (!record) {
        return null;
      }
      return validateSnapshot(record, partition.key);
    } finally {
      database.close();
    }
  }

  public async replaceSnapshot(
    partition: DriverCachePartition,
    snapshot: DriverStopsSnapshot,
  ): Promise<void> {
    const validated = validateSnapshot(
      { ...snapshot, partitionKey: partition.key },
      partition.key,
    );
    const database = await openDriverDatabase();
    try {
      const transaction = database.transaction(DriverStopsStoreName, "readwrite");
      transaction.objectStore(DriverStopsStoreName).put({
        partitionKey: partition.key,
        ...validated,
        stops: validated.stops.map((stop) => ({ ...stop })),
      } satisfies PersistedSnapshot);
      await driverDatabaseTransactionCompleted(transaction);
    } finally {
      database.close();
    }
  }

  public async clearPartition(partition: DriverCachePartition): Promise<void> {
    const database = await openDriverDatabase();
    try {
      const transaction = database.transaction(DriverStopsStoreName, "readwrite");
      transaction.objectStore(DriverStopsStoreName).delete(partition.key);
      await driverDatabaseTransactionCompleted(transaction);
    } finally {
      database.close();
    }
  }
}

function validateSnapshot(
  value: unknown,
  expectedPartitionKey: string,
): DriverStopsSnapshot {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw new DriverStopsCacheError();
  }

  const record = value as Record<string, unknown>;
  const keys = Object.keys(record);
  const allowed = new Set([
    "partitionKey",
    "schemaVersion",
    "synchronizedAt",
    "stops",
  ]);
  if (
    keys.length !== allowed.size ||
    keys.some((key) => !allowed.has(key)) ||
    record.partitionKey !== expectedPartitionKey ||
    record.schemaVersion !== DriverStopsSchemaVersion ||
    typeof record.synchronizedAt !== "string" ||
    !isUtcTimestamp(record.synchronizedAt)
  ) {
    throw new DriverStopsCacheError();
  }

  try {
    return Object.freeze({
      schemaVersion: DriverStopsSchemaVersion,
      synchronizedAt: record.synchronizedAt,
      stops: parseDriverStops(record.stops),
    });
  } catch (error) {
    if (error instanceof DriverStopsContractError) {
      throw new DriverStopsCacheError();
    }
    throw error;
  }
}

function isUtcTimestamp(value: string): boolean {
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) && value.endsWith("Z");
}

export function openDriverDatabase(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const open = indexedDB.open(DriverStopsDatabaseName, DriverDatabaseVersion);
    open.onupgradeneeded = () => {
      if (!open.result.objectStoreNames.contains(DriverStopsStoreName)) {
        open.result.createObjectStore(DriverStopsStoreName, {
          keyPath: "partitionKey",
        });
      }
      if (!open.result.objectStoreNames.contains(DriverOfflineOperationsStoreName)) {
        const operations = open.result.createObjectStore(
          DriverOfflineOperationsStoreName,
          { keyPath: ["partitionKey", "id"] },
        );
        operations.createIndex("by_partition", "partitionKey");
        operations.createIndex("by_partition_order", [
          "partitionKey",
          "orderId",
        ]);
        operations.createIndex("by_partition_status", [
          "partitionKey",
          "status",
        ]);
        operations.createIndex("by_partition_next_attempt", [
          "partitionKey",
          "nextAttemptAt",
        ]);
        operations.createIndex("by_partition_created", [
          "partitionKey",
          "createdAt",
          "id",
        ]);
      }
      if (!open.result.objectStoreNames.contains(DriverProofBlobsStoreName)) {
        const blobs = open.result.createObjectStore(DriverProofBlobsStoreName, {
          keyPath: ["partitionKey", "operationId"],
        });
        blobs.createIndex("by_partition", "partitionKey");
      }
      if (!open.result.objectStoreNames.contains(DriverSyncLeasesStoreName)) {
        open.result.createObjectStore(DriverSyncLeasesStoreName, {
          keyPath: "partitionKey",
        });
      }
    };
    open.onsuccess = () => resolve(open.result);
    open.onerror = () => reject(new DriverStopsCacheError());
    open.onblocked = () => reject(new DriverStopsCacheError());
  });
}

export function driverDatabaseRequest<T>(value: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    value.onsuccess = () => resolve(value.result);
    value.onerror = () => reject(new DriverStopsCacheError());
  });
}

export function driverDatabaseTransactionCompleted(
  transaction: IDBTransaction,
): Promise<void> {
  return new Promise((resolve, reject) => {
    transaction.oncomplete = () => resolve();
    transaction.onerror = () => reject(new DriverStopsCacheError());
    transaction.onabort = () => reject(new DriverStopsCacheError());
  });
}

function base64Url(value: Uint8Array): string {
  let binary = "";
  for (const byte of value) {
    binary += String.fromCharCode(byte);
  }
  return btoa(binary)
    .replaceAll("+", "-")
    .replaceAll("/", "_")
    .replaceAll("=", "");
}
