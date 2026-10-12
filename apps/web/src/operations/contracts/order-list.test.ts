import { afterEach, describe, expect, it } from "vitest";
import {
  defaultOrderListView,
  hasOrderListFilters,
  isCalendarDate,
  maximumOrderListRangeDays,
  orderListHref,
  orderListRange,
  orderListRangeOrderMessage,
  orderListRangeTooLongMessage,
  orderListSearch,
  orderListSummaryText,
  orderListViewKey,
  parseOrderListItem,
  parseOrderListPage,
  parseOrderListView,
} from "./order-list";

const orderId = "0a1b2c3d-1111-4222-8333-944445555666";
const otherId = "0a1b2c3d-1111-4222-8333-944445555667";
const guide = "ORD_AbCdEfGhIjKlMnOpQrSt_-";

/** An AI-05 Order exactly as listOrders returns it. */
function order(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: orderId,
    public_id: guide,
    owner_org_id: "11111111-1111-4111-8111-111111111111",
    operator_org_id: null,
    status: "DELIVERED",
    price_net: { currency: "MXN", amount_cents: 10_000 },
    version: 4,
    origin_location_id: "61000000-0000-4000-8000-000000000001",
    destination_location_id: "62000000-0000-4000-8000-000000000001",
    service_type: "SAME_DAY",
    quote_id: "63000000-0000-4000-8000-000000000001",
    city_id: "33333333-3333-4333-8333-333333333333",
    service_area_id: null,
    pricing_tier: "OCCASIONAL",
    total: { currency: "MXN", amount_cents: 11_600 },
    claim_window_ends_at: null,
    finalized_at: null,
    service_window: null,
    ...overrides,
  };
}

/** UI-PHASE3-ORDERS-LIST-2026-10-12 and ORD-LIST-DATE-FILTER-2026-10-12: the "Órdenes" contract on the web. */
describe("order list page parser", () => {
  it("reduces each strict AI-05 Order to guía, status and total cents", () => {
    const page = parseOrderListPage({ items: [order()], next_cursor: "bzI6NjM5_Xy-1" });
    expect(page).toEqual({
      items: [{ id: orderId, public_id: guide, status: "DELIVERED", total_cents: 11_600 }],
      next_cursor: "bzI6NjM5_Xy-1",
    });
    expect(parseOrderListPage({ items: [], next_cursor: null })).toEqual({ items: [], next_cursor: null });
  });

  it.each([
    ["an unknown field", order({ created_at: "2026-10-01T00:00:00Z" })],
    ["a personal field", order({ recipient_phone: "6670000000" })],
    ["an unknown status", order({ status: "LOST" })],
    ["a guía outside ORD_ and 22 characters", order({ public_id: "PQ-24K7Q" })],
    ["a guía with spaces", order({ public_id: ` ${guide}` })],
    ["a non-MXN total", order({ total: { currency: "USD", amount_cents: 1 } })],
    ["a fractional total", order({ total: { currency: "MXN", amount_cents: 1.5 } })],
    ["a negative total", order({ total: { currency: "MXN", amount_cents: -1 } })],
    ["a total beyond a safe integer", order({ total: { currency: "MXN", amount_cents: 2 ** 53 } })],
    ["a missing id", order({ id: undefined })],
  ])("fails closed on %s", (_name, item) => {
    expect(() => parseOrderListItem(JSON.parse(JSON.stringify(item)))).toThrow();
  });

  it("fails closed on the page shape, a repeated order and a cursor outside the contract", () => {
    expect(() => parseOrderListPage({ items: [order()] })).toThrow();
    expect(() => parseOrderListPage({ items: [order()], next_cursor: null, total: 1 })).toThrow();
    expect(() => parseOrderListPage({ items: [order(), order()], next_cursor: null })).toThrow();
    expect(() => parseOrderListPage({ items: [order(), order({ id: otherId })], next_cursor: null })).not.toThrow();
    for (const cursor of ["", "a&b", "a=b", "x".repeat(129), 7])
      expect(() => parseOrderListPage({ items: [], next_cursor: cursor }), String(cursor)).toThrow();
  });
});

