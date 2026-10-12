import { afterEach, describe, expect, it } from "vitest";
import {
  defaultOrderListView,
  hasOrderListFilters,
  isCalendarDate,
  maximumOrderListRangeDays,
  minimumOrderListDate,
  orderListDateInvalidMessage,
  orderListHref,
  orderListRange,
  orderListRangeOrderMessage,
  orderListRangeTooLongMessage,
  orderListSearch,
  orderListShowsCreatedAt,
  orderListSummaryText,
  orderListViewKey,
  parseOrderListItem,
  parseOrderListPage,
  parseOrderListView,
  readDateFilterEntry,
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
      items: [{ id: orderId, public_id: guide, status: "DELIVERED", total_cents: 11_600, created_at: null }],
      next_cursor: "bzI6NjM5_Xy-1",
    });
    expect(parseOrderListPage({ items: [], next_cursor: null })).toEqual({ items: [], next_cursor: null });
  });

  // UI-PHASE3-ORDERS-LIST-2026-10-12 (owner, "En dos pasos (Recommended)"): the web accepts Order.created_at before
  // the API sends it; absent it is null and no "Creada" column is shown.
  it.each(["2026-10-12T17:04:05Z", "2026-10-12T17:04:05.1234567Z", "2026-10-12T17:04:05.123+00:00"])(
    "keeps a strict UTC created_at once the API sends it (%s)",
    (createdAt) => {
      const item = parseOrderListItem(order({ created_at: createdAt }));
      expect(item.created_at).toBe(createdAt);
      expect(orderListShowsCreatedAt([item])).toBe(true);
    },
  );

  it("shows the creation column only when some order carries created_at", () => {
    const without = parseOrderListItem(order());
    const withDate = parseOrderListItem(order({ id: otherId, created_at: "2026-10-12T17:04:05Z" }));
    expect(without.created_at).toBeNull();
    expect(orderListShowsCreatedAt([])).toBe(false);
    expect(orderListShowsCreatedAt([without])).toBe(false);
    expect(orderListShowsCreatedAt([without, withDate])).toBe(true);
  });

  it.each([
    ["an unknown field", order({ updated_at: "2026-10-01T00:00:00Z" })],
    ["a created_at that is null", order({ created_at: null })],
    ["a created_at without a zone", order({ created_at: "2026-10-01T00:00:00" })],
    ["a created_at in another zone", order({ created_at: "2026-10-01T00:00:00-07:00" })],
    ["a created_at that is only a date", order({ created_at: "2026-10-01" })],
    ["a created_at with eight fractional digits", order({ created_at: "2026-10-01T00:00:00.12345678Z" })],
    ["a created_at that is not a calendar day", order({ created_at: "2026-02-30T00:00:00Z" })],
    ["a created_at with an hour out of range", order({ created_at: "2026-10-01T24:00:00Z" })],
    ["a created_at with spaces", order({ created_at: " 2026-10-01T00:00:00Z" })],
    ["a created_at that is a number", order({ created_at: 1_791_000_000 })],
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

  it("refuses with its own message a date that is not a whole calendar day from 2000 on", () => {
    expect(minimumOrderListDate).toBe("2000-01-01");
    expect(orderListDateInvalidMessage).toBe("Escribe una fecha completa (día, mes y año, desde 2000).");
    // A year typed digit by digit reads 0002, 0020, 0202 before it has four digits.
    for (const from of ["0002-10-01", "0020-10-01", "0202-10-01", "1999-12-31", "2026-02-30", "2026-10-1"])
      expect(orderListRange({ from, to: null }), from).toEqual({ ok: false, message: orderListDateInvalidMessage });
    expect(orderListRange({ from: null, to: "0202-10-07" })).toEqual({ ok: false, message: orderListDateInvalidMessage });
    expect(orderListRange({ from: "2000-01-01", to: "2000-01-31" }).ok).toBe(true);
  });

  it("applies a typed date only once it is whole, and a cleared field at once", () => {
    // Year typed with the keyboard into a date field, one digit at a time (what Chromium reports at each step).
    for (const partialYear of ["0002-10-01", "0020-10-01", "0202-10-01"])
      expect(readDateFilterEntry(partialYear, false), partialYear).toEqual({ apply: false, incomplete: false });
    expect(readDateFilterEntry("2026-10-01", false)).toEqual({ apply: true, value: "2026-10-01" });
    // A part still missing: empty with badInput is a draft, not a cleared field.
    expect(readDateFilterEntry("", true)).toEqual({ apply: false, incomplete: true });
    expect(readDateFilterEntry("", false)).toEqual({ apply: true, value: null });
    expect(readDateFilterEntry("1999-12-31", false)).toEqual({ apply: false, incomplete: false });
    expect(readDateFilterEntry("2026-02-30", false)).toEqual({ apply: false, incomplete: false });
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
