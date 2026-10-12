import { afterEach, describe, expect, it } from "vitest";
import {
  createDriverOfflineOperation,
  parseDriverOfflineOperation,
} from "../offline/operation-contract";
import {
  DriverDatabaseVersion,
  DriverDispatchContactStoreName,
  DriverOfflineOperationsStoreName,
  DriverOrganizationRolesStoreName,
  DriverProofBlobsStoreName,
  DriverStopsDatabaseName,
  DriverStopsStoreName,
  DriverSyncLeasesStoreName,
  forgetDriverStopDetails,
  upgradeDriverDatabase,
} from "./driver-stops-cache";

/**
 * UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: IndexedDB version 3 keeps every
 * queued operation, drops the detail-less schema 1 snapshots, and "Cerrar
 * sesión" wipes the saved stop details. UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12:
 * version 4 rewrites every queued operation as schema 3 and keeps the schema 2
 * snapshots. DRV-DISPATCH-CONTACT-2026-10-12: version 5 only adds the dispatch
 * contact store. A minimal in-memory stand-in for the IndexedDB calls the module
 * makes is enough here; the real browser upgrade runs in the Playwright driver
 * suite.
 */
const partitionKey = "H".repeat(43);

describe("driver database version 5 upgrade", () => {
  it("creates every store on a fresh install, the organization roles and the dispatch contacts included", async () => {
    const database = new FakeDatabase();
    upgradeDriverDatabase(database.asDatabase(), database.asTransaction(), 0);
    await settle();
    expect(database.storeNames()).toEqual([
      DriverDispatchContactStoreName,
      DriverOfflineOperationsStoreName,
      DriverOrganizationRolesStoreName,
      DriverProofBlobsStoreName,
      DriverStopsStoreName,
      DriverSyncLeasesStoreName,
    ].sort());
    expect(DriverOrganizationRolesStoreName).toBe("organization_roles");
    expect(DriverDispatchContactStoreName).toBe("dispatch_contacts");
  });

  it("drops DRV-001 snapshots from version 1, which carry no stop details", async () => {
    const database = new FakeDatabase();
    database.seed(DriverStopsStoreName, "snapshot", legacySnapshot());
    upgradeDriverDatabase(database.asDatabase(), database.asTransaction(), 1);
    await settle();
    expect(database.records(DriverStopsStoreName)).toEqual([]);
    expect(database.storeNames()).toHaveLength(6);
  });

  it("keeps every queued operation, blob and lease from version 2 as schema 3", async () => {
    const database = new FakeDatabase();
    for (const name of [
      DriverStopsStoreName,
      DriverOfflineOperationsStoreName,
      DriverProofBlobsStoreName,
      DriverSyncLeasesStoreName,
    ]) {
      database.createObjectStore(name);
    }
    const queued = [legacyOperation("11111111-1111-4111-8111-111111111111", "CHECK_IN"), legacyOperation("22222222-2222-4222-8222-222222222222", "START_TRANSIT")];
    queued.forEach((operation, index) =>
      database.seed(DriverOfflineOperationsStoreName, `operation-${index}`, operation),
    );
    const blob = { schemaVersion: 1, partitionKey, operationId: "x" };
    const lease = { schemaVersion: 1, partitionKey, ownerId: "y" };
    database.seed(DriverProofBlobsStoreName, "blob", blob);
    database.seed(DriverSyncLeasesStoreName, "lease", lease);
    database.seed(DriverStopsStoreName, "snapshot", legacySnapshot());

    upgradeDriverDatabase(database.asDatabase(), database.asTransaction(), 2);
    await settle();

    const migrated = database.records(DriverOfflineOperationsStoreName);
    expect(migrated).toHaveLength(2);
    migrated.forEach((record, index) => {
      expect(record).toEqual({
        ...queued[index],
        schemaVersion: 3,
        cod: null,
        rejectionCode: null,
        failedDelivery: null,
      });
      expect(parseDriverOfflineOperation(record, partitionKey).id).toBe(queued[index].id);
    });
    expect(database.records(DriverProofBlobsStoreName)).toEqual([blob]);
    expect(database.records(DriverSyncLeasesStoreName)).toEqual([lease]);
    expect(database.records(DriverStopsStoreName)).toEqual([]);
  });

  it("keeps every queued operation and the stop snapshots from version 3, as schema 3", async () => {
    const database = new FakeDatabase();
    for (const name of [
      DriverStopsStoreName,
      DriverOfflineOperationsStoreName,
      DriverProofBlobsStoreName,
      DriverSyncLeasesStoreName,
    ]) {
      database.createObjectStore(name);
    }
    const queued = [
      schema2Operation("11111111-1111-4111-8111-111111111111", "START_DELIVERY"),
      schema2Operation("22222222-2222-4222-8222-222222222222", "COD_COLLECTION"),
    ];
    queued.forEach((operation, index) =>
      database.seed(DriverOfflineOperationsStoreName, `operation-${index}`, operation),
    );
    const snapshot = { partitionKey, schemaVersion: 2, synchronizedAt: "2026-10-12T19:00:00.000Z", stops: [], completedToday: 1 };
    database.seed(DriverStopsStoreName, "snapshot", snapshot);

    upgradeDriverDatabase(database.asDatabase(), database.asTransaction(), 3);
    await settle();

    const migrated = database.records(DriverOfflineOperationsStoreName);
    expect(migrated).toHaveLength(2);
    migrated.forEach((record, index) => {
      expect(record).toEqual({ ...queued[index], schemaVersion: 3, failedDelivery: null });
      expect(parseDriverOfflineOperation(record, partitionKey)).toMatchObject({
        id: queued[index].id,
        kind: queued[index].kind,
      });
    });
    expect(database.records(DriverStopsStoreName)).toEqual([snapshot]);
  });

  it("only adds the dispatch contact store from version 4, which already reached main (DRV-DISPATCH-CONTACT-2026-10-12)", async () => {
    const database = new FakeDatabase();
    for (const name of [
      DriverStopsStoreName,
      DriverOfflineOperationsStoreName,
      DriverProofBlobsStoreName,
      DriverSyncLeasesStoreName,
      DriverOrganizationRolesStoreName,
    ]) {
      database.createObjectStore(name);
    }
    const operation = {
      ...createDriverOfflineOperation({
        partitionKey,
        orderId: "33333333-3333-4333-8333-333333333333",
        kind: "START_DELIVERY",
        expectedVersion: 7,
        now: () => new Date("2026-10-12T19:00:00.000Z"),
        randomUuid: () => "11111111-1111-4111-8111-111111111111",
      }),
    };
    const snapshot = { partitionKey, schemaVersion: 2, synchronizedAt: "2026-10-12T19:00:00.000Z", stops: [], completedToday: 1 };
    const roles = { partitionKey, schemaVersion: 1, roles: ["DRIVER"] };
    const blob = { schemaVersion: 1, partitionKey, operationId: "x" };
    const lease = { schemaVersion: 1, partitionKey, ownerId: "y" };
    database.seed(DriverOfflineOperationsStoreName, "operation", operation);
    database.seed(DriverStopsStoreName, "snapshot", snapshot);
    database.seed(DriverOrganizationRolesStoreName, "roles", roles);
    database.seed(DriverProofBlobsStoreName, "blob", blob);
    database.seed(DriverSyncLeasesStoreName, "lease", lease);

    upgradeDriverDatabase(database.asDatabase(), database.asTransaction(), 4);
    await settle();

    expect(database.storeNames()).toHaveLength(6);
    expect(database.records(DriverDispatchContactStoreName)).toEqual([]);
    expect(database.records(DriverOfflineOperationsStoreName)).toEqual([operation]);
    expect(database.records(DriverStopsStoreName)).toEqual([snapshot]);
    expect(database.records(DriverOrganizationRolesStoreName)).toEqual([roles]);
    expect(database.records(DriverProofBlobsStoreName)).toEqual([blob]);
    expect(database.records(DriverSyncLeasesStoreName)).toEqual([lease]);
  });

  it("opens with version 5", () => {
    expect(DriverDatabaseVersion).toBe(5);
  });
});

