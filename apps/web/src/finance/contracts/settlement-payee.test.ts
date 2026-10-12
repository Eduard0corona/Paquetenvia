import { describe, expect, it } from "vitest";
import {
  parseSettlementPayee,
  parseSettlementPayeePage,
  settlementPayeeOption,
  settlementPayeeSearch,
  sortSettlementPayees,
} from "./settlement-payee";

/** UI-PHASE3-DRIVERS-2026-10-11: AI-05 SettlementPayee and SettlementPayeePage, read fail-closed. */
const payee = {
  driver_id: "d71e0000-0000-4000-8000-000000000002",
  driver_reference: "DRV-3d93b4c6",
  vehicle_type: "MOTORCYCLE",
};

describe("settlement payees", () => {
  it("reads exactly the published payee and page", () => {
    expect(parseSettlementPayee(payee)).toEqual(payee);
    expect(parseSettlementPayeePage({ items: [payee], next_cursor: "AwAAAAAAAAAAAAAAAAAAAAB" })).toEqual({
      items: [payee],
      next_cursor: "AwAAAAAAAAAAAAAAAAAAAAB",
    });
    expect(parseSettlementPayeePage({ items: [], next_cursor: null })).toEqual({ items: [], next_cursor: null });
  });

  it.each([
    ["an extra field", { ...payee, status: "ACTIVE" }],
    ["a missing field", { driver_id: payee.driver_id, driver_reference: payee.driver_reference }],
    ["a name", { ...payee, name: "Persona" }],
    ["a phone", { ...payee, phone: "6670000000" }],
    ["a reference that is not DRV-", { ...payee, driver_reference: "d71e0000" }],
    ["an upper-case reference", { ...payee, driver_reference: "DRV-3D93B4C6" }],
    ["an unknown vehicle", { ...payee, vehicle_type: "TRUCK" }],
    ["a non-canonical id", { ...payee, driver_id: "D71E0000-0000-4000-8000-000000000002" }],
  ])("refuses %s", (_, value) => {
    expect(() => parseSettlementPayee(value)).toThrow();
  });

  it("refuses a page with duplicates, too many items or a foreign cursor", () => {
    expect(() => parseSettlementPayeePage({ items: [payee, payee], next_cursor: null })).toThrow();
    expect(() =>
      parseSettlementPayeePage({
        items: Array.from({ length: 101 }, (_, index) => ({
          ...payee,
          driver_id: `d71e0000-0000-4000-8000-${String(index).padStart(12, "0")}`,
        })),
        next_cursor: null,
      }),
    ).toThrow();
    expect(() => parseSettlementPayeePage({ items: [], next_cursor: "a b" })).toThrow();
    expect(() => parseSettlementPayeePage({ items: [], next_cursor: "" })).toThrow();
    expect(() => parseSettlementPayeePage({ items: [] })).toThrow();
  });

  it("sends only a cursor", () => {
    expect(settlementPayeeSearch().toString()).toBe("");
    expect(settlementPayeeSearch(null).toString()).toBe("");
    expect(settlementPayeeSearch("AwAAAAAAAAAAAAAAAAAAAAB").toString()).toBe("cursor=AwAAAAAAAAAAAAAAAAAAAAB");
    expect(() => settlementPayeeSearch("a&b=c")).toThrow();
  });

  it("names a payee by reference and vehicle and lists them by reference", () => {
    expect(settlementPayeeOption(parseSettlementPayee(payee))).toBe("DRV-3d93b4c6 · Motocicleta");
    const sorted = sortSettlementPayees([
      { driver_id: "d71e0000-0000-4000-8000-000000000003", driver_reference: "DRV-ffffffff", vehicle_type: "CAR" },
      parseSettlementPayee(payee),
      { driver_id: "d71e0000-0000-4000-8000-000000000001", driver_reference: "DRV-00000000", vehicle_type: "VAN" },
    ]);
    expect(sorted.map((item) => item.driver_reference)).toEqual(["DRV-00000000", "DRV-3d93b4c6", "DRV-ffffffff"]);
  });
});
