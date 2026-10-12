import { afterEach, describe, expect, it } from "vitest";
import {
  formatPeriodRange,
  lastFortnightPeriod,
  lastWeekPeriod,
  mazatlanToday,
  presetPeriod,
  settlementPeriodPresetLabels,
  settlementPeriodPresets,
} from "./settlement-period";

/**
 * UI-PHASE3-DRIVERS-2026-10-11: "Semana pasada" is Monday to Sunday of the previous week and "Quincena pasada" the
 * half month before the current one, both from today's date in America/Mazatlan (UTC-7 all year since 2022).
 */
const at = (utc: string) => new Date(utc);

describe("settlement period presets", () => {
  const originalZone = process.env.TZ;
  afterEach(() => {
    if (originalZone === undefined) delete process.env.TZ;
    else process.env.TZ = originalZone;
  });

  it("offers last week, last half month and typed dates, in that order", () => {
    expect(settlementPeriodPresets).toEqual(["last_week", "last_fortnight", "manual"]);
    expect(Object.values(settlementPeriodPresetLabels)).toEqual(["Semana pasada", "Quincena pasada", "Fechas a mano"]);
    expect(presetPeriod("manual", at("2026-10-11T18:00:00Z"))).toBeNull();
  });

  it.each([
    // Sunday 11 Oct 2026: the current week started Monday 5 Oct.
    ["2026-10-11T18:00:00Z", "2026-09-28", "2026-10-04"],
    // Monday 12 Oct 2026, first minute in Mazatlán.
    ["2026-10-12T07:00:00Z", "2026-10-05", "2026-10-11"],
    // Still Sunday 11 Oct at 23:59 in Mazatlán although UTC is already Monday.
    ["2026-10-12T06:59:00Z", "2026-09-28", "2026-10-04"],
    // Wednesday 14 Oct 2026.
    ["2026-10-14T15:00:00Z", "2026-10-05", "2026-10-11"],
    // Across a year end: Thursday 1 Jan 2026.
    ["2026-01-01T15:00:00Z", "2025-12-22", "2025-12-28"],
    // Across a month end: Tuesday 3 Mar 2026.
    ["2026-03-03T15:00:00Z", "2026-02-23", "2026-03-01"],
  ])("last week at %s is %s to %s", (now, from, to) => {
    expect(lastWeekPeriod(at(now))).toEqual({ period_from: from, period_to: to });
    expect(presetPeriod("last_week", at(now))).toEqual({ period_from: from, period_to: to });
  });

  it.each([
    // From the 1st to the 15th: the second half of the previous month.
    ["2026-10-11T18:00:00Z", "2026-09-16", "2026-09-30"],
    ["2026-10-01T07:00:00Z", "2026-09-16", "2026-09-30"],
    ["2026-10-15T18:00:00Z", "2026-09-16", "2026-09-30"],
    // From the 16th on: the first half of the current month.
    ["2026-10-16T07:00:00Z", "2026-10-01", "2026-10-15"],
    ["2026-10-31T18:00:00Z", "2026-10-01", "2026-10-15"],
    // Month ends of 31, 28 and 29 (leap year) days, and a year end.
    ["2026-08-02T18:00:00Z", "2026-07-16", "2026-07-31"],
    ["2027-03-10T18:00:00Z", "2027-02-16", "2027-02-28"],
    ["2028-03-03T18:00:00Z", "2028-02-16", "2028-02-29"],
    ["2027-01-05T18:00:00Z", "2026-12-16", "2026-12-31"],
    // 16 Oct 00:30 in UTC is still 15 Oct in Mazatlán.
    ["2026-10-16T06:30:00Z", "2026-09-16", "2026-09-30"],
  ])("last half month at %s is %s to %s", (now, from, to) => {
    expect(lastFortnightPeriod(at(now))).toEqual({ period_from: from, period_to: to });
  });

  it.each(["America/New_York", "Asia/Tokyo", "UTC", "Pacific/Kiritimati"])(
    "ignores the device zone (%s)",
    (zone) => {
      process.env.TZ = zone;
      expect(mazatlanToday(at("2026-10-12T06:59:00Z"))).toBe("2026-10-11");
      expect(lastWeekPeriod(at("2026-10-12T06:59:00Z"))).toEqual({ period_from: "2026-09-28", period_to: "2026-10-04" });
      expect(lastFortnightPeriod(at("2026-10-16T06:30:00Z"))).toEqual({
        period_from: "2026-09-16",
        period_to: "2026-09-30",
      });
    },
  );

  it("always ends before today, so a preset is a closed period", () => {
    for (let day = 0; day < 400; day += 1) {
      const now = new Date(Date.UTC(2026, 0, 1, 19) + day * 86_400_000);
      const today = mazatlanToday(now);
      for (const period of [lastWeekPeriod(now), lastFortnightPeriod(now)]) {
        expect(period.period_to < today, `${today} ${period.period_to}`).toBe(true);
        expect(period.period_from <= period.period_to).toBe(true);
      }
    }
  });

  it("refuses an unreadable instant", () => {
    expect(() => mazatlanToday(new Date(Number.NaN))).toThrow(RangeError);
  });
});

describe("readable period", () => {
  it.each([
    ["2026-09-01", "2026-09-07", "1–7 sep 2026"],
    ["2026-09-16", "2026-09-30", "16–30 sep 2026"],
    ["2026-09-28", "2026-10-04", "28 sep – 4 oct 2026"],
    ["2025-12-29", "2026-01-04", "29 dic 2025 – 4 ene 2026"],
    ["2026-09-01", "2026-09-01", "1 sep 2026"],
    ["2028-02-16", "2028-02-29", "16–29 feb 2028"],
  ])("%s to %s reads %s", (from, to, text) => {
    expect(formatPeriodRange(from, to)).toBe(text);
  });

  it("names every month with its es-MX abbreviation", () => {
    expect(
      Array.from({ length: 12 }, (_, month) => {
        const day = `2026-${String(month + 1).padStart(2, "0")}-01`;
        return formatPeriodRange(day, day).split(" ")[1];
      }),
    ).toEqual(["ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic"]);
  });

  it("writes an unreadable date as it came", () => {
    expect(formatPeriodRange("2026-02-30", "2026-03-01")).toBe("2026-02-30 a 2026-03-01");
  });
});
