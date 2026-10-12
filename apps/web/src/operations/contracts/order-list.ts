import { mazatlanWallTimeToInstant } from "../../lib/mazatlan-time";
import { parseCreatedOrder } from "./create-order";
import { orderStatuses, type OrderStatus } from "./operations-dashboard";
import { normalizePublicIdInput } from "./order-transitions";
import { array, boundedString, exactObject, fail, nullable, oneOf } from "./strict-json";

/**
 * UI-PHASE3-ORDERS-LIST-2026-10-12 ("Sí, lista de órdenes"): the "Órdenes" screen lists every order of the selected
 * organization with AI-05 listOrders, newest first, searched by guía (exact public_id) and filtered by one status and
 * a creation period in Mazatlán calendar days (ORD-LIST-DATE-FILTER-2026-10-12, created_from / created_to). A row
 * shows only the guía, the status and the total; the order id is a key and a link target, never shown.
 */
export const orderListPath = "/ops/orders";

/** One listed order, reduced to what the screen shows (the response is still the strict AI-05 Order). */
export interface OrderListItem {
  readonly id: string;
  readonly public_id: string;
  readonly status: OrderStatus;
  /** Order.total in integer MXN cents, IVA included. */
  readonly total_cents: number;
  /**
   * Order.created_at once the API sends it (owner, "En dos pasos (Recommended)"), a strict UTC instant; null while it
   * is absent, and then the screen shows no "Creada" column.
   */
  readonly created_at: string | null;
}

export interface OrderListPage {
  readonly items: readonly OrderListItem[];
  readonly next_cursor: string | null;
}

/** The server page size is 50 (OrdersOptions.PageSize); anything far larger is not this contract. */
const maximumPageItems = 200;
/**
 * The longest next_cursor listOrders issues (a bound o2 cursor is at most 128 characters, OrderCursorCodec); a longer
 * one is not this contract. AI-05 itself sets no maxLength, so earlier cursors stay readable.
 */
export const maximumOrderListCursorLength = 128;
const cursorPattern = /^[A-Za-z0-9_-]+$/;

export function isOrderListCursor(value: string): boolean {
  return value.length >= 1 && value.length <= maximumOrderListCursorLength && cursorPattern.test(value);
}

/** One AI-05 Order of the page: the strict Order reader, then the guía format and a known status. */
export function parseOrderListItem(value: unknown): OrderListItem {
  const order = parseCreatedOrder(value);
  if (normalizePublicIdInput(order.public_id) !== order.public_id) fail();
  return {
    id: order.id,
    public_id: order.public_id,
    status: oneOf(order.status, orderStatuses),
    total_cents: order.total.amount_cents,
    created_at: order.created_at,
  };
}

/** The "Creada" column is shown only when the API sends Order.created_at (UI-PHASE3-ORDERS-LIST-2026-10-12). */
export function orderListShowsCreatedAt(rows: readonly OrderListItem[]): boolean {
  return rows.some((row) => row.created_at !== null);
}

/** The listOrders page; the same order twice in one page breaks the contract. */
export function parseOrderListPage(value: unknown): OrderListPage {
  const page = exactObject(value, ["items", "next_cursor"]);
  const items = array(page.items, maximumPageItems).map(parseOrderListItem);
  if (new Set(items.map((item) => item.id)).size !== items.length) fail();
  const nextCursor = nullable(page.next_cursor, (cursor) => {
    const text = boundedString(cursor, 1, maximumOrderListCursorLength);
    if (!isOrderListCursor(text)) fail();
    return text;
  });
  return { items, next_cursor: nextCursor };
}

/**
 * The filters of the screen that live in the URL, so the browser's back button returns to the same list: one status
 * (or all) and the first and last creation days, both inclusive Mazatlán calendar dates (`YYYY-MM-DD`). The guía
 * search is not kept in the URL.
 */
export interface OrderListView {
  readonly status: OrderStatus | null;
  readonly from: string | null;
  readonly to: string | null;
}

export const defaultOrderListView: OrderListView = { status: null, from: null, to: null };

const calendarDatePattern = /^(\d{4})-(\d{2})-(\d{2})$/;

/** A real calendar date written `YYYY-MM-DD`. */
export function isCalendarDate(value: string | null | undefined): value is string {
  if (typeof value !== "string") return false;
  const match = calendarDatePattern.exec(value);
  if (match === null) return false;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const date = new Date(Date.UTC(year, month - 1, day));
  return year >= 2000 && date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day;
}

/** Reads the view from the URL; an unknown status or a value that is not a calendar date is dropped, never sent. */
export function parseOrderListView(search: { get(name: string): string | null }): OrderListView {
  const status = search.get("status");
  const from = search.get("from");
  const to = search.get("to");
  return {
    status: orderStatuses.find((candidate) => candidate === status) ?? null,
    from: isCalendarDate(from) ? from : null,
    to: isCalendarDate(to) ? to : null,
  };
}

/** Canonical query of a view, keys in a fixed order. */
export function orderListViewSearch(view: OrderListView): URLSearchParams {
  const search = new URLSearchParams();
  if (view.status !== null) search.set("status", view.status);
  if (view.from !== null) search.set("from", view.from);
  if (view.to !== null) search.set("to", view.to);
  return search;
}

/** One string per distinct view. */
export function orderListViewKey(view: OrderListView): string {
  return orderListViewSearch(view).toString();
}

