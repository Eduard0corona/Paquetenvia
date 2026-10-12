import { describe, expect, it } from "vitest";
import { isStrictUtcInstant, optionalOrderCreatedAt, orderCreatedAtField } from "./utc-instant";

const fail = (): never => {
  throw new Error("contract");
};

/** UI-PHASE3-ORDERS-LIST-2026-10-12 (owner, "En dos pasos (Recommended)"): the one rule for Order.created_at. */
describe("strict UTC instant", () => {
  it.each([
    "2026-10-12T17:04:05Z",
    "2026-10-12T17:04:05.1Z",
    "2026-10-12T17:04:05.1234567Z",
    "2026-10-12T17:04:05+00:00",
    "2026-10-12T17:04:05.123+00:00",
    "2028-02-29T23:59:59Z",
  ])("accepts %s", (value) => {
    expect(isStrictUtcInstant(value)).toBe(true);
  });

  it.each([
    "2026-10-12T17:04:05",
    "2026-10-12T17:04:05.Z",
    "2026-10-12T17:04:05.12345678Z",
    "2026-10-12T10:04:05-07:00",
    "2026-10-12T17:04:05+01:00",
    "2026-10-12T17:04:05z",
    "2026-10-12 17:04:05Z",
    "2026-10-12",
    " 2026-10-12T17:04:05Z",
    "2026-10-12T17:04:05Z ",
    "2026-02-29T00:00:00Z",
    "2026-13-01T00:00:00Z",
    "2026-10-12T24:00:00Z",
    "2026-10-12T17:60:00Z",
    "2026-10-12T17:04:60Z",
    "Mon, 12 Oct 2026 17:04:05 GMT",
    "",
  ])("refuses %s", (value) => {
    expect(isStrictUtcInstant(value)).toBe(false);
  });

  it("refuses anything that is not a string", () => {
    for (const value of [null, undefined, 1_791_000_000, new Date(0), {}]) expect(isStrictUtcInstant(value)).toBe(false);
  });
});

describe("optional Order.created_at", () => {
  it("reads an absent field as null and keeps a strict instant", () => {
    expect(orderCreatedAtField).toBe("created_at");
    expect(optionalOrderCreatedAt({ id: "x" }, fail)).toBeNull();
    expect(optionalOrderCreatedAt({ created_at: "2026-10-12T17:04:05Z" }, fail)).toBe("2026-10-12T17:04:05Z");
  });

  it("fails closed on a present field that is not a strict UTC instant, null included", () => {
    for (const value of [null, undefined, "2026-10-12", "2026-10-12T17:04:05", 0])
      expect(() => optionalOrderCreatedAt({ created_at: value }, fail), String(value)).toThrow("contract");
  });
});
