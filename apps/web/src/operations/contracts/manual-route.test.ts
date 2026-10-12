import { describe, expect, it } from "vitest";
import {
  isRouteCursor,
  parseManualRoute,
  parseManualRouteDetail,
  parseManualRoutePage,
  removeRouteStopConfirmation,
  routeDateText,
  routeName,
  routeStopsText,
  routeStopSubject,
} from "./manual-route";

const route = {
  id: "11111111-1111-1111-1111-111111111111",
  status: "DRAFT",
  version: 3,
  driver_id: "22222222-2222-2222-2222-222222222222",
  city_id: "33333333-3333-3333-3333-333333333333",
  service_area_id: null,
  scheduled_for: "2026-08-30",
  assignment_cost_cents_total: 9700,
  stop_count: 2,
};

describe("manual route contract", () => {
  it("accepts exact int64 route detail with contiguous server sequence", () => {
    const result = parseManualRouteDetail({ ...route, stops: [
      { id: "44444444-4444-4444-4444-444444444444", order_id: "66666666-6666-6666-6666-666666666666", sequence: 1, stop_type: "DELIVERY", status: "PENDING" },
      { id: "55555555-5555-5555-5555-555555555555", order_id: "77777777-7777-7777-7777-777777777777", sequence: 2, stop_type: "DELIVERY", status: "PENDING" },
    ] });
    expect(result.assignment_cost_cents_total).toBe(9700);
    expect(result.stops.map((stop) => stop.sequence)).toEqual([1, 2]);
  });

  it("rejects extra fields and a non-contiguous response", () => {
    expect(() => parseManualRoutePage({ items: [{ ...route, extra: true }], next_cursor: null })).toThrow();
    expect(() => parseManualRouteDetail({ ...route, stops: [
      { id: "44444444-4444-4444-4444-444444444444", order_id: "66666666-6666-6666-6666-666666666666", sequence: 2, stop_type: "DELIVERY", status: "PENDING" },
    ] })).toThrow();
  });

  it("accepts a real calendar date or none and fails closed on any other date (UI-PHASE3-ROUTES-NO-IDS-2026-10-12)", () => {
    expect(parseManualRoute(route).scheduled_for).toBe("2026-08-30");
    expect(parseManualRoute({ ...route, scheduled_for: null }).scheduled_for).toBeNull();
    for (const scheduled_for of ["2026-02-30", "2026-13-01", "2026-8-30", "30/08/2026", ""])
      expect(() => parseManualRoute({ ...route, scheduled_for }), scheduled_for).toThrow();
  });
});

describe("routes people read (UI-PHASE3-ROUTES-NO-IDS-2026-10-12)", () => {
  it("names a route by its date, never by an id or a YYYY-MM-DD value", () => {
    expect(routeName({ scheduled_for: "2026-10-12" })).toBe("Ruta del 12 de octubre de 2026");
    expect(routeName({ scheduled_for: "2027-01-01" })).toBe("Ruta del 1 de enero de 2027");
    expect(routeName({ scheduled_for: null })).toBe("Ruta sin fecha");
    expect(routeDateText("2026-08-30")).toBe("30 de agosto de 2026");
    expect(routeName({ scheduled_for: "2026-08-30" })).not.toMatch(/\d{4}-\d{2}-\d{2}/);
  });

  it("counts the stops in words", () => {
    expect(routeStopsText(0)).toBe("Sin paradas");
    expect(routeStopsText(1)).toBe("1 parada");
    expect(routeStopsText(12)).toBe("12 paradas");
  });

  it("names a stop by its guía, or by its position when the guía is not available", () => {
    expect(routeStopSubject({ sequence: 3 }, "ORD_AbCdEfGhIjKlMnOpQrStUv")).toBe("orden ORD_AbCdEfGhIjKlMnOpQrStUv");
    expect(routeStopSubject({ sequence: 3 }, null)).toBe("parada 3");
    const stop = {
      id: "44444444-4444-4444-4444-444444444444",
      order_id: "66666666-6666-6666-6666-666666666666",
      sequence: 1,
      stop_type: "RETURN",
      status: "PENDING",
    } as const;
    expect(removeRouteStopConfirmation(stop, "ORD_AbCdEfGhIjKlMnOpQrStUv")).toEqual({
      title: "¿Retirar parada?",
      description:
        "Se retirará de la ruta la parada 1 (devolución de la orden ORD_AbCdEfGhIjKlMnOpQrStUv). La orden quedará fuera " +
        "de esta ruta.",
      confirmLabel: "Retirar",
    });
  });

  it("accepts only listRoutes cursors (opaque base64 up to 256 characters)", () => {
    expect(isRouteCursor("QUJDRA==")).toBe(true);
    expect(isRouteCursor("a".repeat(254))).toBe(true);
    for (const value of ["", "a b", "abc?x=1", "a".repeat(257), "QUJD===", "ab-_"]) expect(isRouteCursor(value), value).toBe(false);
  });
});
