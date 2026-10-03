import { describe, expect, it } from "vitest";
import {
  buildServiceWindow,
  formatServiceWindow,
  mazatlanWallTimeToInstant,
  serviceWindowMessages,
} from "./service-window";

// ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02. 10:00 in Mazatlan (UTC-7, no DST since 2022).
const now = new Date("2026-10-02T17:00:00Z");

describe("Mazatlan wall-clock conversion", () => {
  it.each([
    ["2026-10-02T12:00", "2026-10-02T19:00:00.000Z"],
    ["2026-10-02T23:30", "2026-10-03T06:30:00.000Z"],
    ["2026-01-15T08:15", "2026-01-15T15:15:00.000Z"],
    ["2026-07-15T08:15:30", "2026-07-15T15:15:30.000Z"],
  ])("reads %s as America/Mazatlan, never the browser zone", (wall, instant) => {
    expect(mazatlanWallTimeToInstant(wall)?.toISOString()).toBe(instant);
  });

  it.each(["", "2026-10-02", "2026-10-02 12:00", "2026-02-30T10:00", "2026-10-02T24:00", "2026-10-02T12:00Z", "x"])(
    "rejects %o",
    (text) => {
      expect(mazatlanWallTimeToInstant(text)).toBeNull();
    },
  );
});

describe("service window body", () => {
  it("sends nothing when both bounds are empty (the zone's schedule applies)", () => {
    expect(buildServiceWindow("", "  ", now)).toEqual({ ok: true, window: null });
    expect(buildServiceWindow(undefined, undefined, now)).toEqual({ ok: true, window: null });
  });

  it("sends UTC instants with whole seconds", () => {
    expect(buildServiceWindow("2026-10-02T12:00", "2026-10-02T14:30", now)).toEqual({
      ok: true,
      window: { from: "2026-10-02T19:00:00Z", to: "2026-10-02T21:30:00Z" },
    });
  });

  it("accepts a window crossing midnight and exactly 12 hours long (AI-04 has no same-day rule)", () => {
    expect(buildServiceWindow("2026-10-02T18:00", "2026-10-03T06:00", now)).toEqual({
      ok: true,
      window: { from: "2026-10-03T01:00:00Z", to: "2026-10-03T13:00:00Z" },
    });
  });

  it("accepts a start within the 5-minute clock tolerance", () => {
    expect(buildServiceWindow("2026-10-02T09:55", "2026-10-02T11:00", now).ok).toBe(true);
  });

  it.each([
    ["2026-10-02T12:00", "", serviceWindowMessages.incomplete],
    ["", "2026-10-02T12:00", serviceWindowMessages.incomplete],
    ["2026-10-02T25:00", "2026-10-02T26:00", serviceWindowMessages.invalid],
    ["2026-10-02T14:00", "2026-10-02T14:00", serviceWindowMessages.order],
    ["2026-10-02T14:00", "2026-10-02T12:00", serviceWindowMessages.order],
    ["2026-10-02T11:00", "2026-10-02T23:01", serviceWindowMessages.span],
    ["2026-10-02T07:00", "2026-10-02T10:00", serviceWindowMessages.ended],
    ["2026-10-02T09:54", "2026-10-02T11:00", serviceWindowMessages.past],
    ["2026-11-01T10:01", "2026-11-01T12:00", serviceWindowMessages.lead],
  ])("refuses %s to %s", (from, to, message) => {
    expect(buildServiceWindow(from, to, now)).toEqual({ ok: false, error: message });
  });
});

describe("service window display", () => {
  it("shows the zone schedule when there is no window and Mazatlan time otherwise", () => {
    expect(formatServiceWindow(null)).toBe("Horario de la zona");
    const text = formatServiceWindow({ from: "2026-10-02T19:00:00Z", to: "2026-10-02T21:30:00Z" });
    expect(text).toContain("12:00");
    expect(text).toContain("14:30");
  });
});
