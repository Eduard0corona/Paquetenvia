import type {
  OperationsDashboardFilters,
  OperationsServiceType,
  OrderStatus,
} from "./operations-dashboard";
import type { OperationsQueueCounts, OperationsQueueId } from "./queue-counts";
import { operationsOrderHref } from "../routing/operations-routing";

/**
 * UI-PHASE3-INBOX-2026-10-10 ("avanza con la fase 3"): the dispatcher's work inbox.
 *
 * Each queue is a set of existing operations dashboard queries (GET /operations/dashboard,
 * OBS-001) built only from that endpoint's own filters, and its tab shows the real server count
 * of getOperationsQueueCounts. No queue is ever computed by filtering rows already loaded.
 */
export const inboxPath = "/ops/inbox";

/** The queues listed as a table, in the order of the approved design. */
export const inboxListQueueIds = [
  "unassigned",
  "needs_attention",
  "delivered_not_closed",
  "en_route",
] as const;

export type InboxQueueId = (typeof inboxListQueueIds)[number];

export interface InboxQueue {
  readonly id: InboxQueueId;
  readonly label: string;
  /** What the queue holds, in plain es-MX (table caption). */
  readonly description: string;
  readonly empty: string;
  /**
   * The AI-04 statuses the queue lists. A multi-status queue is one dashboard query per
   * status, merged; a person may narrow it to one of them with a status chip.
   */
  readonly statuses: readonly OrderStatus[];
  /** Listed through the dashboard `unassigned=true` filter instead of one query per status. */
  readonly unassignedFilter: boolean;
}

export const inboxQueues: Readonly<Record<InboxQueueId, InboxQueue>> = {
  unassigned: {
    id: "unassigned",
    label: "Sin asignar",
    description: "Órdenes listas para recolección o reprogramadas que todavía no tienen repartidor.",
    empty: "No hay órdenes sin asignar.",
    // The dashboard `unassigned` rule: READY_FOR_PICKUP or RESCHEDULED without an ACCEPTED
    // or ACTIVE assignment (the same rule as queues.unassigned).
    statuses: ["READY_FOR_PICKUP", "RESCHEDULED"],
    unassignedFilter: true,
  },
  needs_attention: {
    id: "needs_attention",
    label: "Requiere atención",
    description: "Intentos fallidos, entregas reprogramadas, devoluciones y reclamaciones abiertas.",
    empty: "No hay órdenes que requieran atención.",
    statuses: ["FAILED_ATTEMPT", "RESCHEDULED", "RETURNING", "CLAIM_OPEN"],
    unassignedFilter: false,
  },
  delivered_not_closed: {
    id: "delivered_not_closed",
    label: "Entregadas sin cerrar",
    description: "Órdenes entregadas que todavía no se cierran.",
    empty: "No hay órdenes entregadas pendientes de cerrar.",
    statuses: ["DELIVERED"],
    unassignedFilter: false,
  },
  en_route: {
    id: "en_route",
    label: "En ruta",
    description: "Órdenes en tránsito o en reparto.",
    empty: "No hay órdenes en ruta.",
    statuses: ["IN_TRANSIT", "DELIVERING"],
    unassignedFilter: false,
  },
};

/**
 * "Precio por revisar" (queues.price_review) has no server filter and its count includes
 * finished orders, so it stays a count with a note: the inbox never fakes its list.
 */
export const priceReviewQueue = {
  id: "price_review",
  label: "Precio por revisar",
  note: "Solo conteo por ahora: su lista llegará después y el total incluye órdenes ya terminadas.",
} as const;

/** The queue tabs in the order of the approved design; price review is the count-only tile. */
export const inboxQueueTabs: readonly (InboxQueueId | typeof priceReviewQueue.id)[] = [
  "unassigned",
  "needs_attention",
  priceReviewQueue.id,
  "delivered_not_closed",
  "en_route",
];

/** The server count shown on a queue tab (getOperationsQueueCounts `queues`). */
export function inboxQueueCount(
  counts: OperationsQueueCounts | null,
  queue: OperationsQueueId,
): number | null {
  return counts === null ? null : counts.queues[queue];
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

/** Statuses a person may pick inside a queue; empty for a single-status queue. */
export function inboxStatusChoices(queue: InboxQueueId): readonly OrderStatus[] {
  const statuses = inboxQueues[queue].statuses;
  return statuses.length > 1 ? statuses : [];
}

/**
 * Reads the view from the URL. Anything unknown or malformed is dropped rather than sent: an
 * unknown queue is "Sin asignar", a status outside the queue, an unknown service type or a
 * zone that is not a canonical id is no filter.
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
  if (queue.unassignedFilter) {
    return [{ ...shared, unassigned: true, ...(view.status === null ? {} : { status: view.status }) }];
  }
  const statuses = view.status === null ? queue.statuses : [view.status];
  return statuses.map((status) => ({ ...shared, status }));
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
