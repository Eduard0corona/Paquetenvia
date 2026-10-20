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
 * and operation schema 2 (cash collection). 4:
 * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12 operation schema 3 ("No se pudo
 * entregar") and the organization roles store (ORG-MY-ROLES-2026-10-12). 5:
 * the dispatch contact store (DRV-DISPATCH-CONTACT-2026-10-12); version 4
 * already reached main, so the new store needs its own version. See
 * upgradeDriverDatabase. A browser never opens the database below its current
 * version, so an app built for an older version cannot read (and therefore
 * cannot misread or drop) an operation it does not know.
 */
export const DriverDatabaseVersion = 5 as const;
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
/**
 * ORG-MY-ROLES-2026-10-12: the last successful answer of getMyOrganizationRoles
 * of each partition (role codes only), so "No se pudo entregar" keeps working
 * offline; see IndexedDbDriverOrganizationRolesCache. It leaves the phone with
 * its partition (403 or no usable credential; a 401 keeps it,
 * DRV-SESSION-EXPIRY-OFFLINE-2026-10-19) and on logout, like the stop snapshots.
 */
export const DriverOrganizationRolesStoreName = "organization_roles";
/**
 * DRV-DISPATCH-CONTACT-2026-10-12: the last successful answer of
 * getMyDispatchContact of each partition (the organization's dispatch number,
 * or that it has none), so "Llamar a despacho" keeps working offline; see
 * IndexedDbDriverDispatchContactCache. It leaves the phone with its partition
 * (403 or no usable credential; a 401 keeps it,
 * DRV-SESSION-EXPIRY-OFFLINE-2026-10-19) and on logout, like the stop snapshots.
 */
export const DriverDispatchContactStoreName = "dispatch_contacts";

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

/**
 * The upgrade to {@link DriverDatabaseVersion} waits for another tab that
 * still holds the database open (typically one of an older version). The
 * request fails now; the screen asks the driver to close the other
 * Paquetenvia tabs and reload.
 */
export class DriverDatabaseBlockedError extends DriverStopsCacheError {
  public constructor() {
    super();
    this.name = "DriverDatabaseBlockedError";
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
      return validateDriverStopsSnapshot(record, partition.key);
    } finally {
      database.close();
    }
  }

  public async replaceSnapshot(
    partition: DriverCachePartition,
    snapshot: DriverStopsSnapshot,
  ): Promise<void> {
    const validated = validateDriverStopsSnapshot(
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

  /**
   * 403 or a session without a usable credential (never a 401,
   * DRV-SESSION-EXPIRY-OFFLINE-2026-10-19): the partition's snapshot, its saved organization roles
   * (ORG-MY-ROLES-2026-10-12) and its saved dispatch contact
   * (DRV-DISPATCH-CONTACT-2026-10-12) leave together.
   */
  public async clearPartition(partition: DriverCachePartition): Promise<void> {
    const database = await openDriverDatabase();
    try {
      const stores = partitionCacheStores(database);
      const transaction = database.transaction(stores, "readwrite");
      for (const store of stores) {
        transaction.objectStore(store).delete(partition.key);
      }
      await driverDatabaseTransactionCompleted(transaction);
    } finally {
      database.close();
    }
  }
}

/**
 * The stores keyed by partition that only cache what the server answered: the
 * stop snapshots and, when the database has them, the organization roles
 * (since version 4) and the dispatch contacts (since version 5).
 */
function partitionCacheStores(database: IDBDatabase): string[] {
  return [
    DriverStopsStoreName,
    DriverOrganizationRolesStoreName,
    DriverDispatchContactStoreName,
  ].filter(
    (store) =>
      store === DriverStopsStoreName ||
      database.objectStoreNames.contains(store),
  );
}

/**
 * The strict check of a saved snapshot: exact members, this partition, schema 2,
 * a UTC time, every stop valid and a non-negative integer or null count of
 * today's completed stops. Anything else is never shown.
 */
export function validateDriverStopsSnapshot(
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
 * partition, before signing out, and with them every saved answer of
 * getMyOrganizationRoles (ORG-MY-ROLES-2026-10-12) and of getMyDispatchContact
 * (DRV-DISPATCH-CONTACT-2026-10-12). They are only a cache that the next
 * successful response rebuilds; the offline queue is left as it is.
 */
export async function forgetDriverStopDetails(): Promise<void> {
  const database = await openDriverDatabase();
  try {
    const stores = partitionCacheStores(database);
    const transaction = database.transaction(stores, "readwrite");
    for (const store of stores) {
      transaction.objectStore(store).clear();
    }
    await driverDatabaseTransactionCompleted(transaction);
  } finally {
    database.close();
  }
}

/**
 * Opens the database at {@link DriverDatabaseVersion}. Every connection closes
 * itself when a newer version asks for the database, so this tab never blocks
 * a later upgrade. When the upgrade is blocked by another tab the request
 * fails with DriverDatabaseBlockedError; the browser may still open the
 * database later, once that tab lets go, and that late connection is closed
 * at once instead of being left open by a request nobody waits for.
 */
export function openDriverDatabase(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    let settled = false;
    const fail = (error: DriverStopsCacheError) => {
      if (settled) return;
      settled = true;
      reject(error);
    };
    const open = indexedDB.open(DriverStopsDatabaseName, DriverDatabaseVersion);
    open.onupgradeneeded = (event) => {
      const transaction = open.transaction;
      if (!transaction) {
        fail(new DriverStopsCacheError());
        return;
      }
      upgradeDriverDatabase(open.result, transaction, event.oldVersion);
    };
    open.onsuccess = () => {
      const database = open.result;
      if (settled) {
        database.close();
        return;
      }
      settled = true;
      database.onversionchange = () => database.close();
      resolve(database);
    };
    open.onerror = () => fail(new DriverStopsCacheError());
    open.onblocked = () => fail(new DriverDatabaseBlockedError());
  });
}

/**
 * Brings the database to {@link DriverDatabaseVersion} inside the version
 * change transaction, so it either completes or leaves the old version intact:
 * - creates any missing store (DRV-001 snapshots, DRV-002 queue stores, the
 *   ORG-MY-ROLES-2026-10-12 organization roles, the
 *   DRV-DISPATCH-CONTACT-2026-10-12 dispatch contacts); from version 4 that is
 *   all the upgrade does, so every queued action, proof blob, lease, snapshot
 *   and saved roles answer stays as it is;
 * - from version 1 or 2, drops the schema 1 snapshots: they carry no stop
 *   details and the next successful REST response replaces them (the schema 2
 *   snapshots of version 3 are kept);
 * - from version 2 or 3, rewrites every queued schema 1 or 2 operation as
 *   schema 3 in place, so no queued action, proof blob or lease is lost.
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
  if (oldVersion === 2 || oldVersion === 3) {
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
  if (!database.objectStoreNames.contains(DriverOrganizationRolesStoreName)) {
    database.createObjectStore(DriverOrganizationRolesStoreName, {
      keyPath: "partitionKey",
    });
  }
  if (!database.objectStoreNames.contains(DriverDispatchContactStoreName)) {
    database.createObjectStore(DriverDispatchContactStoreName, {
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
