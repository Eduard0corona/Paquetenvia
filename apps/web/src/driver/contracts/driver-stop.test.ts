import { describe, expect, it } from "vitest";
import {
  DriverStopsContractError,
  MaximumStopsPerResponse,
  parseDriverStops,
} from "./driver-stop";

const validStop = {
  order_id: "11111111-1111-1111-1111-111111111111",
  aggregate_version: 3,
  order_public_id: "ORD_abcdefghijklmnopqrstuv",
  stop_type: "PICKUP",
  status: "ASSIGNED",
  address_summary: "Centro, Culiacán",
} as const;

describe("parseDriverStops", () => {
  it("accepts the exact DTO and preserves REST order", () => {
    const second = {
      ...validStop,
      order_id: "22222222-2222-2222-2222-222222222222",
      order_public_id: "ORD_bcdefghijklmnopqrstuvw",
    };
    expect(parseDriverStops([validStop, second])).toEqual([validStop, second]);
  });

  it.each([
    ["non-array root", {}],
    ["null element", [null]],
    ["empty UUID", [{ ...validStop, order_id: "" }]],
    ["invalid UUID", [{ ...validStop, order_id: "not-a-uuid" }]],
    ["zero version", [{ ...validStop, aggregate_version: 0 }]],
    ["fractional version", [{ ...validStop, aggregate_version: 1.5 }]],
    [
      "unsafe version",
      [{ ...validStop, aggregate_version: Number.MAX_SAFE_INTEGER + 1 }],
    ],
    ["empty public id", [{ ...validStop, order_public_id: "" }]],
    ["unknown stop type", [{ ...validStop, stop_type: "OTHER" }]],
    ["unknown status", [{ ...validStop, status: "DELIVERED" }]],
    ["empty address", [{ ...validStop, address_summary: " " }]],
    ["extra property", [{ ...validStop, driver_id: "secret" }]],
    ["missing property", [{ ...validStop, address_summary: undefined }]],
    ["duplicate order", [validStop, validStop]],
  ])("rejects %s", (_name, payload) => {
    expect(() => parseDriverStops(payload)).toThrow(DriverStopsContractError);
  });

  it("rejects more than the defensive maximum", () => {
    const payload = Array.from(
      { length: MaximumStopsPerResponse + 1 },
      (_, index) => ({
        ...validStop,
        order_id: `11111111-1111-1111-1111-${String(index + 1).padStart(12, "0")}`,
      }),
    );
    expect(() => parseDriverStops(payload)).toThrow(DriverStopsContractError);
  });
});
