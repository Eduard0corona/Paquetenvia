import {
  DriverStopsContractError,
  parseDriverStops,
  type DriverStop,
} from "../contracts/driver-stop";
import { migrateDriverOfflineOperationRecord } from "../offline/operation-contract";
import type { DriverSession } from "../session/driver-session";

export const DriverStopsDatabaseName = "paquetenvia-driver-stops-v1";
export const DriverStopsStoreName = "snapshots";
/**
 * 1: DRV-001 snapshots. 2: DRV-002 offline queue stores. 3:
 * UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11 snapshot schema 2 (stop details)
 * and operation schema 2 (cash collection); see upgradeDriverDatabase.
 */
export const DriverDatabaseVersion = 3 as const;
/**
 * Schema 2 snapshots carry the stop details (contact name, zone, window, cash
 * to collect and navigation target, or that they are unavailable) and the
 * count of today's completed stops. They exist only for the stops the last
 * successful REST response returned.
 */
export const DriverStopsSnapshotSchemaVersion = 2 as const;
/** @deprecated Use DriverStopsSnapshotSchemaVersion for persisted snapshots. */
export const DriverStopsSchemaVersion = DriverStopsSnapshotSchemaVersion;
export const DriverOfflineOperationsStoreName = "operations";
export const DriverProofBlobsStoreName = "proof_blobs";
export const DriverSyncLeasesStoreName = "sync_leases";

export interface DriverCachePartition {
  readonly key: string;
}

export interface DriverStopsSnapshot {
  readonly schemaVersion: typeof DriverStopsSnapshotSchemaVersion;
  readonly synchronizedAt: string;
  readonly stops: readonly DriverStop[];
  /**
   * The driver's stops completed on the Mazatlán day of `synchronizedAt`, as
   * the server counted them; null when no answer carried the count yet.
   */
  readonly completedToday: number | null;
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
    "completedToday",
  ]);
  if (
    keys.length !== allowed.size ||
    keys.some((key) => !allowed.has(key)) ||
    record.partitionKey !== expectedPartitionKey ||
    record.schemaVersion !== DriverStopsSchemaVersion ||
    typeof record.synchronizedAt !== "string" ||
    !isUtcTimestamp(record.synchronizedAt) ||
    (record.completedToday !== null &&
      (!Number.isSafeInteger(record.completedToday) ||
        (record.completedToday as number) < 0))
  ) {
    throw new DriverStopsCacheError();
  }

  try {
    return Object.freeze({
      schemaVersion: DriverStopsSchemaVersion,
      synchronizedAt: record.synchronizedAt,
      stops: parseDriverStops(record.stops),
      completedToday: record.completedToday as number | null,
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

/**
 * UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11 retention ("Se borra del teléfono
 * ... al cerrar sesión"): removes every saved stop snapshot, of every
 * partition, before signing out. Snapshots are only a cache that the next
 * successful REST response rebuilds; the offline queue is left as it is.
 */
export async function forgetDriverStopDetails(): Promise<void> {
  const database = await openDriverDatabase();
  try {
    const transaction = database.transaction(DriverStopsStoreName, "readwrite");
    transaction.objectStore(DriverStopsStoreName).clear();
    await driverDatabaseTransactionCompleted(transaction);
  } finally {
    database.close();
  }
}

export function openDriverDatabase(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const open = indexedDB.open(DriverStopsDatabaseName, DriverDatabaseVersion);
    open.onupgradeneeded = (event) => {
      const transaction = open.transaction;
      if (!transaction) {
        reject(new DriverStopsCacheError());
        return;
      }
      upgradeDriverDatabase(open.result, transaction, event.oldVersion);
    };
    open.onsuccess = () => resolve(open.result);
    open.onerror = () => reject(new DriverStopsCacheError());
    open.onblocked = () => reject(new DriverStopsCacheError());
  });
}

/**
 * Brings the database to {@link DriverDatabaseVersion} inside the version
 * change transaction, so it either completes or leaves the old version intact:
 * - creates any missing store (DRV-001 snapshots, DRV-002 queue stores);
 * - from version 1 or 2, drops the schema 1 snapshots: they carry no stop
 *   details and the next successful REST response replaces them;
 * - from version 2, rewrites every queued schema 1 operation as schema 2 in
 *   place, so no queued action, proof blob or lease is lost.
 */
export function upgradeDriverDatabase(
  database: IDBDatabase,
  transaction: IDBTransaction,
  oldVersion: number,
): void {
  createMissingStores(database);
  if (oldVersion >= 1 && oldVersion < 3) {
    transaction.objectStore(DriverStopsStoreName).clear();
  }
  if (oldVersion === 2) {
    const cursorRequest = transaction
      .objectStore(DriverOfflineOperationsStoreName)
      .openCursor();
    cursorRequest.onsuccess = () => {
      const cursor = cursorRequest.result;
      if (!cursor) return;
      const migrated = migrateDriverOfflineOperationRecord(cursor.value);
      if (migrated !== cursor.value) cursor.update(migrated);
      cursor.continue();
    };
  }
}

function createMissingStores(database: IDBDatabase): void {
  if (!database.objectStoreNames.contains(DriverStopsStoreName)) {
    database.createObjectStore(DriverStopsStoreName, {
      keyPath: "partitionKey",
    });
  }
  if (!database.objectStoreNames.contains(DriverOfflineOperationsStoreName)) {
    const operations = database.createObjectStore(
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
  if (!database.objectStoreNames.contains(DriverProofBlobsStoreName)) {
    const blobs = database.createObjectStore(DriverProofBlobsStoreName, {
      keyPath: ["partitionKey", "operationId"],
    });
    blobs.createIndex("by_partition", "partitionKey");
  }
  if (!database.objectStoreNames.contains(DriverSyncLeasesStoreName)) {
    database.createObjectStore(DriverSyncLeasesStoreName, {
      keyPath: "partitionKey",
    });
  }
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
