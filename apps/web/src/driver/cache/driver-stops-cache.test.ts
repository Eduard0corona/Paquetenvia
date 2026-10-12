import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import type { DriverSession } from "../session/driver-session";
import {
  withStopDetails,
  withoutStopDetails,
} from "../contracts/driver-stop.fixtures";
import {
  createDriverCachePartition,
  DriverDatabaseVersion,
  DriverDispatchContactStoreName,
  DriverOfflineOperationsStoreName,
  DriverProofBlobsStoreName,
  DriverStopsCacheError,
  DriverStopsDatabaseName,
  DriverStopsSchemaVersion,
  DriverStopsStoreName,
  DriverSyncLeasesStoreName,
  validateDriverStopsSnapshot,
} from "./driver-stops-cache";

const baseSession: DriverSession = {
  organizationId: "11111111-1111-1111-1111-111111111111",
  cacheNamespace: "opaque-session-0001",
  getAccessToken: () => "never-serialized",
};

describe("driver stop cache partition", () => {
  it("is stable for the same session and organization", async () => {
    expect(await createDriverCachePartition(baseSession)).toEqual(
      await createDriverCachePartition(baseSession),
    );
  });

  it("isolates organizations, sessions and identical order ids", async () => {
    const organizationB = await createDriverCachePartition({
      ...baseSession,
      organizationId: "22222222-2222-2222-2222-222222222222",
    });
    const sessionB = await createDriverCachePartition({
      ...baseSession,
      cacheNamespace: "opaque-session-0002",
    });
    const original = await createDriverCachePartition(baseSession);
    expect(organizationB.key).not.toBe(original.key);
    expect(sessionB.key).not.toBe(original.key);
    expect(new Set([original.key, organizationB.key, sessionB.key]).size).toBe(3);
  });

  it("does not expose session or organization in the technical key", async () => {
    const partition = await createDriverCachePartition(baseSession);
    expect(partition.key).not.toContain(baseSession.cacheNamespace);
    expect(partition.key).not.toContain(baseSession.organizationId);
    expect(partition.key).toMatch(/^[A-Za-z0-9_-]{43}$/);
  });

  it("upgrades the existing database to v5 with snapshot schema 2", () => {
    const source = readFileSync(
      resolve(process.cwd(), "src/driver/cache/driver-stops-cache.ts"),
      "utf8",
    );
    expect(DriverStopsDatabaseName).toBe("paquetenvia-driver-stops-v1");
    expect(DriverStopsStoreName).toBe("snapshots");
    expect(DriverStopsSchemaVersion).toBe(2);
    expect(DriverDatabaseVersion).toBe(5);
    expect(DriverDispatchContactStoreName).toBe("dispatch_contacts");
    expect(DriverOfflineOperationsStoreName).toBe("operations");
    expect(DriverProofBlobsStoreName).toBe("proof_blobs");
    expect(DriverSyncLeasesStoreName).toBe("sync_leases");
    expect(source).toContain("indexedDB.open");
    expect(source).toContain('createIndex("by_partition_order"');
    expect(source).toContain('createIndex("by_partition_status"');
    expect(source).toContain('createIndex("by_partition_next_attempt"');
    expect(source).toContain('createIndex("by_partition_created"');
    expect(source).not.toContain("localStorage");
    expect(source).not.toContain("sessionStorage");
  });
});

describe("saved stop snapshot (UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11)", () => {
  const partitionKey = "P".repeat(43);
  const stop = withStopDetails({
    order_id: "33333333-3333-4333-8333-333333333333",
    aggregate_version: 2,
    order_public_id: "ORD_SYNTHETIC",
    stop_type: "DELIVERY",
    status: "IN_TRANSIT",
    address_summary: "Zona sintética",
  });
  const snapshot = {
    partitionKey,
    schemaVersion: DriverStopsSchemaVersion,
    synchronizedAt: "2026-10-11T20:00:00.000Z",
    stops: [stop, withoutStopDetails({ ...stop, order_id: "44444444-4444-4444-8444-444444444444" })],
    completedToday: 2,
  };

  it("keeps today's completed count and stops with or without details", () => {
    expect(validateDriverStopsSnapshot(snapshot, partitionKey)).toEqual({
      schemaVersion: DriverStopsSchemaVersion,
      synchronizedAt: snapshot.synchronizedAt,
      stops: snapshot.stops,
      completedToday: 2,
    });
    expect(
      validateDriverStopsSnapshot({ ...snapshot, completedToday: null }, partitionKey)
        .completedToday,
    ).toBeNull();
  });

  it.each([
    ["a negative count", { ...snapshot, completedToday: -1 }],
    ["a fractional count", { ...snapshot, completedToday: 0.5 }],
    ["a count as text", { ...snapshot, completedToday: "2" }],
    ["no count", withoutCount(snapshot)],
    ["another partition", { ...snapshot, partitionKey: "Q".repeat(43) }],
    ["a stop of an older shape", { ...snapshot, stops: [{ ...stop, details_available: undefined }] }],
  ])("never shows a snapshot with %s", (_name, value) => {
    expect(() => validateDriverStopsSnapshot(value, partitionKey)).toThrow(
      DriverStopsCacheError,
    );
  });
});

function withoutCount(value: object): Record<string, unknown> {
  const copy: Record<string, unknown> = { ...value };
  delete copy.completedToday;
  return copy;
}
