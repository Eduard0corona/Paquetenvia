import {
  DriverStopsContractError,
  parseDriverStops,
  type DriverStop,
} from "../contracts/driver-stop";
import type { DriverSession } from "../session/driver-session";

export const DriverStopsDatabaseName = "paquetenvia-driver-stops-v1";
export const DriverStopsStoreName = "snapshots";
export const DriverStopsSchemaVersion = 1 as const;

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
    const database = await openDatabase();
    try {
      const record = await request<PersistedSnapshot | undefined>(
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
    const database = await openDatabase();
    try {
      const transaction = database.transaction(DriverStopsStoreName, "readwrite");
      transaction.objectStore(DriverStopsStoreName).put({
        partitionKey: partition.key,
        ...validated,
        stops: validated.stops.map((stop) => ({ ...stop })),
      } satisfies PersistedSnapshot);
      await transactionCompleted(transaction);
    } finally {
      database.close();
    }
  }

  public async clearPartition(partition: DriverCachePartition): Promise<void> {
    const database = await openDatabase();
    try {
      const transaction = database.transaction(DriverStopsStoreName, "readwrite");
      transaction.objectStore(DriverStopsStoreName).delete(partition.key);
      await transactionCompleted(transaction);
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

function openDatabase(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const open = indexedDB.open(DriverStopsDatabaseName, DriverStopsSchemaVersion);
    open.onupgradeneeded = () => {
      if (!open.result.objectStoreNames.contains(DriverStopsStoreName)) {
        open.result.createObjectStore(DriverStopsStoreName, {
          keyPath: "partitionKey",
        });
      }
    };
    open.onsuccess = () => resolve(open.result);
    open.onerror = () => reject(new DriverStopsCacheError());
    open.onblocked = () => reject(new DriverStopsCacheError());
  });
}

function request<T>(value: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    value.onsuccess = () => resolve(value.result);
    value.onerror = () => reject(new DriverStopsCacheError());
  });
}

function transactionCompleted(transaction: IDBTransaction): Promise<void> {
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
