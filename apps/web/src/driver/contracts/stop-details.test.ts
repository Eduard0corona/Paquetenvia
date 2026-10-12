import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import {
  completedTodayAt,
  driverStopActionVerb,
  driverStopContactName,
  driverStopLead,
  driverStopsDayLabel,
  formatCoordinate,
  formatDriverServiceWindow,
  googleMapsSearchUrl,
} from "./stop-details";

describe("Cómo llegar (Google Maps, GATE-003)", () => {
  it("opens the Google Maps search URL on the exact point, encoded", () => {
    expect(googleMapsSearchUrl({ latitude: 24.8067, longitude: -107.3938 })).toBe(
      "https://www.google.com/maps/search/?api=1&query=24.8067%2C-107.3938",
    );
  });

  it("writes plain decimals: no exponent, no trailing zeros, no negative zero", () => {
    expect(formatCoordinate(24.8)).toBe("24.8");
    expect(formatCoordinate(-107.39)).toBe("-107.39");
    expect(formatCoordinate(0.0000001)).toBe("0.0000001");
    expect(formatCoordinate(0.00000001)).toBe("0");
    expect(formatCoordinate(-0)).toBe("0");
    expect(formatCoordinate(90)).toBe("90");
    expect(formatCoordinate(-179.123456789)).toBe("-179.1234568");
    expect(() => formatCoordinate(Number.NaN)).toThrow(RangeError);
  });

  it("never asks Google (or anyone) for a route, distance or ETA from the driver app", () => {
    const sources = driverSources("src/driver").map((path) => readFileSync(path, "utf8"));
    for (const fragment of [
      "maps/dir",
      "directions",
      "distancematrix",
      "routes.googleapis.com",
      "computeRoutes",
      "maps.googleapis.com",
    ]) {
      for (const source of sources) {
        expect(source.toLowerCase()).not.toContain(fragment.toLowerCase());
      }
    }
    const component = readFileSync("src/driver/components/driver-stop-details.tsx", "utf8");
    // A top-level navigation in a new tab, never a fetch and never a referrer.
    expect(component).toContain("href={googleMapsSearchUrl(stop.navigation)}");
    expect(component).toContain('target="_blank"');
    expect(component).toContain('rel="noopener noreferrer"');
  });
});

describe("stop details presentation", () => {
  it("names what the driver does and with whom", () => {
    expect(driverStopActionVerb("DELIVERY")).toBe("Entregar");
    expect(driverStopActionVerb("PICKUP")).toBe("Recoger");
    expect(driverStopActionVerb("RETURN")).toBe("Devolver");
    expect(
      driverStopLead({
        stop_type: "DELIVERY",
        contact_short_name: "M. López",
        contact_full_name: null,
      }),
    ).toBe("Entregar a M. López");
    // Owner answer "Completo en recolección": a pickup reads the full name.
    expect(
      driverStopLead({
        stop_type: "PICKUP",
        contact_short_name: null,
        contact_full_name: "Farmacia Las Quintas",
      }),
    ).toBe("Recoger con Farmacia Las Quintas");
    // Owner answer "Completo, como recolección": a return reads the full name
    // of the sender, the same contact as the pickup.
    expect(
      driverStopLead({
        stop_type: "RETURN",
        contact_short_name: null,
        contact_full_name: "Farmacia Las Quintas",
      }),
    ).toBe("Devolver a Farmacia Las Quintas");
    expect(
      driverStopLead({ stop_type: "RETURN", contact_short_name: null, contact_full_name: null }),
    ).toBe("Devolver");
    expect(
      driverStopContactName({ contact_short_name: null, contact_full_name: "Farmacia Las Quintas" }),
    ).toBe("Farmacia Las Quintas");
    expect(
      driverStopContactName({ contact_short_name: "M. López", contact_full_name: null }),
    ).toBe("M. López");
    expect(driverStopContactName({ contact_short_name: null, contact_full_name: null })).toBeNull();
  });

  it("writes the day header with today's completed stops (owner answer Solo el número)", () => {
    expect(driverStopsDayLabel(3, 2)).toBe("Hoy · 5 paradas · 2 hechas");
    expect(driverStopsDayLabel(0, 1)).toBe("Hoy · 1 parada · 1 hecha");
    expect(driverStopsDayLabel(1, 0)).toBe("Hoy · 1 parada · 0 hechas");
    expect(driverStopsDayLabel(3, null)).toBe("Hoy · 3 paradas");
  });

  it("shows the completed count only on the Mazatlán day it was counted", () => {
    // 06:59Z is 23:59 of October 10 in Mazatlán (UTC-7); 07:00Z is 00:00 of October 11.
    const lateEvening = "2026-10-11T06:59:00.000Z";
    expect(completedTodayAt(lateEvening, 4, new Date("2026-10-11T06:59:59.000Z"))).toBe(4);
    expect(completedTodayAt(lateEvening, 4, new Date("2026-10-11T07:00:00.000Z"))).toBeNull();
    // Same UTC date, another Mazatlán day: the phone's own zone never decides.
    expect(
      completedTodayAt("2026-10-11T01:00:00.000Z", 3, new Date("2026-10-11T08:00:00.000Z")),
    ).toBeNull();
    expect(
      completedTodayAt("2026-10-11T07:00:00.000Z", 3, new Date("2026-10-12T06:59:00.000Z")),
    ).toBe(3);
    expect(completedTodayAt(null, 3, new Date("2026-10-11T08:00:00.000Z"))).toBeNull();
    expect(completedTodayAt(lateEvening, null, new Date("2026-10-11T06:59:59.000Z"))).toBeNull();
  });

  it("shows today's window in Mazatlán time as 13:00–15:00", () => {
    // 20:00Z and 22:00Z are 13:00 and 15:00 in America/Mazatlan (UTC-7).
    expect(
      formatDriverServiceWindow(
        { from: "2026-10-11T20:00:00.000Z", to: "2026-10-11T22:00:00.000Z" },
        new Date("2026-10-11T18:00:00.000Z"),
      ),
    ).toBe("13:00–15:00");
  });

  it("adds the day when the window is not today and both days across midnight", () => {
    const tomorrow = formatDriverServiceWindow(
      { from: "2026-10-12T20:00:00.000Z", to: "2026-10-12T22:00:00.000Z" },
      new Date("2026-10-11T18:00:00.000Z"),
    );
    expect(tomorrow).toMatch(/^12 .+, 13:00–15:00$/);
    const overnight = formatDriverServiceWindow(
      { from: "2026-10-12T05:00:00.000Z", to: "2026-10-12T09:00:00.000Z" },
      new Date("2026-10-11T18:00:00.000Z"),
    );
    expect(overnight).toMatch(/^11 .+, 22:00 – 12 .+, 02:00$/);
  });
});

function driverSources(directory: string): string[] {
  return readdirSync(directory).flatMap((name) => {
    const path = join(directory, name);
    if (statSync(path).isDirectory()) return driverSources(path);
    return /\.(ts|tsx)$/.test(name) && !name.includes(".test.") ? [path] : [];
  });
}
