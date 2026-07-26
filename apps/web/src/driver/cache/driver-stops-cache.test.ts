import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import type { DriverSession } from "../session/driver-session";
import {
  createDriverCachePartition,
  DriverStopsDatabaseName,
  DriverStopsSchemaVersion,
  DriverStopsStoreName,
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

  it("uses one native IndexedDB database/store and schema version", () => {
    const source = readFileSync(
      resolve(process.cwd(), "src/driver/cache/driver-stops-cache.ts"),
      "utf8",
    );
    expect(DriverStopsDatabaseName).toBe("paquetenvia-driver-stops-v1");
    expect(DriverStopsStoreName).toBe("snapshots");
    expect(DriverStopsSchemaVersion).toBe(1);
    expect(source).toContain("indexedDB.open");
    expect(source).not.toContain("localStorage");
    expect(source).not.toContain("sessionStorage");
  });
});
