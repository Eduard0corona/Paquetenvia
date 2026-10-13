import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RateLimitWaits } from "../../lib/rate-limit-wait";
import { OperationsApiError } from "../api/operations-api";
import { buildOperationsDashboardSearch } from "../api/operations-api";
import { defaultInboxView, type InboxView } from "../contracts/inbox";
import type {
  OperationsDashboardFilters,
  OperationsDashboardOrder,
  OperationsDashboardResponse,
  OrderStatus,
} from "../contracts/operations-dashboard";
import type { OperationsQueueCounts } from "../contracts/queue-counts";
import type { OperationsSession } from "../session/operations-session";
import {
  InboxController,
  inboxRateLimit,
  inboxLoadFailedMessage,
  inboxPaginationFailedMessage,
  type InboxApi,
} from "./inbox-controller";

const session = {
  organizationId: "22222222-2222-4222-8222-222222222222",
  sessionNamespace: "inbox-test",
  credentialMode: "cookie",
  getCsrfToken: () => "csrf",
} as unknown as OperationsSession;

const zoneA = { operating_zone_id: "5a2b3c4d-1111-4222-8333-944445555666", name: "Centro", zone_type: "CORE" } as const;
const zoneB = { operating_zone_id: "6b2b3c4d-1111-4222-8333-944445555666", name: "Aeropuerto", zone_type: "STANDARD" } as const;

let sequence = 0;
function row(minute: number, status: OrderStatus, overrides: Partial<OperationsDashboardOrder> = {}): OperationsDashboardOrder {
  sequence += 1;
  const instant = new Date(Date.UTC(2026, 9, 10, 18, minute)).toISOString();
  return {
    order_id: `00000000-0000-4000-8000-${String(sequence).padStart(12, "0")}`,
    aggregate_version: 1,
    public_id: `ORD_${String(sequence).padStart(22, "0")}`,
    owner: { organization_id: "11111111-1111-4111-8111-111111111111", display_name: "Dueña" },
    operator: null,
    client: null,
    status,
    created_at: instant,
    updated_at: instant,
    service_type: "SAME_DAY",
    pickup_window: null,
    delivery_window: null,
    delivery_zone: null,
    assignment: null,
    latest_driver_location: null,
    total: { currency: "MXN", amount_cents: 9_000 },
    cost_warning: null,
    unassigned_alert: status === "READY_FOR_PICKUP" || status === "RESCHEDULED",
    ...overrides,
  };
}

function page(items: readonly OperationsDashboardOrder[], nextCursor: string | null = null): OperationsDashboardResponse {
  return { generated_at: "2026-10-10T19:00:00Z", items, next_cursor: nextCursor };
}

