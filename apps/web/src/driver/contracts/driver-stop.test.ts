import { describe, expect, it } from "vitest";
import {
  DriverStopsContractError,
  MaximumAddressTextLength,
  MaximumContactFullNameLength,
  MaximumContactShortNameLength,
  MaximumReferencesLength,
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
  contact_short_name: null,
  contact_full_name: "Farmacia Sintética",
  zone_name: null,
  service_window: null,
  cod: null,
  navigation: {
    latitude: 24.8,
    longitude: -107.39,
    address_text: "Calle Sintética 100",
    references: null,
  },
} as const;

/** UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: a delivery stop with every detail present. */
const deliveryStop = {
  ...validStop,
  order_id: "22222222-2222-2222-2222-222222222222",
  order_public_id: "ORD_bcdefghijklmnopqrstuvw",
  stop_type: "DELIVERY",
  status: "DELIVERING",
  contact_short_name: "M. López",
  contact_full_name: null,
  zone_name: "Tres Ríos",
  service_window: {
    from: "2026-10-11T20:00:00+00:00",
    to: "2026-10-11T22:00:00Z",
  },
  cod: { expected: { currency: "MXN", amount_cents: 125_000 }, recorded: false },
  navigation: {
    latitude: -24.81,
    longitude: 107.4,
    address_text: "Calle Sintética 200\nInterior B",
    references: "Portón azul",
  },
} as const;

describe("parseDriverStops", () => {
  it("accepts the exact DTO and preserves REST order", () => {
    const second = {
      ...validStop,
      order_id: "33333333-3333-3333-3333-333333333333",
      order_public_id: "ORD_cdefghijklmnopqrstuvwx",
    };
    expect(parseDriverStops([validStop, second])).toEqual([validStop, second]);
  });

  it("keeps a pickup's full contact name and a return's short one", () => {
    const returning = {
      ...deliveryStop,
      order_id: "44444444-4444-4444-4444-444444444444",
      order_public_id: "ORD_defghijklmnopqrstuvwxy",
      stop_type: "RETURN",
      status: "RETURNING",
      cod: null,
    };
    const [pickup, back] = parseDriverStops([validStop, returning]);
    expect(pickup.contact_full_name).toBe("Farmacia Sintética");
    expect(pickup.contact_short_name).toBeNull();
    expect(back.contact_short_name).toBe("M. López");
    expect(back.contact_full_name).toBeNull();
  });

  it("keeps the stop details and normalizes the window to UTC Z", () => {
    const [stop] = parseDriverStops([deliveryStop]);
    expect(stop).toEqual({
      ...deliveryStop,
      service_window: {
        from: "2026-10-11T20:00:00.000Z",
        to: "2026-10-11T22:00:00.000Z",
      },
    });
    expect(Object.isFrozen(stop.navigation)).toBe(true);
    expect(Object.isFrozen(stop.cod)).toBe(true);
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
    ["phone property", [{ ...validStop, contact_phone: "6670000000" }]],
    ["missing property", [{ ...validStop, address_summary: undefined }]],
    ["missing detail member", [omit(validStop, "navigation")]],
    [
      "legacy six-key stop",
      [omit(omit(omit(omit(omit(omit(validStop, "navigation"), "cod"), "service_window"), "zone_name"), "contact_short_name"), "contact_full_name")],
    ],
    ["missing full name member", [omit(validStop, "contact_full_name")]],
    ["duplicate order", [validStop, validStop]],
    ["blank short name", [{ ...deliveryStop, contact_short_name: " " }]],
    [
      "long short name",
      [{ ...deliveryStop, contact_short_name: "M".repeat(MaximumContactShortNameLength + 1) }],
    ],
    // Owner answer "Completo en recolección": the full name only at pickup.
    ["full name on a delivery stop", [{ ...deliveryStop, contact_full_name: "María López Pérez" }]],
    ["full name on a return stop", [{ ...deliveryStop, stop_type: "RETURN", status: "RETURNING", cod: null, contact_short_name: null, contact_full_name: "María López Pérez" }]],
    ["short name on a pickup stop", [{ ...validStop, contact_short_name: "F. Sintética" }]],
    ["blank full name", [{ ...validStop, contact_full_name: "  " }]],
    [
      "long full name",
      [{ ...validStop, contact_full_name: "F".repeat(MaximumContactFullNameLength + 1) }],
    ],
    ["non-text zone", [{ ...validStop, zone_name: 7 }]],
    ["window extra member", [{ ...deliveryStop, service_window: { ...deliveryStop.service_window, zone: "x" } }]],
    ["window with local offset", [{ ...deliveryStop, service_window: { from: "2026-10-11T13:00:00-07:00", to: "2026-10-11T22:00:00Z" } }]],
    ["window not ordered", [{ ...deliveryStop, service_window: { from: "2026-10-11T22:00:00Z", to: "2026-10-11T20:00:00Z" } }]],
    ["cod on a pickup stop", [{ ...validStop, cod: deliveryStop.cod }]],
    ["cod in another currency", [{ ...deliveryStop, cod: { expected: { currency: "USD", amount_cents: 1 }, recorded: false } }]],
    ["fractional cod cents", [{ ...deliveryStop, cod: { expected: { currency: "MXN", amount_cents: 12.5 }, recorded: false } }]],
    ["cod amount as text", [{ ...deliveryStop, cod: { expected: { currency: "MXN", amount_cents: "125000" }, recorded: false } }]],
    ["zero cod", [{ ...deliveryStop, cod: { expected: { currency: "MXN", amount_cents: 0 }, recorded: false } }]],
    ["cod recorded as text", [{ ...deliveryStop, cod: { expected: { currency: "MXN", amount_cents: 1 }, recorded: "no" } }]],
    ["cod extra member", [{ ...deliveryStop, cod: { ...deliveryStop.cod, reference: "x" } }]],
    ["navigation extra member", [{ ...validStop, navigation: { ...validStop.navigation, phone: "x" } }]],
    ["latitude out of range", [{ ...validStop, navigation: { ...validStop.navigation, latitude: 90.5 } }]],
    ["longitude out of range", [{ ...validStop, navigation: { ...validStop.navigation, longitude: -180.5 } }]],
    ["non-finite latitude", [{ ...validStop, navigation: { ...validStop.navigation, latitude: Number.NaN } }]],
    ["coordinate as text", [{ ...validStop, navigation: { ...validStop.navigation, longitude: "-107.39" } }]],
    ["blank address text", [{ ...validStop, navigation: { ...validStop.navigation, address_text: "  " } }]],
    [
      "long address text",
      [{ ...validStop, navigation: { ...validStop.navigation, address_text: "a".repeat(MaximumAddressTextLength + 1) } }],
    ],
    [
      "long references",
      [{ ...validStop, navigation: { ...validStop.navigation, references: "a".repeat(MaximumReferencesLength + 1) } }],
    ],
    ["null navigation", [{ ...validStop, navigation: null }]],
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

function omit<T extends object, K extends keyof T>(value: T, key: K): Omit<T, K> {
  const copy = { ...value };
  delete copy[key];
  return copy;
}
