import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  DriverOrganizationRolesSchemaVersion,
  IndexedDbDriverOrganizationRolesCache,
  validateDriverOrganizationRoles,
} from "./driver-roles-cache";
import {
  DriverOrganizationRolesStoreName,
  DriverStopsCacheError,
  DriverStopsDatabaseName,
  DriverStopsStoreName,
  IndexedDbDriverStopsCache,
  forgetDriverStopDetails,
  openDriverDatabase,
} from "./driver-stops-cache";
import { FakeIndexedDb, fakeIdbKeyRange } from "./fake-indexed-db.fixtures";

/**
 * ORG-MY-ROLES-2026-10-12: the phone keeps the last answer of
 * getMyOrganizationRoles per partition (role codes only), so the report keeps
 * working offline, and drops it with the partition (401 or 403) and on logout.
 */
const partition = { key: "R".repeat(43) };
const other = { key: "S".repeat(43) };
let idb: FakeIndexedDb;

beforeEach(() => {
  idb = new FakeIndexedDb();
  vi.stubGlobal("indexedDB", idb);
  vi.stubGlobal("IDBKeyRange", fakeIdbKeyRange);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("saved organization roles", () => {
  it("are created with the version 4 database and keep role codes only, per partition", async () => {
    const cache = new IndexedDbDriverOrganizationRolesCache();
    expect(await cache.readRoles(partition)).toBeNull();

    await cache.replaceRoles(partition, ["DISPATCHER", "DRIVER"]);
    await cache.replaceRoles(other, ["DRIVER"]);

    expect(await cache.readRoles(partition)).toEqual(["DISPATCHER", "DRIVER"]);
    expect(await cache.readRoles(other)).toEqual(["DRIVER"]);
    expect(idb.records(DriverStopsDatabaseName, DriverOrganizationRolesStoreName)).toEqual([
      { partitionKey: partition.key, schemaVersion: DriverOrganizationRolesSchemaVersion, roles: ["DISPATCHER", "DRIVER"] },
      { partitionKey: other.key, schemaVersion: DriverOrganizationRolesSchemaVersion, roles: ["DRIVER"] },
    ]);

    await cache.replaceRoles(partition, ["DRIVER"]);
    expect(await cache.readRoles(partition)).toEqual(["DRIVER"]);
    await cache.clearRoles(partition);
    expect(await cache.readRoles(partition)).toBeNull();
    expect(await cache.readRoles(other)).toEqual(["DRIVER"]);
  });

  it("never stores something that is not a list of role codes", async () => {
    const cache = new IndexedDbDriverOrganizationRolesCache();
    await expect(cache.replaceRoles(partition, [])).rejects.toThrow();
    await expect(cache.replaceRoles(partition, ["Ana López"])).rejects.toThrow();
    expect(await cache.readRoles(partition)).toBeNull();
  });

  it.each<[string, unknown]>([
    ["another partition", { partitionKey: other.key, schemaVersion: 1, roles: ["DRIVER"] }],
    ["another schema", { partitionKey: partition.key, schemaVersion: 2, roles: ["DRIVER"] }],
    ["an extra member", { partitionKey: partition.key, schemaVersion: 1, roles: ["DRIVER"], organizationId: "x" }],
    ["no roles", { partitionKey: partition.key, schemaVersion: 1, roles: [] }],
    ["repeated roles", { partitionKey: partition.key, schemaVersion: 1, roles: ["DRIVER", "DRIVER"] }],
    ["a name", { partitionKey: partition.key, schemaVersion: 1, roles: ["Ana"] }],
  ])("are never used when the record has %s", (_name, record) => {
    expect(() => validateDriverOrganizationRoles(record, partition.key)).toThrow(DriverStopsCacheError);
  });

  it("leave the phone with their partition on 401 or 403, and every partition's on logout", async () => {
    const roles = new IndexedDbDriverOrganizationRolesCache();
    const stops = new IndexedDbDriverStopsCache();
    await roles.replaceRoles(partition, ["DRIVER"]);
    await roles.replaceRoles(other, ["DRIVER"]);
    await stops.replaceSnapshot(partition, {
      schemaVersion: 2,
      synchronizedAt: "2026-10-12T18:00:00.000Z",
      stops: [],
      completedToday: null,
    });

    await stops.clearPartition(partition);

    expect(await roles.readRoles(partition)).toBeNull();
    expect(await stops.readSnapshot(partition)).toBeNull();
    expect(await roles.readRoles(other)).toEqual(["DRIVER"]);

    await forgetDriverStopDetails();

    expect(await roles.readRoles(other)).toBeNull();
    expect(idb.records(DriverStopsDatabaseName, DriverOrganizationRolesStoreName)).toEqual([]);
    expect(idb.records(DriverStopsDatabaseName, DriverStopsStoreName)).toEqual([]);
  });

  it("read as unknown, and are never written, in a version 4 database that predates the store", async () => {
    // A development build of version 4 without the store (never released): the roles are unknown and the
    // snapshot is still cleared.
    const database = await openDriverDatabase();
    database.close();
    const state = (idb as unknown as { databases: Map<string, { schemas: Map<string, unknown> }> }).databases
      .get(DriverStopsDatabaseName)!;
    state.schemas.delete(DriverOrganizationRolesStoreName);

    const roles = new IndexedDbDriverOrganizationRolesCache();
    expect(await roles.readRoles(partition)).toBeNull();
    await expect(roles.replaceRoles(partition, ["DRIVER"])).rejects.toThrow(DriverStopsCacheError);
    await expect(roles.clearRoles(partition)).resolves.toBeUndefined();
    await expect(new IndexedDbDriverStopsCache().clearPartition(partition)).resolves.toBeUndefined();
    await expect(forgetDriverStopDetails()).resolves.toBeUndefined();
  });
});
