import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { instantToMazatlanWallTime, mazatlanWallTimeToInstant, pilotTimeZone } from "./mazatlan-time";

// AI-02 meta.timezone. Mazatlan is UTC-7 all year since Mexico ended DST in 2022.
describe("America/Mazatlan wall-clock conversions", () => {
  const originalZone = process.env.TZ;
  afterEach(() => {
    if (originalZone === undefined) delete process.env.TZ;
    else process.env.TZ = originalZone;
  });

  it("names the pilot zone", () => {
    expect(pilotTimeZone).toBe("America/Mazatlan");
  });

  it.each([
    ["2026-10-05T08:15", "2026-10-05T15:15:00.000Z"],
    ["2026-10-05T23:30", "2026-10-06T06:30:00.000Z"],
    ["2026-01-15T00:00", "2026-01-15T07:00:00.000Z"],
    ["2022-07-01T12:00", "2022-07-01T18:00:00.000Z"],
  ])("reads %s as Mazatlan time", (wall, utc) => {
    expect(mazatlanWallTimeToInstant(wall)?.toISOString()).toBe(utc);
  });

  it.each(["America/New_York", "Asia/Tokyo", "UTC"])("ignores the device zone (%s)", (zone) => {
    process.env.TZ = zone;
    expect(mazatlanWallTimeToInstant("2026-10-05T08:15")?.toISOString()).toBe("2026-10-05T15:15:00.000Z");
    expect(instantToMazatlanWallTime("2026-10-05T15:15:00.000Z")).toBe("2026-10-05T08:15");
  });

  it("rejects malformed, impossible and skipped wall-clock times", () => {
    for (const text of ["", "x", "2026-10-05", "2026-10-05 08:15", "2026-02-30T10:00", "2026-10-05T24:00"])
      expect(mazatlanWallTimeToInstant(text)).toBeNull();
    // 2022-04-03 02:00 jumped to 03:00 in Mazatlan.
    expect(mazatlanWallTimeToInstant("2022-04-03T02:30")).toBeNull();
  });

  it("writes instants back as Mazatlan datetime-local values", () => {
    expect(instantToMazatlanWallTime(new Date("2026-10-06T06:30:00Z"))).toBe("2026-10-05T23:30");
    expect(instantToMazatlanWallTime(undefined)).toBe("");
    expect(instantToMazatlanWallTime("not a date")).toBe("");
  });
});

function sourceFiles(directory: string): string[] {
  return readdirSync(directory).flatMap((entry) => {
    const path = join(directory, entry);
    if (statSync(path).isDirectory()) return sourceFiles(path);
    return /\.tsx?$/.test(entry) && !/\.test\.tsx?$/.test(entry) ? [path] : [];
  });
}

describe("typed date and time values", () => {
  it("are never parsed in the device zone", () => {
    const screens = sourceFiles("src").filter((file) =>
      readFileSync(file, "utf8").includes('type="datetime-local"'),
    );
    expect(screens.length).toBeGreaterThan(0);
    for (const file of screens) {
      // Only `new Date()` (the current time) may appear where a datetime-local value is typed.
      expect(readFileSync(file, "utf8"), file).not.toMatch(/new Date\(\s*[^)\s]/);
    }
    for (const helper of [
      "src/operations/contracts/filter-datetime.ts",
      "src/operations/contracts/incident.ts",
      "src/operations/contracts/service-window.ts",
      "src/operations/components/operations-order-card.tsx",
    ])
      expect(readFileSync(helper, "utf8"), helper).toContain('from "../../lib/mazatlan-time"');
  });
});