function counts(unassigned: number): OperationsQueueCounts {
  return {
    generated_at: "2026-10-10T19:00:00Z",
    total: unassigned,
    by_status: {
      DRAFT: 0, CONFIRMED: 0, READY_FOR_PICKUP: unassigned, ASSIGNED: 0, AT_PICKUP: 0, PICKED_UP: 0,
      IN_TRANSIT: 0, DELIVERING: 0, FAILED_ATTEMPT: 0, RESCHEDULED: 0, RETURNING: 0, RETURNED: 0,
      DELIVERED: 0, CLOSED: 0, CLAIM_OPEN: 0, CLAIM_RESOLVED: 0, CANCELLED: 0,
    },
    queues: { unassigned, needs_attention: 0, price_review: 0, delivered_not_closed: 0, en_route: 0 },
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

/** Answers each dashboard query by its query string; anything unexpected fails the test. */
class FakeApi implements InboxApi {
  public readonly requests: string[] = [];
  public countRequests = 0;
  public countAnswer: () => Promise<OperationsQueueCounts> = () => Promise.resolve(counts(1));
  public readonly signals: AbortSignal[] = [];

  public constructor(
    private readonly answers: Record<string, () => Promise<OperationsDashboardResponse>>,
  ) {}

  public list(filters: OperationsDashboardFilters, signal?: AbortSignal): Promise<OperationsDashboardResponse> {
    const key = buildOperationsDashboardSearch(filters).toString();
    this.requests.push(key);
    if (signal !== undefined) this.signals.push(signal);
    const answer = this.answers[key];
    if (answer === undefined) return Promise.reject(new Error(`Unexpected query ${key}`));
    return answer();
  }

  public queueCounts(): Promise<OperationsQueueCounts> {
    this.countRequests += 1;
    return this.countAnswer();
  }
}

function controllerFor(
  api: FakeApi,
  view: InboxView = defaultInboxView,
  options: { readSession?: () => OperationsSession | null; hidden?: () => boolean } = {},
) {
  return new InboxController(
    {
      readSession: options.readSession ?? (() => session),
      createApi: () => api,
      isHidden: options.hidden ?? (() => false),
    },
    view,
  );
}

describe("work inbox controller (UI-PHASE3-INBOX-2026-10-10)", () => {
  it("shows nothing without a session", async () => {
    const api = new FakeApi({});
    const controller = controllerFor(api, defaultInboxView, { readSession: () => null });
    expect(await controller.start()).toEqual({ session: null, phase: "no_session" });
    expect(api.requests).toEqual([]);
    expect(controller.getSnapshot().rows).toEqual([]);
  });

  it("loads the selected queue and the real server counts", async () => {
    const ready = row(30, "READY_FOR_PICKUP", { delivery_zone: zoneA });
    const api = new FakeApi({ "unassigned=true": () => Promise.resolve(page([ready], "next-1")) });
    const controller = controllerFor(api);
    expect(await controller.start()).toEqual({ session, phase: "ready" });
    await flush();
    const state = controller.getSnapshot();
    expect(api.requests).toEqual(["unassigned=true"]);
    expect(state.rows).toEqual([ready]);
    expect(state.hasMore).toBe(true);
    expect(state.loaded).toBe(true);
    expect(state.lastUpdated).toBe("2026-10-10T19:00:00Z");
    expect(state.counts?.queues.unassigned).toBe(1);
    expect(state.countsUnavailable).toBe(false);
    expect(state.zones).toEqual([{ id: zoneA.operating_zone_id, label: "Centro" }]);
    expect(api.countRequests).toBe(1);
  });

  it("merges a multi-status queue from one query per status", async () => {
    const failed = row(50, "FAILED_ATTEMPT");
    const claim = row(40, "CLAIM_OPEN");
    const api = new FakeApi({
      "status=FAILED_ATTEMPT": () => Promise.resolve(page([failed])),
      "status=RESCHEDULED": () => Promise.resolve(page([])),
      "status=RETURNING": () => Promise.resolve(page([])),
      "status=CLAIM_OPEN": () => Promise.resolve(page([claim])),
    });
    const controller = controllerFor(api, { ...defaultInboxView, queue: "needs_attention" });
    await controller.start();
    expect(api.requests).toEqual(["status=FAILED_ATTEMPT", "status=RESCHEDULED", "status=RETURNING", "status=CLAIM_OPEN"]);
    expect(controller.getSnapshot().rows).toEqual([failed, claim]);
    expect(controller.getSnapshot().hasMore).toBe(false);
  });

  it("pages every unfinished query with its own cursor and reveals the held rows", async () => {
    const transitNew = row(59, "IN_TRANSIT");
    const transitOld = row(30, "IN_TRANSIT");
    const deliveringNew = row(45, "DELIVERING");
    const deliveringOld = row(20, "DELIVERING");
    const api = new FakeApi({
      "status=IN_TRANSIT": () => Promise.resolve(page([transitNew], "t-1")),
      "status=DELIVERING": () => Promise.resolve(page([deliveringNew, deliveringOld])),
      "status=IN_TRANSIT&cursor=t-1": () => Promise.resolve(page([transitOld])),
    });
    const controller = controllerFor(api, { ...defaultInboxView, queue: "en_route" });
    await controller.start();
    // IN_TRANSIT could still hold rows newer than 18:45 below its cursor.
    expect(controller.getSnapshot().rows).toEqual([transitNew]);
    expect(controller.getSnapshot().hasMore).toBe(true);
    await controller.loadMore();
    // Only the query that still has pages is read again, with its own cursor.
    expect(api.requests).toEqual(["status=IN_TRANSIT", "status=DELIVERING", "status=IN_TRANSIT&cursor=t-1"]);
    expect(controller.getSnapshot().rows).toEqual([transitNew, deliveringNew, transitOld, deliveringOld]);
    expect(controller.getSnapshot().hasMore).toBe(false);
    // Paging does not read the counts again; a full read does.
    expect(api.countRequests).toBe(1);
  });

  it("lists price review through the server filter, with its own cursor (UI-PHASE3-INBOX-PRICE-REVIEW-BULK-2026-10-11)", async () => {
    const draft = row(50, "DRAFT", { cost_warning: "BELOW_MINIMUM_SNAPSHOT" });
    const claim = row(40, "CLAIM_OPEN", { cost_warning: "AUTHORIZED_OVERRIDE" });
    const ready = row(30, "READY_FOR_PICKUP", { cost_warning: "AUTHORIZED_OVERRIDE" });
    const api = new FakeApi({
      "price_review=true": () => Promise.resolve(page([draft, claim], "p-1")),
      "price_review=true&cursor=p-1": () => Promise.resolve(page([ready])),
    });
    const controller = controllerFor(api, { ...defaultInboxView, queue: "price_review" });
    await controller.start();
    expect(controller.getSnapshot().rows).toEqual([draft, claim]);
    await controller.loadMore();
    // One query, paged by the server: never a list built by filtering other rows.
    expect(api.requests).toEqual(["price_review=true", "price_review=true&cursor=p-1"]);
    expect(controller.getSnapshot().rows).toEqual([draft, claim, ready]);
    expect(controller.getSnapshot().error).toBeNull();
  });

  it.each([
    ["a finished order", row(20, "DELIVERED", { cost_warning: "AUTHORIZED_OVERRIDE" })],
    ["an order without a warning", row(20, "DRAFT")],
  ])("fails closed when the price review answer holds %s", async (_name, outside) => {
    const kept = row(50, "CONFIRMED", { cost_warning: "AUTHORIZED_OVERRIDE" });
    let answer: readonly OperationsDashboardOrder[] = [kept];
    const api = new FakeApi({ "price_review=true": () => Promise.resolve(page(answer)) });
    const controller = controllerFor(api, { ...defaultInboxView, queue: "price_review" });
    await controller.start();
    expect(controller.getSnapshot().rows).toEqual([kept]);
    answer = [kept, outside];
    await controller.refresh("manual");
    // The rows of the last valid read stay; the order outside the rule is never listed.
    expect(controller.getSnapshot().rows).toEqual([kept]);
    expect(controller.getSnapshot().error).toBe(inboxLoadFailedMessage);
  });

  it("stops paging when the server repeats a cursor", async () => {
    const api = new FakeApi({
      "unassigned=true": () => Promise.resolve(page([row(30, "READY_FOR_PICKUP")], "loop")),
      "unassigned=true&cursor=loop": () => Promise.resolve(page([row(20, "READY_FOR_PICKUP")], "loop")),
    });
    const controller = controllerFor(api);
    await controller.start();
    const before = controller.getSnapshot().rows;
    await controller.loadMore();
    expect(controller.getSnapshot().error).toBe(inboxPaginationFailedMessage);
    expect(controller.getSnapshot().rows).toEqual(before);
  });

  it("loads a new view from scratch and drops answers of the previous one", async () => {
    const slow = deferred<OperationsDashboardResponse>();
    const delivered = row(10, "DELIVERED");
    const api = new FakeApi({
      "unassigned=true": () => slow.promise,
      "status=DELIVERED": () => Promise.resolve(page([delivered])),
    });
    const controller = controllerFor(api);
    const started = controller.start();
    controller.setView({ ...defaultInboxView, queue: "delivered_not_closed" });
    await flush();
    expect(api.signals[0]?.aborted).toBe(true);
    slow.resolve(page([row(50, "READY_FOR_PICKUP")]));
    await started;
    await flush();
    expect(controller.getSnapshot().view.queue).toBe("delivered_not_closed");
    expect(controller.getSnapshot().rows).toEqual([delivered]);
    // The same view again changes nothing.
    const requests = api.requests.length;
    controller.setView({ ...defaultInboxView, queue: "delivered_not_closed" });
    expect(api.requests.length).toBe(requests);
  });

  it("clears everything and stops loading when access is lost", async () => {
    let denied = false;
    const api = new FakeApi({
      "unassigned=true": () =>
        denied ? Promise.reject(new OperationsApiError("forbidden")) : Promise.resolve(page([row(30, "READY_FOR_PICKUP")])),
    });
    const controller = controllerFor(api);
    await controller.start();
    await flush();
    expect(controller.getSnapshot().rows).toHaveLength(1);
    denied = true;
    await controller.refresh("manual");
    const state = controller.getSnapshot();
    expect(state.phase).toBe("access_unavailable");
    expect(state.rows).toEqual([]);
    expect(state.counts).toBeNull();
    const requests = api.requests.length;
    await controller.refresh("polling");
    controller.setView({ ...defaultInboxView, queue: "en_route" });
    expect(api.requests.length).toBe(requests);
    await expect(controller.resynchronize()).rejects.toThrow();
  });

  it("keeps the rows and says so when a read fails", async () => {
    let failing = false;
    const ready = row(30, "READY_FOR_PICKUP");
    const api = new FakeApi({
      "unassigned=true": () => (failing ? Promise.reject(new OperationsApiError("network")) : Promise.resolve(page([ready]))),
    });
    const controller = controllerFor(api);
    await controller.start();
    failing = true;
    await controller.refresh("manual");
    expect(controller.getSnapshot().rows).toEqual([ready]);
    expect(controller.getSnapshot().error).toBe(inboxLoadFailedMessage);
    expect(controller.getSnapshot().phase).toBe("ready");
  });

  it("shows Sin dato when the counts fail and the list still loads", async () => {
    const api = new FakeApi({ "unassigned=true": () => Promise.resolve(page([])) });
    api.countAnswer = () => Promise.reject(new OperationsApiError("unavailable"));
    const controller = controllerFor(api);
    await controller.start();
    await flush();
    expect(controller.getSnapshot().counts).toBeNull();
    expect(controller.getSnapshot().countsUnavailable).toBe(true);
    expect(controller.getSnapshot().loaded).toBe(true);
  });

  it("skips background reads while the page is hidden, never a person's own", async () => {
    let hidden = false;
    const api = new FakeApi({ "unassigned=true": () => Promise.resolve(page([])) });
    const controller = controllerFor(api, defaultInboxView, { hidden: () => hidden });
    await controller.start();
    hidden = true;
    const requests = api.requests.length;
    for (const trigger of ["realtime", "polling", "visibility"] as const) await controller.refresh(trigger);
    expect(api.requests.length).toBe(requests);
    await controller.refresh("manual");
    expect(api.requests.length).toBe(requests + 1);
  });

  it("answers a reconnect with the versions of every loaded order", async () => {
    const first = row(30, "IN_TRANSIT", { aggregate_version: 7 });
    const second = row(20, "DELIVERING", { aggregate_version: 2 });
    const api = new FakeApi({
      "status=IN_TRANSIT": () => Promise.resolve(page([first])),
      "status=DELIVERING": () => Promise.resolve(page([second])),
    });
    const controller = controllerFor(api, { ...defaultInboxView, queue: "en_route" });
    await controller.start();
    const before = api.requests.length;
    expect(await controller.resynchronize()).toEqual({
      aggregate_versions: { [first.order_id]: 7, [second.order_id]: 2 },
    });
    expect(api.requests.length).toBe(before + 2);
  });

  it("starts empty for a new session or organization and remembers zones only within one", async () => {
    const api = new FakeApi({
      "unassigned=true": () => Promise.resolve(page([row(30, "READY_FOR_PICKUP", { delivery_zone: zoneA })])),
      [`delivery_zone_id=${zoneB.operating_zone_id}&unassigned=true`]: () =>
        Promise.resolve(page([row(10, "READY_FOR_PICKUP", { delivery_zone: zoneB })])),
    });
    const controller = controllerFor(api);
    await controller.start();
    controller.setView({ ...defaultInboxView, zoneId: zoneB.operating_zone_id });
    await flush();
    // Zones seen in this session stay available as chips, sorted by name.
    expect(controller.getSnapshot().zones.map((zone) => zone.label)).toEqual(["Aeropuerto", "Centro"]);
    const generation = controller.getSnapshot().sessionGeneration;
    const restart = controller.start();
    expect(controller.getSnapshot().rows).toEqual([]);
    expect(controller.getSnapshot().zones).toEqual([]);
    expect(controller.getSnapshot().counts).toBeNull();
    expect(controller.getSnapshot().sessionGeneration).toBe(generation + 1);
    await restart;
    controller.stop();
    expect(controller.getSnapshot().phase).toBe("no_session");
    expect(controller.getSnapshot().rows).toEqual([]);
  });
});

/** SEC-004: the API's 429 (the client records the wait before throwing, as operations-api does). */
function tooManyRequests(waits: RateLimitWaits, operation: "dashboard" | "queue_counts", seconds: number): Promise<never> {
  waits.record(operation, seconds);
  return Promise.reject(new OperationsApiError("rate_limited", seconds));
}

describe("work inbox under the SEC-004 limits (owner answers Q9, Q11, Q14, Q16)", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-13T12:00:00Z"));
  });
  afterEach(() => vi.useRealTimers());

  function limitedInbox(answers: Record<string, () => Promise<OperationsDashboardResponse>>) {
    const waits = new RateLimitWaits();
    const api = new FakeApi(answers);
    const controller = new InboxController(
      { readSession: () => session, createApi: () => api, isHidden: () => false, waits },
      defaultInboxView,
    );
    return { waits, api, controller };
  }

  it("reads the dashboard and the counts, each with its own wait", () => {
    expect(inboxRateLimit).toEqual(["dashboard", "queue_counts"]);
  });

  it("keeps the rows on a 429, sends nothing during the wait and reads once, by itself, when it ends", async () => {
    const ready = row(10, "READY_FOR_PICKUP");
    let limited = false;
    const { waits, api, controller } = limitedInbox({
      "unassigned=true": () => (limited ? tooManyRequests(waits, "dashboard", 2) : Promise.resolve(page([ready]))),
    });
    await controller.start();
    await vi.advanceTimersByTimeAsync(0);
    expect(api.requests).toHaveLength(1);

    limited = true;
    await controller.refresh("manual");
    expect(api.requests).toHaveLength(2);
    expect(controller.getSnapshot()).toMatchObject({ phase: "ready", error: null, rows: [ready] });

    // Polling, the page becoming visible, "Actualizar", the assignment refresh and realtime notices: nothing is sent.
    limited = false;
    await controller.refresh("polling");
    await controller.refresh("visibility");
    await controller.refresh("manual");
    await controller.refresh("assignment");
    controller.notice();
    controller.notice();
    await controller.loadMore();
    await vi.advanceTimersByTimeAsync(1_999);
    expect(api.requests).toHaveLength(2);

    // At 0: exactly one read, and the counts with it.
    const countsBefore = api.countRequests;
    await vi.advanceTimersByTimeAsync(1);
    await vi.advanceTimersByTimeAsync(0);
    expect(api.requests).toHaveLength(3);
    expect(api.countRequests).toBe(countsBefore + 1);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(api.requests).toHaveLength(3);
    expect(vi.getTimerCount()).toBe(0);
  });

  it("pauses the whole inbox when only the counts meet the limit, keeping the counts shown (Q16)", async () => {
    const ready = row(11, "READY_FOR_PICKUP");
    const { waits, api, controller } = limitedInbox({ "unassigned=true": () => Promise.resolve(page([ready])) });
    await controller.start();
    await vi.advanceTimersByTimeAsync(0);
    expect(controller.getSnapshot().counts?.queues.unassigned).toBe(1);

    api.countAnswer = () => tooManyRequests(waits, "queue_counts", 3);
    await controller.refresh("manual");
    await vi.advanceTimersByTimeAsync(0);
    expect(api.requests).toHaveLength(2);
    expect(controller.getSnapshot()).toMatchObject({ countsUnavailable: false, error: null });
    expect(controller.getSnapshot().counts?.queues.unassigned).toBe(1);

    api.countAnswer = () => Promise.resolve(counts(4));
    await controller.refresh("polling");
    controller.notice();
    await vi.advanceTimersByTimeAsync(2_999);
    expect(api.requests).toHaveLength(2);
    await vi.advanceTimersByTimeAsync(1);
    await vi.advanceTimersByTimeAsync(0);
    expect(api.requests).toHaveLength(3);
    expect(controller.getSnapshot().counts?.queues.unassigned).toBe(4);
  });

  it("keeps a queue or filter change during the wait and reads it once the wait ends (Q14)", async () => {
    const ready = row(12, "READY_FOR_PICKUP");
    const sameDay = row(13, "READY_FOR_PICKUP", { service_type: "SAME_DAY" });
    const { waits, api, controller } = limitedInbox({
      "unassigned=true": () => Promise.resolve(page([ready])),
      "service_type=SAME_DAY&unassigned=true": () => Promise.resolve(page([sameDay])),
    });
    await controller.start();
    await vi.advanceTimersByTimeAsync(0);
    waits.record("dashboard", 5);

    controller.setView({ ...defaultInboxView, serviceType: "SAME_DAY" });
    await vi.advanceTimersByTimeAsync(4_999);
    expect(api.requests).toEqual(["unassigned=true"]);
    expect(controller.getSnapshot().view.serviceType).toBe("SAME_DAY");
    await vi.advanceTimersByTimeAsync(1);
    await vi.advanceTimersByTimeAsync(0);
    expect(api.requests).toEqual(["unassigned=true", "service_type=SAME_DAY&unassigned=true"]);
    expect(controller.getSnapshot().rows).toEqual([sameDay]);
  });

  it("gathers realtime notices into at most one read every 3 seconds, losing none (Q9)", async () => {
    const ready = row(14, "READY_FOR_PICKUP");
    const { api, controller } = limitedInbox({ "unassigned=true": () => Promise.resolve(page([ready])) });
    await controller.start();
    await vi.advanceTimersByTimeAsync(0);
    await vi.advanceTimersByTimeAsync(5_000);

    // A bulk assignment: 30 notices in 1.5 s.
    for (let notice = 0; notice < 30; notice += 1) {
      controller.notice();
      await vi.advanceTimersByTimeAsync(50);
    }
    expect(api.requests).toHaveLength(2);
    // The notices after the first read are read 3 s after it (t = 8250 ms).
    await vi.advanceTimersByTimeAsync(1_749);
    expect(api.requests).toHaveLength(2);
    await vi.advanceTimersByTimeAsync(1);
    expect(api.requests).toHaveLength(3);
    // One more notice right after: read 3 s after the previous read, not lost.
    await vi.advanceTimersByTimeAsync(100);
    controller.notice();
    await vi.advanceTimersByTimeAsync(2_800);
    expect(api.requests).toHaveLength(3);
    await vi.advanceTimersByTimeAsync(100);
    expect(api.requests).toHaveLength(4);
    await vi.advanceTimersByTimeAsync(30_000);
    expect(api.requests).toHaveLength(4);
  });

  it("lets a realtime reconnect wait out the server's wait, then resynchronize as before", async () => {
    const ready = row(15, "READY_FOR_PICKUP");
    const { waits, api, controller } = limitedInbox({ "unassigned=true": () => Promise.resolve(page([ready])) });
    await controller.start();
    await vi.advanceTimersByTimeAsync(0);
    waits.record("dashboard", 2);
    let snapshot: unknown = null;
    void controller.resynchronize().then((value) => (snapshot = value));
    await vi.advanceTimersByTimeAsync(1_999);
    expect(api.requests).toHaveLength(1);
    expect(snapshot).toBeNull();
    await vi.advanceTimersByTimeAsync(1);
    await vi.advanceTimersByTimeAsync(0);
    expect(api.requests).toHaveLength(2);
    expect(snapshot).toEqual({ aggregate_versions: { [ready.order_id]: 1 } });
  });

  it("leaves no timer behind when the screen stops during the wait", async () => {
    const ready = row(16, "READY_FOR_PICKUP");
    const { waits, api, controller } = limitedInbox({ "unassigned=true": () => Promise.resolve(page([ready])) });
    await controller.start();
    await vi.advanceTimersByTimeAsync(0);
    waits.record("dashboard", 30);
    await controller.refresh("polling");
    controller.notice();
    controller.stop();
    expect(vi.getTimerCount()).toBe(0);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(api.requests).toHaveLength(1);
  });
});