function schema2Operation(id: string, kind: "START_DELIVERY" | "COD_COLLECTION") {
  const schema2: Record<string, unknown> = {
    ...createDriverOfflineOperation({
      partitionKey,
      orderId: "33333333-3333-4333-8333-333333333333",
      kind,
      expectedVersion: 7,
      cod:
        kind === "COD_COLLECTION"
          ? { amountCents: 125_000, reference: "ORD_X 2026-10-12T12:00:00-07:00" }
          : undefined,
      now: () => new Date("2026-10-12T19:00:00.000Z"),
      randomUuid: () => id,
    }),
    schemaVersion: 2,
  };
  // A UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11 (schema 2) record has no failedDelivery.
  delete schema2.failedDelivery;
  return schema2 as { readonly id: string; readonly kind: string } & Record<string, unknown>;
}

describe("Cerrar sesión wipes the saved stop details", () => {
  const original = globalThis.indexedDB;
  afterEach(() => {
    Object.defineProperty(globalThis, "indexedDB", { value: original, configurable: true });
  });

  it("clears every snapshot, every saved roles answer and every saved dispatch contact, and nothing else", async () => {
    const database = new FakeDatabase();
    for (const name of [
      DriverStopsStoreName,
      DriverOfflineOperationsStoreName,
      DriverProofBlobsStoreName,
      DriverSyncLeasesStoreName,
      DriverOrganizationRolesStoreName,
      DriverDispatchContactStoreName,
    ]) {
      database.createObjectStore(name);
    }
    database.seed(DriverStopsStoreName, "partition-a", { stops: ["a"] });
    database.seed(DriverStopsStoreName, "partition-b", { stops: ["b"] });
    database.seed(DriverOrganizationRolesStoreName, "partition-a", { roles: ["DRIVER"] });
    database.seed(DriverDispatchContactStoreName, "partition-a", { phoneE164: null });
    database.seed(DriverDispatchContactStoreName, "partition-b", { phoneE164: null });
    database.seed(DriverOfflineOperationsStoreName, "operation", { id: "kept" });
    const opened: [string, number][] = [];
    Object.defineProperty(globalThis, "indexedDB", {
      configurable: true,
      value: {
        open(name: string, version: number) {
          opened.push([name, version]);
          const request: { result?: unknown; onsuccess?: () => void } = {};
          queueMicrotask(() => {
            request.result = database.asDatabase();
            request.onsuccess?.();
          });
          return request;
        },
      },
    });

    await forgetDriverStopDetails();

    expect(opened).toEqual([[DriverStopsDatabaseName, DriverDatabaseVersion]]);
    expect(database.records(DriverStopsStoreName)).toEqual([]);
    expect(database.records(DriverOrganizationRolesStoreName)).toEqual([]);
    expect(database.records(DriverDispatchContactStoreName)).toEqual([]);
    expect(database.records(DriverOfflineOperationsStoreName)).toEqual([{ id: "kept" }]);
    expect(database.closed).toBe(true);
  });
});

