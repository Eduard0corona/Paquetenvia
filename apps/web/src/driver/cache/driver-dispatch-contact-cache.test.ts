import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  DriverDispatchContactSchemaVersion,
  IndexedDbDriverDispatchContactCache,
  validateDriverDispatchContact,
} from "./driver-dispatch-contact-cache";
import { IndexedDbDriverOrganizationRolesCache } from "./driver-roles-cache";
import {
  DriverDispatchContactStoreName,
  DriverStopsCacheError,
  DriverStopsDatabaseName,
  DriverStopsStoreName,
  IndexedDbDriverStopsCache,
  forgetDriverStopDetails,
  openDriverDatabase,
} from "./driver-stops-cache";
import { FakeIndexedDb, fakeIdbKeyRange } from "./fake-indexed-db.fixtures";

/**
 * DRV-DISPATCH-CONTACT-2026-10-12: the phone keeps the last answer of
 * getMyDispatchContact per partition (the number, or null), so "Llamar a
 * despacho" keeps working offline, and drops it with the partition (401 or
 * 403) and on logout. The numbers are synthetic.
 */
const syntheticNumber = "+522000000001";
const otherNumber = "+522000000002";
const partition = { key: "D".repeat(43) };
const other = { key: "E".repeat(43) };
let idb: FakeIndexedDb;

beforeEach(() => {
  idb = new FakeIndexedDb();
  vi.stubGlobal("indexedDB", idb);
  vi.stubGlobal("IDBKeyRange", fakeIdbKeyRange);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("saved dispatch contact", () => {
  it("is created with the version 5 database and keeps the number (or none) only, per partition", async () => {
    const cache = new IndexedDbDriverDispatchContactCache();
    expect(await cache.readContact(partition)).toBeNull();

    await cache.replaceContact(partition, syntheticNumber);
    await cache.replaceContact(other, null);

    expect(await cache.readContact(partition)).toEqual({ phoneE164: syntheticNumber });
    expect(await cache.readContact(other)).toEqual({ phoneE164: null });
    expect(idb.records(DriverStopsDatabaseName, DriverDispatchContactStoreName)).toEqual([
      { partitionKey: partition.key, schemaVersion: DriverDispatchContactSchemaVersion, phoneE164: syntheticNumber },
      { partitionKey: other.key, schemaVersion: DriverDispatchContactSchemaVersion, phoneE164: null },
    ]);

    await cache.replaceContact(partition, otherNumber);
    expect(await cache.readContact(partition)).toEqual({ phoneE164: otherNumber });
    await cache.replaceContact(partition, null);
    expect(await cache.readContact(partition)).toEqual({ phoneE164: null });
    await cache.clearContact(partition);
    expect(await cache.readContact(partition)).toBeNull();
    expect(await cache.readContact(other)).toEqual({ phoneE164: null });
  });

  it("never stores something that is not a number of the AI-05 form", async () => {
    const cache = new IndexedDbDriverDispatchContactCache();
    await expect(cache.replaceContact(partition, "+52 200 000 0001")).rejects.toThrow();
    await expect(cache.replaceContact(partition, "Despacho")).rejects.toThrow();
    expect(await cache.readContact(partition)).toBeNull();
  });

  it.each<[string, unknown]>([
    ["another partition", { partitionKey: other.key, schemaVersion: 1, phoneE164: syntheticNumber }],
    ["another schema", { partitionKey: partition.key, schemaVersion: 2, phoneE164: syntheticNumber }],
    ["an extra member", { partitionKey: partition.key, schemaVersion: 1, phoneE164: syntheticNumber, name: "x" }],
    ["no number member", { partitionKey: partition.key, schemaVersion: 1 }],
    ["a number of another form", { partitionKey: partition.key, schemaVersion: 1, phoneE164: "2000000001" }],
    ["a list", [syntheticNumber]],
  ])("is never used when the record has %s", (_name, record) => {
    expect(() => validateDriverDispatchContact(record, partition.key)).toThrow(DriverStopsCacheError);
  });

  it("leaves the phone with its partition on 401 or 403, and every partition's on logout", async () => {
    const contacts = new IndexedDbDriverDispatchContactCache();
    const roles = new IndexedDbDriverOrganizationRolesCache();
    const stops = new IndexedDbDriverStopsCache();
    await contacts.replaceContact(partition, syntheticNumber);
    await contacts.replaceContact(other, syntheticNumber);
    await roles.replaceRoles(partition, ["DRIVER"]);
    await stops.replaceSnapshot(partition, {
      schemaVersion: 2,
      synchronizedAt: "2026-10-12T18:00:00.000Z",
      stops: [],
      completedToday: null,
    });

    await stops.clearPartition(partition);

    expect(await contacts.readContact(partition)).toBeNull();
    expect(await roles.readRoles(partition)).toBeNull();
    expect(await stops.readSnapshot(partition)).toBeNull();
    expect(await contacts.readContact(other)).toEqual({ phoneE164: syntheticNumber });

    await forgetDriverStopDetails();

    expect(await contacts.readContact(other)).toBeNull();
    expect(idb.records(DriverStopsDatabaseName, DriverDispatchContactStoreName)).toEqual([]);
    expect(idb.records(DriverStopsDatabaseName, DriverStopsStoreName)).toEqual([]);
  });

  it("reads as none, and is never written, in a database without the store", async () => {
    const database = await openDriverDatabase();
    database.close();
    const state = (idb as unknown as { databases: Map<string, { schemas: Map<string, unknown> }> }).databases
      .get(DriverStopsDatabaseName)!;
    state.schemas.delete(DriverDispatchContactStoreName);

    const contacts = new IndexedDbDriverDispatchContactCache();
    expect(await contacts.readContact(partition)).toBeNull();
    await expect(contacts.replaceContact(partition, syntheticNumber)).rejects.toThrow(DriverStopsCacheError);
    await expect(contacts.clearContact(partition)).resolves.toBeUndefined();
    await expect(new IndexedDbDriverStopsCache().clearPartition(partition)).resolves.toBeUndefined();
    await expect(forgetDriverStopDetails()).resolves.toBeUndefined();
  });
});
