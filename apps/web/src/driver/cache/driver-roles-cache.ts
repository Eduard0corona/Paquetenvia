import { parseOrganizationRoleCodes } from "../contracts/organization-roles";
import {
  DriverOrganizationRolesStoreName,
  DriverStopsCacheError,
  driverDatabaseRequest,
  driverDatabaseTransactionCompleted,
  openDriverDatabase,
  type DriverCachePartition,
} from "./driver-stops-cache";

/** Version of a saved answer of getMyOrganizationRoles. */
export const DriverOrganizationRolesSchemaVersion = 1 as const;

/**
 * ORG-MY-ROLES-2026-10-12: the last successful answer of getMyOrganizationRoles
 * of a partition (the session's namespace and organization). It holds role
 * codes only: no id beyond the opaque partition key, no name and no other
 * personal datum.
 */
export interface DriverOrganizationRolesCache {
  /** The saved roles of the partition; null when none is saved. */
  readRoles(partition: DriverCachePartition): Promise<readonly string[] | null>;
  replaceRoles(partition: DriverCachePartition, roles: readonly string[]): Promise<void>;
  clearRoles(partition: DriverCachePartition): Promise<void>;
}

interface PersistedOrganizationRoles {
  readonly partitionKey: string;
  readonly schemaVersion: typeof DriverOrganizationRolesSchemaVersion;
  readonly roles: readonly string[];
}

export class IndexedDbDriverOrganizationRolesCache implements DriverOrganizationRolesCache {
  public async readRoles(partition: DriverCachePartition): Promise<readonly string[] | null> {
    const database = await openDriverDatabase();
    try {
      if (!database.objectStoreNames.contains(DriverOrganizationRolesStoreName)) return null;
      const record = await driverDatabaseRequest<unknown>(
        database
          .transaction(DriverOrganizationRolesStoreName, "readonly")
          .objectStore(DriverOrganizationRolesStoreName)
          .get(partition.key),
      );
      return record === undefined ? null : validateDriverOrganizationRoles(record, partition.key);
    } finally {
      database.close();
    }
  }

  public async replaceRoles(partition: DriverCachePartition, roles: readonly string[]): Promise<void> {
    const record: PersistedOrganizationRoles = {
      partitionKey: partition.key,
      schemaVersion: DriverOrganizationRolesSchemaVersion,
      roles: [...parseOrganizationRoleCodes(roles)],
    };
    const database = await openDriverDatabase();
    try {
      if (!database.objectStoreNames.contains(DriverOrganizationRolesStoreName)) {
        throw new DriverStopsCacheError();
      }
      const transaction = database.transaction(DriverOrganizationRolesStoreName, "readwrite");
      transaction.objectStore(DriverOrganizationRolesStoreName).put(record);
      await driverDatabaseTransactionCompleted(transaction);
    } finally {
      database.close();
    }
  }

  public async clearRoles(partition: DriverCachePartition): Promise<void> {
    const database = await openDriverDatabase();
    try {
      if (!database.objectStoreNames.contains(DriverOrganizationRolesStoreName)) return;
      const transaction = database.transaction(DriverOrganizationRolesStoreName, "readwrite");
      transaction.objectStore(DriverOrganizationRolesStoreName).delete(partition.key);
      await driverDatabaseTransactionCompleted(transaction);
    } finally {
      database.close();
    }
  }
}

/**
 * The strict check of a saved answer: exactly the partition key, schema 1 and
 * a non-empty list of unique role codes. Anything else is never used (the
 * roles are then unknown and the report is not offered).
 */
export function validateDriverOrganizationRoles(
  value: unknown,
  expectedPartitionKey: string,
): readonly string[] {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw new DriverStopsCacheError();
  }
  const record = value as Record<string, unknown>;
  const keys = Object.keys(record).sort();
  if (
    keys.length !== 3 ||
    keys[0] !== "partitionKey" ||
    keys[1] !== "roles" ||
    keys[2] !== "schemaVersion" ||
    record.partitionKey !== expectedPartitionKey ||
    record.schemaVersion !== DriverOrganizationRolesSchemaVersion
  ) {
    throw new DriverStopsCacheError();
  }
  try {
    return parseOrganizationRoleCodes(record.roles);
  } catch {
    throw new DriverStopsCacheError();
  }
}
