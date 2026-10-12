import { parseDispatchPhone } from "../contracts/dispatch-contact";
import {
  DriverDispatchContactStoreName,
  DriverStopsCacheError,
  driverDatabaseRequest,
  driverDatabaseTransactionCompleted,
  openDriverDatabase,
  type DriverCachePartition,
} from "./driver-stops-cache";

/** Version of a saved answer of getMyDispatchContact. */
export const DriverDispatchContactSchemaVersion = 1 as const;

/**
 * DRV-DISPATCH-CONTACT-2026-10-12: the last successful answer of
 * getMyDispatchContact of a partition (the session's namespace and
 * organization): the organization's dispatch number, or null when it has
 * none. Nothing else: no id beyond the opaque partition key and no personal
 * datum.
 */
export interface DriverDispatchContactCache {
  /** The saved answer of the partition: `{ phoneE164 }`, or null when nothing is saved. */
  readContact(partition: DriverCachePartition): Promise<SavedDriverDispatchContact | null>;
  replaceContact(partition: DriverCachePartition, phoneE164: string | null): Promise<void>;
  clearContact(partition: DriverCachePartition): Promise<void>;
}

export interface SavedDriverDispatchContact {
  readonly phoneE164: string | null;
}

interface PersistedDispatchContact {
  readonly partitionKey: string;
  readonly schemaVersion: typeof DriverDispatchContactSchemaVersion;
  readonly phoneE164: string | null;
}

export class IndexedDbDriverDispatchContactCache implements DriverDispatchContactCache {
  public async readContact(
    partition: DriverCachePartition,
  ): Promise<SavedDriverDispatchContact | null> {
    const database = await openDriverDatabase();
    try {
      if (!database.objectStoreNames.contains(DriverDispatchContactStoreName)) return null;
      const record = await driverDatabaseRequest<unknown>(
        database
          .transaction(DriverDispatchContactStoreName, "readonly")
          .objectStore(DriverDispatchContactStoreName)
          .get(partition.key),
      );
      return record === undefined
        ? null
        : validateDriverDispatchContact(record, partition.key);
    } finally {
      database.close();
    }
  }

  public async replaceContact(
    partition: DriverCachePartition,
    phoneE164: string | null,
  ): Promise<void> {
    const record: PersistedDispatchContact = {
      partitionKey: partition.key,
      schemaVersion: DriverDispatchContactSchemaVersion,
      phoneE164: parseDispatchPhone(phoneE164),
    };
    const database = await openDriverDatabase();
    try {
      if (!database.objectStoreNames.contains(DriverDispatchContactStoreName)) {
        throw new DriverStopsCacheError();
      }
      const transaction = database.transaction(DriverDispatchContactStoreName, "readwrite");
      transaction.objectStore(DriverDispatchContactStoreName).put(record);
      await driverDatabaseTransactionCompleted(transaction);
    } finally {
      database.close();
    }
  }

  public async clearContact(partition: DriverCachePartition): Promise<void> {
    const database = await openDriverDatabase();
    try {
      if (!database.objectStoreNames.contains(DriverDispatchContactStoreName)) return;
      const transaction = database.transaction(DriverDispatchContactStoreName, "readwrite");
      transaction.objectStore(DriverDispatchContactStoreName).delete(partition.key);
      await driverDatabaseTransactionCompleted(transaction);
    } finally {
      database.close();
    }
  }
}

/**
 * The strict check of a saved answer: exactly the partition key, schema 1
 * and a number of the AI-05 form or null. Anything else is never used (no
 * number is known and no button is shown).
 */
export function validateDriverDispatchContact(
  value: unknown,
  expectedPartitionKey: string,
): SavedDriverDispatchContact {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw new DriverStopsCacheError();
  }
  const record = value as Record<string, unknown>;
  const keys = Object.keys(record).sort();
  if (
    keys.length !== 3 ||
    keys[0] !== "partitionKey" ||
    keys[1] !== "phoneE164" ||
    keys[2] !== "schemaVersion" ||
    record.partitionKey !== expectedPartitionKey ||
    record.schemaVersion !== DriverDispatchContactSchemaVersion
  ) {
    throw new DriverStopsCacheError();
  }
  try {
    return Object.freeze({ phoneE164: parseDispatchPhone(record.phoneE164) });
  } catch {
    throw new DriverStopsCacheError();
  }
}