function legacyOperation(id: string, kind: "CHECK_IN" | "START_TRANSIT") {
  const legacy: Record<string, unknown> = {
    ...createDriverOfflineOperation({
      partitionKey,
      orderId: "33333333-3333-4333-8333-333333333333",
      kind,
      expectedVersion: 3,
      now: () => new Date("2026-10-11T19:00:00.000Z"),
      randomUuid: () => id,
    }),
    schemaVersion: 1,
  };
  // A DRV-002 (schema 1) record has none of the later members.
  delete legacy.cod;
  delete legacy.rejectionCode;
  delete legacy.failedDelivery;
  return legacy as { readonly id: string } & Record<string, unknown>;
}

function legacySnapshot() {
  return {
    partitionKey,
    schemaVersion: 1,
    synchronizedAt: "2026-10-11T19:00:00.000Z",
    stops: [],
  };
}

async function settle(): Promise<void> {
  for (let index = 0; index < 10; index += 1) {
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
}

class FakeStore {
  public readonly records = new Map<string, unknown>();

  public createIndex(): void {}

  public clear(): void {
    this.records.clear();
  }

  public openCursor() {
    const request: { result: unknown; onsuccess?: () => void } = { result: null };
    const entries = [...this.records.keys()];
    let position = 0;
    const step = () =>
      queueMicrotask(() => {
        if (position >= entries.length) {
          request.result = null;
          request.onsuccess?.();
          return;
        }
        const key = entries[position];
        request.result = {
          value: this.records.get(key),
          update: (value: unknown) => {
            this.records.set(key, value);
          },
          continue: () => {
            position += 1;
            step();
          },
        };
        request.onsuccess?.();
      });
    step();
    return request;
  }
}

class FakeDatabase {
  private readonly stores = new Map<string, FakeStore>();
  public closed = false;

  public createObjectStore(name: string): FakeStore {
    const store = new FakeStore();
    this.stores.set(name, store);
    return store;
  }

  public seed(name: string, key: string, value: unknown): void {
    (this.stores.get(name) ?? this.createObjectStore(name)).records.set(key, value);
  }

  public records(name: string): unknown[] {
    return [...(this.stores.get(name)?.records.values() ?? [])];
  }

  public storeNames(): string[] {
    return [...this.stores.keys()].sort();
  }

  public asDatabase(): IDBDatabase {
    return {
      objectStoreNames: { contains: (name: string) => this.stores.has(name) },
      createObjectStore: (name: string) => this.createObjectStore(name),
      transaction: () => {
        const transaction: { oncomplete?: () => void; objectStore: (name: string) => FakeStore } = {
          objectStore: (name: string) => {
            const store = this.stores.get(name);
            if (!store) throw new Error(`missing store ${name}`);
            return store;
          },
        };
        setTimeout(() => transaction.oncomplete?.(), 0);
        return transaction;
      },
      close: () => {
        this.closed = true;
      },
    } as unknown as IDBDatabase;
  }

  public asTransaction(): IDBTransaction {
    return {
      objectStore: (name: string) => {
        const store = this.stores.get(name);
        if (!store) throw new Error(`missing store ${name}`);
        return store;
      },
    } as unknown as IDBTransaction;
  }
}
