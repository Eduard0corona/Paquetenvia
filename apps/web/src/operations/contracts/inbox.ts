import {
  orderStatuses,
  type OperationsDashboardFilters,
  type OperationsDashboardOrder,
  type OperationsServiceType,
  type OrderStatus,
} from "./operations-dashboard";
import type { OperationsQueueCounts, OperationsQueueId } from "./queue-counts";
import { statusGroup } from "./status-groups";
import { operationsOrderHref } from "../routing/operations-routing";

/**
 * UI-PHASE3-INBOX-2026-10-10 ("avanza con la fase 3"): the dispatcher's work inbox.
 *
 * Each queue is a set of existing operations dashboard queries (GET /operations/dashboard,
 * OBS-001) built only from that endpoint's own filters, and its tab shows the real server count
 * of getOperationsQueueCounts. No queue is ever computed by filtering rows already loaded.
 */
export const inboxPath = "/ops/inbox";

/** Every queue is listed as a table; the tabs follow the order of the approved design. */
export const inboxListQueueIds = [
  "unassigned",
  "needs_attention",
  "price_review",
  "delivered_not_closed",
  "en_route",
] as const;

export type InboxQueueId = (typeof inboxListQueueIds)[number];

/** A dashboard flag that lists a queue in one query instead of one query per status. */
export type InboxQueueFlag = "unassigned" | "priceReview";

export interface InboxQueue {
  readonly id: InboxQueueId;
  readonly label: string;
  /** What the queue holds, in plain es-MX (table caption). */
  readonly description: string;
  readonly empty: string;
  /** Footer words after "Mostrando N de M", for one order and for several (for example "sin asignar"). */
  readonly shownLabel: { readonly one: string; readonly other: string };
  /** The AI-04 statuses the queue can list. */
  readonly statuses: readonly OrderStatus[];
  /**
   * The dashboard flag that lists the queue (`unassigned=true`, or `price_review=true` from
   * UI-PHASE3-INBOX-PRICE-REVIEW-BULK-2026-10-11); null for a status queue, read as one dashboard
   * query per status and merged.
   */
  readonly flag: InboxQueueFlag | null;
  /** Statuses a person may narrow the queue to with a chip; empty when the queue offers none. */
  readonly statusChoices: readonly OrderStatus[];
}

/**
 * UI-PHASE3-INBOX-PRICE-REVIEW-BULK-2026-10-11 ("Solo órdenes activas"): an order has finished when
 * its status is in the UI-STATUS-GROUPS-2026-10-05 group "Terminadas"; the price review list and
 * its count keep only the others, so a finished order leaves them by itself.
 */
export const activeOrderStatuses: readonly OrderStatus[] = orderStatuses.filter(
  (status) => statusGroup(status) !== "FINISHED",
);

