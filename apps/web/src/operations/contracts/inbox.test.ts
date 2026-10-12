import { describe, expect, it } from "vitest";
import { buildOperationsDashboardSearch } from "../api/operations-api";
import {
  defaultInboxView,
  hasInboxFilters,
  inboxHref,
  inboxListQueueIds,
  inboxOrderHref,
  inboxQueries,
  inboxQueueCount,
  inboxQueues,
  inboxQueueTabs,
  inboxReturnHref,
  inboxSearch,
  inboxStatusChoices,
  inboxViewKey,
  activeOrderStatuses,
  inboxFooterText,
  isPriceReviewRow,
  parseInboxView,
  type InboxView,
} from "./inbox";
import { orderStatuses, type OperationsDashboardOrder } from "./operations-dashboard";
import { operationsQueueIds, type OperationsQueueCounts } from "./queue-counts";
import { statusesInGroup } from "./status-groups";

const zone = "5a2b3c4d-1111-4222-8333-944445555666";
const order = "0f1e2d3c-4b5a-4697-8877-665544332211";

const view = (overrides: Partial<InboxView>): InboxView => ({ ...defaultInboxView, ...overrides });
const parse = (query: string) => parseInboxView(new URLSearchParams(query));

describe("work inbox queues (UI-PHASE3-INBOX-2026-10-10)", () => {
  it("shows the five queues of the approved design in order, with real server counts", () => {
    expect(inboxQueueTabs.map((id) => inboxQueues[id].label)).toEqual([
      "Sin asignar",
      "Requiere atención",
      "Precio por revisar",
      "Entregadas sin cerrar",
      "En ruta",
    ]);
    // Every tab reads one getOperationsQueueCounts queue and every published queue has a tab.
    expect([...inboxQueueTabs].sort()).toEqual([...operationsQueueIds].sort());
    const counts = {
      queues: { unassigned: 3, needs_attention: 4, price_review: 5, delivered_not_closed: 6, en_route: 7 },
    } as unknown as OperationsQueueCounts;
    expect(inboxQueueTabs.map((id) => inboxQueueCount(counts, id))).toEqual([3, 4, 5, 6, 7]);
    expect(inboxQueueCount(null, "unassigned")).toBeNull();
  });

  it("lists the same statuses the server counts and the status groups use", () => {
    expect(inboxQueues.needs_attention.statuses).toEqual(statusesInGroup("NEEDS_ATTENTION"));
    expect(inboxQueues.en_route.statuses).toEqual(statusesInGroup("EN_ROUTE"));
    expect(inboxQueues.delivered_not_closed.statuses).toEqual(["DELIVERED"]);
    expect(inboxQueues.unassigned.statuses).toEqual(["READY_FOR_PICKUP", "RESCHEDULED"]);
  });

  it("lists price review from the server filter, only orders that have not finished (UI-PHASE3-INBOX-PRICE-REVIEW-BULK-2026-10-11)", () => {
    // Every queue is a server list now; none is a count-only tile or a list built from loaded rows.
    expect(inboxListQueueIds).toContain("price_review");
    expect(inboxQueueTabs).toEqual(inboxListQueueIds);
    expect(parse("queue=price_review").queue).toBe("price_review");
    expect(inboxQueries(view({ queue: "price_review" }))).toEqual([{ priceReview: true }]);
    expect(buildOperationsDashboardSearch(inboxQueries(view({ queue: "price_review" }))[0]).toString()).toBe(
      "price_review=true",
    );
    expect(inboxQueues.price_review.flag).toBe("priceReview");
    // Active means outside the "Terminadas" group: the same five statuses the server leaves out.
    expect(activeOrderStatuses).toEqual(orderStatuses.filter((status) => !statusesInGroup("FINISHED").includes(status)));
    expect(statusesInGroup("FINISHED")).toEqual(["RETURNED", "DELIVERED", "CLOSED", "CLAIM_RESOLVED", "CANCELLED"]);
    expect(inboxQueues.price_review.statuses).toEqual(activeOrderStatuses);
    expect(inboxQueues.price_review.description).toMatch(/salen solas/);
    expect(inboxQueues.price_review.empty).toBe("No hay órdenes activas con precio por revisar.");
  });

  it("accepts only price review rows that follow the rule, so an ignored filter fails closed", () => {
    const row = (status: OperationsDashboardOrder["status"], warning: OperationsDashboardOrder["cost_warning"]) =>
      ({ status, cost_warning: warning }) as OperationsDashboardOrder;
    for (const status of orderStatuses) {
      const finished = statusesInGroup("FINISHED").includes(status);
      expect(isPriceReviewRow(row(status, "BELOW_MINIMUM_SNAPSHOT")), status).toBe(!finished);
      expect(isPriceReviewRow(row(status, "AUTHORIZED_OVERRIDE")), status).toBe(!finished);
      expect(isPriceReviewRow(row(status, null)), status).toBe(false);
    }
  });

  it("says what the table shows as in the approved footer, with the selection", () => {
    const base = { queue: "unassigned" as const, shown: 4, count: 6, filtered: false, hasMore: true };
    expect(inboxFooterText({ ...base, selected: 0 })).toBe("Mostrando 4 de 6 sin asignar · seleccionadas: 0");
    expect(inboxFooterText({ ...base, selected: 3 })).toBe("Mostrando 4 de 6 sin asignar · seleccionadas: 3");
    // Without a selection column, the selection is not mentioned.
    expect(inboxFooterText({ ...base, queue: "en_route", selected: null })).toBe("Mostrando 4 de 6 en ruta");
    expect(inboxFooterText({ ...base, queue: "needs_attention", shown: 1, count: 1, selected: null })).toBe(
      "Mostrando 1 de 1 que requiere atención",
    );
    expect(inboxFooterText({ ...base, queue: "price_review", selected: 0 })).toBe(
      "Mostrando 4 de 6 con precio por revisar · seleccionadas: 0",
    );
    // The counts never apply the chips, and a count below the rows shown is stale: no "de M" then.
    expect(inboxFooterText({ ...base, filtered: true, selected: 0 })).toBe(
      "Mostrando 4 sin asignar con los filtros elegidos; hay más por cargar · seleccionadas: 0",
    );
    expect(inboxFooterText({ ...base, count: 3, hasMore: false, selected: null })).toBe("Mostrando 4 sin asignar");
    expect(inboxFooterText({ ...base, count: null, queue: "delivered_not_closed", selected: null })).toBe(
      "Mostrando 4 entregadas sin cerrar; hay más por cargar",
    );
  });

  it("builds each queue only from the existing dashboard filters", () => {
    expect(inboxQueries(view({ queue: "unassigned" }))).toEqual([{ unassigned: true }]);
    expect(inboxQueries(view({ queue: "delivered_not_closed" }))).toEqual([{ status: "DELIVERED" }]);
    expect(inboxQueries(view({ queue: "en_route" }))).toEqual([{ status: "IN_TRANSIT" }, { status: "DELIVERING" }]);
    expect(inboxQueries(view({ queue: "needs_attention" }))).toEqual([
      { status: "FAILED_ATTEMPT" },
      { status: "RESCHEDULED" },
      { status: "RETURNING" },
      { status: "CLAIM_OPEN" },
    ]);
  });

  it("narrows a queue with the chips as server filters", () => {
    expect(inboxQueries(view({ queue: "unassigned", status: "RESCHEDULED", serviceType: "URGENT", zoneId: zone }))).toEqual([
      { unassigned: true, status: "RESCHEDULED", serviceType: "URGENT", deliveryZoneId: zone },
    ]);
    expect(inboxQueries(view({ queue: "needs_attention", status: "CLAIM_OPEN" }))).toEqual([{ status: "CLAIM_OPEN" }]);
    expect(inboxQueries(view({ queue: "en_route", serviceType: "SAME_DAY" }))).toEqual([
      { status: "IN_TRANSIT", serviceType: "SAME_DAY" },
      { status: "DELIVERING", serviceType: "SAME_DAY" },
    ]);
    // Each query serializes to the dashboard query string the API already accepts.
    expect(
      inboxQueries(view({ queue: "unassigned", serviceType: "URGENT", zoneId: zone })).map((filters) =>
        buildOperationsDashboardSearch(filters).toString(),
      ),
    ).toEqual([`delivery_zone_id=${zone}&service_type=URGENT&unassigned=true`]);
  });

  it("offers status chips only inside multi-status queues and only their statuses", () => {
    expect(inboxStatusChoices("unassigned")).toEqual(["READY_FOR_PICKUP", "RESCHEDULED"]);
    expect(inboxStatusChoices("needs_attention")).toEqual(["FAILED_ATTEMPT", "RESCHEDULED", "RETURNING", "CLAIM_OPEN"]);
    expect(inboxStatusChoices("en_route")).toEqual(["IN_TRANSIT", "DELIVERING"]);
    expect(inboxStatusChoices("delivered_not_closed")).toEqual([]);
    // Price review offers no status chip (twelve statuses); service type and zone still narrow it.
    expect(inboxStatusChoices("price_review")).toEqual([]);
    expect(parse("queue=price_review&status=DRAFT").status).toBeNull();
    expect(inboxQueries(view({ queue: "price_review", serviceType: "URGENT", zoneId: zone }))).toEqual([
      { serviceType: "URGENT", deliveryZoneId: zone, priceReview: true },
    ]);
  });
});

