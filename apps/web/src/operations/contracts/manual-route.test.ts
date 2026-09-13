import { describe, expect, it } from "vitest";
import { parseManualRouteDetail, parseManualRoutePage } from "./manual-route";

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
});