export const inboxQueues: Readonly<Record<InboxQueueId, InboxQueue>> = {
  unassigned: {
    id: "unassigned",
    label: "Sin asignar",
    description: "Órdenes listas para recolección o reprogramadas que todavía no tienen repartidor.",
    empty: "No hay órdenes sin asignar.",
    shownLabel: { one: "sin asignar", other: "sin asignar" },
    // The dashboard `unassigned` rule: READY_FOR_PICKUP or RESCHEDULED without an ACCEPTED
    // or ACTIVE assignment (the same rule as queues.unassigned).
    statuses: ["READY_FOR_PICKUP", "RESCHEDULED"],
    flag: "unassigned",
    statusChoices: ["READY_FOR_PICKUP", "RESCHEDULED"],
  },
  needs_attention: {
    id: "needs_attention",
    label: "Requiere atención",
    description: "Intentos fallidos, entregas reprogramadas, devoluciones y reclamaciones abiertas.",
    empty: "No hay órdenes que requieran atención.",
    shownLabel: { one: "que requiere atención", other: "que requieren atención" },
    statuses: ["FAILED_ATTEMPT", "RESCHEDULED", "RETURNING", "CLAIM_OPEN"],
    flag: null,
    statusChoices: ["FAILED_ATTEMPT", "RESCHEDULED", "RETURNING", "CLAIM_OPEN"],
  },
  price_review: {
    id: "price_review",
    label: "Precio por revisar",
    description:
      "Órdenes que no han terminado con un total menor al mínimo de referencia o con envío de bajo monto " +
      "autorizado. Al terminar, salen solas de esta lista.",
    empty: "No hay órdenes activas con precio por revisar.",
    shownLabel: { one: "con precio por revisar", other: "con precio por revisar" },
    // The dashboard `price_review` rule: a cost warning on an order that has not finished (the
    // same rule as queues.price_review). Twelve statuses would be noise as chips; service type and
    // zone still narrow it.
    statuses: activeOrderStatuses,
    flag: "priceReview",
    statusChoices: [],
  },
  delivered_not_closed: {
    id: "delivered_not_closed",
    label: "Entregadas sin cerrar",
    description: "Órdenes entregadas que todavía no se cierran.",
    empty: "No hay órdenes entregadas pendientes de cerrar.",
    shownLabel: { one: "entregada sin cerrar", other: "entregadas sin cerrar" },
    statuses: ["DELIVERED"],
    flag: null,
    statusChoices: [],
  },
  en_route: {
    id: "en_route",
    label: "En ruta",
    description: "Órdenes en tránsito o en reparto.",
    empty: "No hay órdenes en ruta.",
    shownLabel: { one: "en ruta", other: "en ruta" },
    statuses: ["IN_TRANSIT", "DELIVERING"],
    flag: null,
    statusChoices: ["IN_TRANSIT", "DELIVERING"],
  },
};

/** The queue tabs in the order of the approved design. */
export const inboxQueueTabs: readonly InboxQueueId[] = inboxListQueueIds;

/** The server count shown on a queue tab (getOperationsQueueCounts `queues`). */
export function inboxQueueCount(
  counts: OperationsQueueCounts | null,
  queue: OperationsQueueId,
): number | null {
  return counts === null ? null : counts.queues[queue];
}

/**
 * Whether a row the dashboard returned for `price_review=true` follows that rule (a cost warning
 * on an order that has not finished). A row outside it means the server ignored the filter, so the
 * inbox fails closed instead of listing it.
 */
export function isPriceReviewRow(row: OperationsDashboardOrder): boolean {
  return row.cost_warning !== null && statusGroup(row.status) !== "FINISHED";
}

const serviceTypeIds: readonly OperationsServiceType[] = ["SAME_DAY", "URGENT", "SCHEDULED_ROUTE"];

/** The view a person is looking at; it lives in the URL so the detail can return to it. */
export interface InboxView {
  readonly queue: InboxQueueId;
  /** One status of a multi-status queue, or null for all of them. */
  readonly status: OrderStatus | null;
  readonly serviceType: OperationsServiceType | null;
  /** Delivery zone (dashboard `delivery_zone_id`). */
  readonly zoneId: string | null;
}

export const defaultInboxView: InboxView = {
  queue: "unassigned",
  status: null,
  serviceType: null,
  zoneId: null,
};

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

function isZoneId(value: string | null): value is string {
  return value !== null && uuidPattern.test(value) && value !== "00000000-0000-0000-0000-000000000000";
}

function isListQueue(value: string | null): value is InboxQueueId {
  return value !== null && (inboxListQueueIds as readonly string[]).includes(value);
}

/** Statuses a person may pick inside a queue; empty when the queue offers no status chip. */
export function inboxStatusChoices(queue: InboxQueueId): readonly OrderStatus[] {
  return inboxQueues[queue].statusChoices;
}

/**
 * Reads the view from the URL. Anything unknown or malformed is dropped rather than sent: an
 * unknown queue is "Sin asignar", a status outside the queue's chips, an unknown service type or
 * a zone that is not a canonical id is no filter.
 */
export function parseInboxView(search: { get(name: string): string | null }): InboxView {
  const queueValue = search.get("queue");
  const queue = isListQueue(queueValue) ? queueValue : defaultInboxView.queue;
  const statusValue = search.get("status");
  const status = inboxStatusChoices(queue).find((candidate) => candidate === statusValue) ?? null;
  const serviceValue = search.get("service_type");
  const serviceType = serviceTypeIds.find((candidate) => candidate === serviceValue) ?? null;
  const zoneValue = search.get("zone");
  return { queue, status, serviceType, zoneId: isZoneId(zoneValue) ? zoneValue : null };
}