describe("work inbox view in the URL (UI-PHASE3-INBOX-2026-10-10)", () => {
  it("opens Sin asignar without filters by default", () => {
    expect(parse("")).toEqual(defaultInboxView);
    expect(defaultInboxView.queue).toBe("unassigned");
    expect(hasInboxFilters(defaultInboxView)).toBe(false);
  });

  it("round-trips a view through its canonical URL", () => {
    const selected = view({ queue: "needs_attention", status: "FAILED_ATTEMPT", serviceType: "URGENT", zoneId: zone });
    expect(inboxHref(selected)).toBe(
      `/ops/inbox?queue=needs_attention&status=FAILED_ATTEMPT&service_type=URGENT&zone=${zone}`,
    );
    expect(parseInboxView(inboxSearch(selected))).toEqual(selected);
    expect(hasInboxFilters(selected)).toBe(true);
    expect(inboxViewKey(selected)).toBe(inboxViewKey(parse(inboxSearch(selected).toString())));
    expect(inboxViewKey(selected)).not.toBe(inboxViewKey(view({ queue: "needs_attention" })));
  });

  it("drops anything unknown or malformed instead of sending it", () => {
    expect(parse("queue=everything")).toEqual(defaultInboxView);
    // A status outside the queue, or any status of a single-status queue, is no filter.
    expect(parse("queue=en_route&status=DELIVERED").status).toBeNull();
    expect(parse("queue=delivered_not_closed&status=DELIVERED").status).toBeNull();
    expect(parse("queue=unassigned&status=ASSIGNED").status).toBeNull();
    expect(parse("service_type=OVERNIGHT").serviceType).toBeNull();
    for (const bad of [zone.toUpperCase(), "00000000-0000-0000-0000-000000000000", "not-a-zone", `${zone}x`, ""])
      expect(parse(`zone=${encodeURIComponent(bad)}`).zoneId, bad).toBeNull();
    // Unknown parameters never reach the URL the inbox builds.
    expect(inboxHref(parse("queue=en_route&cursor=abc&order_id=1&owner_org_id=2"))).toBe("/ops/inbox?queue=en_route");
  });

  it("opens the order detail remembering the view, and returns only to a canonical inbox URL", () => {
    const selected = view({ queue: "en_route", status: "DELIVERING", zoneId: zone });
    const href = inboxOrderHref(order, selected);
    expect(href.startsWith(`/ops/orders/${order}?inbox=`)).toBe(true);
    const back = new URL(href, "https://paquetenvia.test").searchParams.get("inbox");
    expect(inboxReturnHref(back)).toBe(inboxHref(selected));
    // Not opened from the inbox, or a repeated parameter: no back link.
    expect(inboxReturnHref(undefined)).toBeNull();
    expect(inboxReturnHref(null)).toBeNull();
    expect(inboxReturnHref(["queue=en_route", "queue=unassigned"])).toBeNull();
    expect(inboxReturnHref("queue=en_route&x=".padEnd(600, "y"))).toBeNull();
    // Whatever the value holds, the link stays on the inbox with a parsed view.
    for (const hostile of ["https://evil.test/ops/inbox", "//evil.test", "javascript:alert(1)", "queue=../../x"])
      expect(inboxReturnHref(hostile), hostile).toBe("/ops/inbox?queue=unassigned");
    expect(() => inboxOrderHref("not-an-order", selected)).toThrow();
  });
});
