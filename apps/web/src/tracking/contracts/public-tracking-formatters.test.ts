import { describe, expect, it } from "vitest";
import {
  formatPublicTrackingEstimatedWindow,
  formatPublicTrackingTimestamp,
  PUBLIC_TRACKING_TIME_ZONE,
} from "./public-tracking-formatters";

const publicControl = new Intl.DateTimeFormat("es-MX", {
  dateStyle: "medium",
  timeStyle: "short",
  timeZone: PUBLIC_TRACKING_TIME_ZONE,
  hourCycle: "h23",
});

const numericControl = new Intl.DateTimeFormat("en-US", {
  year: "numeric",
  month: "numeric",
  day: "numeric",
  hour: "numeric",
  minute: "2-digit",
  timeZone: PUBLIC_TRACKING_TIME_ZONE,
  hourCycle: "h23",
});

function numericParts(value: string): Record<string, number> {
  return Object.fromEntries(
    numericControl
      .formatToParts(new Date(value))
      .filter(({ type }) =>
        ["year", "month", "day", "hour", "minute"].includes(type),
      )
      .map(({ type, value: part }) => [type, Number(part)]),
  );
}

describe("public tracking timestamp formatter", () => {
  it("uses the single contractual IANA time zone", () => {
    expect(PUBLIC_TRACKING_TIME_ZONE).toBe("America/Mazatlan");
  });

  it("renders a UTC instant on the previous Mazatlan calendar day", () => {
    const value = "2026-01-01T06:30:00.000Z";

    expect(numericParts(value)).toEqual({
      month: 12,
      day: 31,
      year: 2025,
      hour: 23,
      minute: 30,
    });
    expect(formatPublicTrackingTimestamp(value)).toBe(
      publicControl.format(new Date(value)),
    );
  });

  it("renders the summer instant as midnight-thirty in Mazatlan", () => {
    const value = "2026-07-27T07:30:00.000Z";

    expect(numericParts(value)).toEqual({
      month: 7,
      day: 27,
      year: 2026,
      hour: 0,
      minute: 30,
    });
    expect(formatPublicTrackingTimestamp(value)).toBe(
      publicControl.format(new Date(value)),
    );
  });

  it("does not inherit a different recipient time zone", () => {
    const value = "2026-01-01T06:30:00.000Z";
    const newYorkControl = new Intl.DateTimeFormat("es-MX", {
      dateStyle: "medium",
      timeStyle: "short",
      timeZone: "America/New_York",
      hourCycle: "h23",
    });

    const actual = formatPublicTrackingTimestamp(value);
    expect(actual).toBe(publicControl.format(new Date(value)));
    expect(actual).not.toBe(newYorkControl.format(new Date(value)));
  });

  it("uses the same policy for window bounds and Date values", () => {
    const from = "2026-01-01T06:30:00.000Z";
    const to = "2026-01-01T07:15:00.000Z";
    const summer = new Date("2026-07-27T07:30:00.000Z");

    expect(formatPublicTrackingEstimatedWindow({ from, to })).toBe(
      `${formatPublicTrackingTimestamp(from)} – ${formatPublicTrackingTimestamp(to)}`,
    );
    expect(formatPublicTrackingTimestamp(summer)).toBe(
      publicControl.format(summer),
    );
  });

  it("fails closed for invalid timestamps and windows", () => {
    expect(() => formatPublicTrackingTimestamp("not-a-timestamp")).toThrow(
      "Invalid public tracking timestamp.",
    );
    expect(() => formatPublicTrackingTimestamp(new Date(Number.NaN))).toThrow(
      "Invalid public tracking timestamp.",
    );
    expect(
      formatPublicTrackingEstimatedWindow({
        from: "not-a-timestamp",
        to: "2026-01-01T07:15:00.000Z",
      }),
    ).toBeNull();
  });
});