export function orderListHref(view: OrderListView): string {
  const query = orderListViewSearch(view).toString();
  return query === "" ? orderListPath : `${orderListPath}?${query}`;
}

export function hasOrderListFilters(view: OrderListView): boolean {
  return view.status !== null || view.from !== null || view.to !== null;
}

/** The dashboard's (and listOrders') longest creation window, 31 days. */
export const maximumOrderListRangeDays = 31;
const dayMilliseconds = 86_400_000;

export const orderListRangeOrderMessage = "La fecha «Desde» no puede ser posterior a la fecha «Hasta».";
export const orderListRangeTooLongMessage = `Elige un periodo de ${maximumOrderListRangeDays} días o menos.`;
/** A typed date that is not a whole calendar day from 2000 on, or that cannot be read in Mazatlán time. */
export const orderListDateInvalidMessage = "Escribe una fecha completa (día, mes y año, desde 2000).";
/** The earliest date the filters take; a typed year is applied only once it has four digits and reaches it. */
export const minimumOrderListDate = "2000-01-01";

/**
 * What one change of a date field does. A whole calendar date from 2000 on is applied, and so is a cleared field. A
 * date still being typed is never applied: the browser reports it empty with `badInput` while a part is missing, and a
 * year typed digit by digit reads 0002, 0020, 0202 before it has four digits; both stay in the field as a draft.
 */
export type DateFilterEntry =
  | { readonly apply: true; readonly value: string | null }
  | { readonly apply: false; readonly incomplete: boolean };

export function readDateFilterEntry(value: string, badInput: boolean): DateFilterEntry {
  if (value === "") return badInput ? { apply: false, incomplete: true } : { apply: true, value: null };
  return isCalendarDate(value) ? { apply: true, value } : { apply: false, incomplete: false };
}

/** The first instant of a Mazatlán calendar day. */
function mazatlanDayStart(date: string): Date | null {
  return mazatlanWallTimeToInstant(`${date}T00:00`);
}

function nextCalendarDate(date: string): string {
  const [year, month, day] = date.split("-").map(Number) as [number, number, number];
  return new Date(Date.UTC(year, month - 1, day + 1)).toISOString().slice(0, 10);
}

/** The last microsecond before `instant`, written as listOrders reads it (`…:59.999999Z`). */
function lastMicrosecondBefore(instant: Date): string {
  return `${new Date(instant.getTime() - 1_000).toISOString().slice(0, 19)}.999999Z`;
}

/**
 * ORD-LIST-DATE-FILTER-2026-10-12: the listOrders created_from / created_to of the view. "Desde" is the first instant
 * of its Mazatlán day and "Hasta" the last microsecond of its Mazatlán day, so both chosen days are included, as the
 * server compares both bounds inclusively. A "Desde" after "Hasta" or a period longer than 31 days is refused here,
 * with the screen's message, before anything is sent.
 */
export type OrderListRange =
  | { readonly ok: true; readonly createdFrom: string | null; readonly createdTo: string | null }
  | { readonly ok: false; readonly message: string };

export function orderListRange(view: Pick<OrderListView, "from" | "to">): OrderListRange {
  if ((view.from !== null && !isCalendarDate(view.from)) || (view.to !== null && !isCalendarDate(view.to)))
    return { ok: false, message: orderListDateInvalidMessage };
  const start = view.from === null ? null : mazatlanDayStart(view.from);
  const end = view.to === null ? null : mazatlanDayStart(nextCalendarDate(view.to));
  if ((view.from !== null && start === null) || (view.to !== null && end === null))
    return { ok: false, message: orderListDateInvalidMessage };
  if (start !== null && end !== null) {
    if (end.getTime() <= start.getTime()) return { ok: false, message: orderListRangeOrderMessage };
    if (end.getTime() - start.getTime() > maximumOrderListRangeDays * dayMilliseconds)
      return { ok: false, message: orderListRangeTooLongMessage };
  }
  return {
    ok: true,
    createdFrom: start === null ? null : start.toISOString(),
    createdTo: end === null ? null : lastMicrosecondBefore(end),
  };
}

/** What one listOrders request filters by; a cursor is only ever sent with the query that issued it. */
export interface OrderListQuery {
  readonly status: OrderStatus | null;
  readonly publicId: string | null;
  readonly createdFrom: string | null;
  readonly createdTo: string | null;
}

/** The listOrders query string, keys in a fixed order, with the cursor last. */
export function orderListSearch(query: OrderListQuery, cursor: string | null = null): URLSearchParams {
  const search = new URLSearchParams();
  if (query.status !== null) search.set("status", oneOf(query.status, orderStatuses));
  if (query.publicId !== null) {
    if (normalizePublicIdInput(query.publicId) !== query.publicId) fail();
    search.set("public_id", query.publicId);
  }
  if (query.createdFrom !== null) search.set("created_from", query.createdFrom);
  if (query.createdTo !== null) search.set("created_to", query.createdTo);
  if (cursor !== null) {
    if (!isOrderListCursor(cursor)) fail();
    search.set("cursor", cursor);
  }
  return search;
}

/** "Se muestra 1 orden", "Se muestran 50 órdenes; hay más por cargar." */
export function orderListSummaryText(shown: number, hasMore: boolean): string {
  const base = shown === 1 ? "Se muestra 1 orden" : `Se muestran ${shown} órdenes`;
  return `${base}${hasMore ? "; hay más por cargar." : "."}`;
}