describe("order list view in the URL", () => {
  it("keeps a known status and real calendar dates, in a canonical order", () => {
    const view = parseOrderListView(new URLSearchParams("to=2026-10-07&status=DELIVERED&from=2026-10-01&x=1"));
    expect(view).toEqual({ status: "DELIVERED", from: "2026-10-01", to: "2026-10-07" });
    expect(orderListViewKey(view)).toBe("status=DELIVERED&from=2026-10-01&to=2026-10-07");
    expect(orderListHref(view)).toBe("/ops/orders?status=DELIVERED&from=2026-10-01&to=2026-10-07");
    expect(orderListHref(defaultOrderListView)).toBe("/ops/orders");
    expect(hasOrderListFilters(view)).toBe(true);
    expect(hasOrderListFilters(defaultOrderListView)).toBe(false);
  });

  it("drops an unknown status and anything that is not a calendar date", () => {
    expect(parseOrderListView(new URLSearchParams("status=delivered&from=2026-02-30&to=2026-10-7"))).toEqual(
      defaultOrderListView,
    );
    for (const value of ["2026-13-01", "2026-00-10", "26-10-01", "2026-10-01T00:00", "1999-12-31", "", "hoy"])
      expect(isCalendarDate(value), value).toBe(false);
    expect(isCalendarDate("2028-02-29")).toBe(true);
    expect(isCalendarDate(null)).toBe(false);
  });
});

describe("creation period in Mazatlán days", () => {
  const originalZone = process.env.TZ;
  afterEach(() => {
    if (originalZone === undefined) delete process.env.TZ;
    else process.env.TZ = originalZone;
  });

  it("sends the first instant of Desde and the last microsecond of Hasta, both days included", () => {
    expect(orderListRange({ from: "2026-10-01", to: "2026-10-07" })).toEqual({
      ok: true,
      createdFrom: "2026-10-01T07:00:00.000Z",
      createdTo: "2026-10-08T06:59:59.999999Z",
    });
    expect(orderListRange({ from: "2026-10-12", to: "2026-10-12" })).toEqual({
      ok: true,
      createdFrom: "2026-10-12T07:00:00.000Z",
      createdTo: "2026-10-13T06:59:59.999999Z",
    });
    // A bound alone, and none.
    expect(orderListRange({ from: "2026-12-31", to: null })).toEqual({
      ok: true,
      createdFrom: "2026-12-31T07:00:00.000Z",
      createdTo: null,
    });
    expect(orderListRange({ from: null, to: "2026-12-31" })).toEqual({
      ok: true,
      createdFrom: null,
      createdTo: "2027-01-01T06:59:59.999999Z",
    });
    expect(orderListRange({ from: null, to: null })).toEqual({ ok: true, createdFrom: null, createdTo: null });
  });

  it.each(["America/New_York", "Asia/Tokyo", "UTC"])("ignores the device zone (%s)", (zone) => {
    process.env.TZ = zone;
    expect(orderListRange({ from: "2026-10-01", to: "2026-10-07" })).toEqual({
      ok: true,
      createdFrom: "2026-10-01T07:00:00.000Z",
      createdTo: "2026-10-08T06:59:59.999999Z",
    });
  });

  it("allows at most 31 days and refuses Desde after Hasta before anything is sent", () => {
    expect(maximumOrderListRangeDays).toBe(31);
    expect(orderListRange({ from: "2026-10-01", to: "2026-10-31" }).ok).toBe(true);
    expect(orderListRange({ from: "2026-10-01", to: "2026-11-01" })).toEqual({
      ok: false,
      message: orderListRangeTooLongMessage,
    });
    expect(orderListRange({ from: "2026-10-08", to: "2026-10-07" })).toEqual({
      ok: false,
      message: orderListRangeOrderMessage,
    });
    expect(orderListRangeTooLongMessage).toBe("Elige un periodo de 31 días o menos.");
  });
});

describe("listOrders query", () => {
  it("sends status, guía, the period and a cursor in a fixed order and nothing else", () => {
    const query = {
      status: "CLOSED",
      publicId: guide,
      createdFrom: "2026-10-01T07:00:00.000Z",
      createdTo: "2026-10-08T06:59:59.999999Z",
    } as const;
    expect(orderListSearch(query, "bzI6NjM5").toString()).toBe(
      `status=CLOSED&public_id=${guide}&created_from=2026-10-01T07%3A00%3A00.000Z` +
        "&created_to=2026-10-08T06%3A59%3A59.999999Z&cursor=bzI6NjM5",
    );
    expect(orderListSearch({ status: null, publicId: null, createdFrom: null, createdTo: null }).toString()).toBe("");
  });

  it("refuses a guía, status or cursor outside the contract", () => {
    const empty = { status: null, publicId: null, createdFrom: null, createdTo: null } as const;
    expect(() => orderListSearch({ ...empty, publicId: "nombre del destinatario" })).toThrow();
    expect(() => orderListSearch({ ...empty, status: "LOST" as never })).toThrow();
    expect(() => orderListSearch(empty, "a&b")).toThrow();
  });

  it("says how many orders are shown and whether more remain", () => {
    expect(orderListSummaryText(1, false)).toBe("Se muestra 1 orden.");
    expect(orderListSummaryText(50, true)).toBe("Se muestran 50 órdenes; hay más por cargar.");
  });
});