/** Canonical query of a view, keys in a fixed order. */
export function inboxSearch(view: InboxView): URLSearchParams {
  const search = new URLSearchParams();
  search.set("queue", view.queue);
  if (view.status !== null) search.set("status", view.status);
  if (view.serviceType !== null) search.set("service_type", view.serviceType);
  if (view.zoneId !== null) search.set("zone", view.zoneId);
  return search;
}

/** One string per distinct view; two equal keys load exactly the same queries. */
export function inboxViewKey(view: InboxView): string {
  return inboxSearch(view).toString();
}

export function inboxHref(view: InboxView): string {
  return `${inboxPath}?${inboxSearch(view).toString()}`;
}

export function hasInboxFilters(view: InboxView): boolean {
  return view.status !== null || view.serviceType !== null || view.zoneId !== null;
}

/**
 * The dashboard queries of a view: the queue's own server filter plus the chips, never a
 * cursor (pagination adds it per query).
 */
export function inboxQueries(view: InboxView): readonly OperationsDashboardFilters[] {
  const queue = inboxQueues[view.queue];
  const shared: OperationsDashboardFilters = {
    ...(view.serviceType === null ? {} : { serviceType: view.serviceType }),
    ...(view.zoneId === null ? {} : { deliveryZoneId: view.zoneId }),
  };
  const status = view.status === null ? {} : { status: view.status };
  if (queue.flag === "unassigned") return [{ ...shared, unassigned: true, ...status }];
  if (queue.flag === "priceReview") return [{ ...shared, priceReview: true, ...status }];
  const statuses = view.status === null ? queue.statuses : [view.status];
  return statuses.map((value) => ({ ...shared, status: value }));
}

/**
 * The line under the table, as in the approved design ("Mostrando 4 de 6 sin asignar ·
 * seleccionadas: 0"). "de M" is the queue's server count, so it is said only without chips (the
 * counts never apply them) and while it is not below the rows already shown; the selection part
 * appears only when the table offers selection.
 */
export function inboxFooterText(input: {
  readonly queue: InboxQueueId;
  readonly shown: number;
  readonly count: number | null;
  readonly filtered: boolean;
  readonly hasMore: boolean;
  readonly selected: number | null;
}): string {
  const words = inboxQueues[input.queue].shownLabel;
  const ofCount = !input.filtered && input.count !== null && input.count >= input.shown;
  const last = ofCount ? (input.count ?? input.shown) : input.shown;
  const label = last === 1 ? words.one : words.other;
  const shown = ofCount
    ? `Mostrando ${input.shown} de ${input.count} ${label}`
    : `Mostrando ${input.shown} ${label}${input.filtered ? " con los filtros elegidos" : ""}` +
      (input.hasMore ? "; hay más por cargar" : "");
  return input.selected === null ? shown : `${shown} · seleccionadas: ${input.selected}`;
}

/** Query parameter of the order detail that remembers the inbox view to return to. */
export const inboxReturnParameter = "inbox";

/** The order detail opened from the inbox; "Volver a la bandeja" restores `view`. */
export function inboxOrderHref(orderId: string, view: InboxView): string {
  const search = new URLSearchParams();
  search.set(inboxReturnParameter, inboxSearch(view).toString());
  return `${operationsOrderHref(orderId)}?${search.toString()}`;
}

/**
 * Where "Volver a la bandeja" goes, from the detail's `inbox` query value. Only a canonical
 * inbox URL is ever built (the path is fixed and the view is parsed again), so the value can
 * never send the person anywhere else; null when the detail was not opened from the inbox.
 */
export function inboxReturnHref(value: string | readonly string[] | undefined | null): string | null {
  if (typeof value !== "string" || value.length > 512) return null;
  return inboxHref(parseInboxView(new URLSearchParams(value)));
}
