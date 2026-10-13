import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RateLimitWaits } from "../../lib/rate-limit-wait";
import { TenantApiError } from "../api/tenant-request";
import type { AssignableDriver, AssignableDriverPage, Assignment } from "../contracts/assignable-driver";
import { intersectEligibility, type BulkDriverChoice, type BulkOrder } from "../contracts/bulk-assignment";
import { syntheticUuid } from "../contracts/ui-001-screens.fixtures";
import {
  BulkAssignmentController,
  bulkDriversTooManyMessage,
  bulkDriversUnavailableMessage,
  bulkNotFoundMessage,
  bulkStoppedRetryMessage,
  maximumDriverPagesPerOrder,
  type BulkAssignmentApi,
  type BulkAssignmentState,
  type BulkDriversLoad,
} from "./bulk-assignment-controller";
import { assignmentConflictMessages } from "./driver-assignment-controller";

const driver: AssignableDriver = {
  driver_id: syntheticUuid(0x601),
  driver_reference: "DRV-0a1b2c3d",
  vehicle_type: "MOTORCYCLE",
  eligible: true,
  ineligibility_reasons: [],
  active_assignment_count: 0,
};
const other: AssignableDriver = { ...driver, driver_id: syntheticUuid(0x602), driver_reference: "DRV-0a1b2c3e" };

function bulkOrder(counter: number): BulkOrder {
  return {
    orderId: syntheticUuid(0x100 + counter),
    publicId: `ORD_${String(counter).padStart(22, "0")}`,
    zoneName: "Centro",
    totalCents: 9_000,
  };
}

const [first, second, third] = [bulkOrder(1), bulkOrder(2), bulkOrder(3)];

function page(items: readonly AssignableDriver[], next_cursor: string | null = null): AssignableDriverPage {
  return { items, next_cursor };
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

interface AssignCall {
  readonly orderId: string;
  readonly driverId: string;
  readonly costCents: number;
  readonly key: string;
}

/** Answers listAssignableDrivers per order and cursor and assignDriver per order, recording every call in order. */
class FakeApi implements BulkAssignmentApi {
  public readonly calls: string[] = [];
  public readonly assignCalls: AssignCall[] = [];
  public readonly signals: AbortSignal[] = [];
  public lists: Record<string, () => Promise<AssignableDriverPage>> = {};
  public assignAnswers: Record<string, (() => Promise<Assignment>)[]> = {};

  public listAssignableDrivers(orderId: string, cursor?: string | null, signal?: AbortSignal): Promise<AssignableDriverPage> {
    const key = `${orderId}${cursor ? `@${cursor}` : ""}`;
    this.calls.push(`list ${key}`);
    if (signal !== undefined) this.signals.push(signal);
    const answer = this.lists[key];
    return answer === undefined ? Promise.resolve(page([driver, other])) : answer();
  }

  public assignDriver(
    orderId: string,
    driverId: string,
    costCents: number,
    key: string,
    signal?: AbortSignal,
  ): Promise<Assignment> {
    this.calls.push(`assign ${orderId}`);
    this.assignCalls.push({ orderId, driverId, costCents, key });
    if (signal !== undefined) this.signals.push(signal);
    const answer = this.assignAnswers[orderId]?.shift();
    return answer === undefined ? Promise.resolve(assignment(orderId, driverId, costCents)) : answer();
  }
}

function assignment(orderId: string, driverId: string, costCents: number): Assignment {
  return {
    id: syntheticUuid(0x700),
    order_id: orderId,
    driver_id: driverId,
    route_id: null,
    status: "ACCEPTED",
    cost: { currency: "MXN", amount_cents: costCents },
  };
}

function setup(api = new FakeApi()) {
  const states: BulkAssignmentState[] = [];
  const onChanged = vi.fn();
  let counter = 0;
  const controller = new BulkAssignmentController(
    api,
    (state) => states.push(state),
    onChanged,
    "/ops/inbox?queue=unassigned",
    () => `uuid-${++counter}`,
  );
  return { controller, api, states, onChanged };
}

/** The driver choice the panel would offer once every list loaded. */
function choiceFor(state: BulkAssignmentState, driverId = driver.driver_id): BulkDriverChoice {
  const lists = new Map(
    state.orders.map((order) => {
      const load = state.drivers[order.orderId] as Extract<BulkDriversLoad, { kind: "ready" }>;
      return [order.orderId, load.drivers] as const;
    }),
  );
  const eligibility = intersectEligibility(state.orders, lists);
  const choice = [...eligibility.eligible, ...eligibility.blocked].find((item) => item.driverId === driverId);
  if (choice === undefined) throw new Error("The driver is not listed.");
  return choice;
}

function costs(entries: readonly [BulkOrder, number][]): ReadonlyMap<string, number> {
  return new Map(entries.map(([order, cents]) => [order.orderId, cents]));
}

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

describe("bulk assignment controller (UI-PHASE3-INBOX-PRICE-REVIEW-BULK-2026-10-11)", () => {
  it("reads every page of each order's drivers, one order after another", async () => {
    const firstPage = deferred<AssignableDriverPage>();
    const api = new FakeApi();
    api.lists = {
      [first.orderId]: () => firstPage.promise,
      [`${first.orderId}@next-1`]: () => Promise.resolve(page([other])),
      [second.orderId]: () => Promise.resolve(page([driver])),
    };
    const { controller } = setup(api);
    const loading = controller.load([first, second]);
    await flush();
    // The second order waits for the first one's list.
    expect(api.calls).toEqual([`list ${first.orderId}`]);
    expect(controller.current.phase).toBe("loading");
    firstPage.resolve(page([driver], "next-1"));
    await loading;
    expect(api.calls).toEqual([`list ${first.orderId}`, `list ${first.orderId}@next-1`, `list ${second.orderId}`]);
    expect(controller.current.phase).toBe("ready");
    expect(controller.current.drivers[first.orderId]).toEqual({ kind: "ready", drivers: [driver, other] });
    expect(controller.current.drivers[second.orderId]).toEqual({ kind: "ready", drivers: [driver] });
  });

  it("marks an order whose list cannot be read and lets it be read again or taken out", async () => {
    const api = new FakeApi();
    let failing = true;
    api.lists = {
      // A repeated cursor never ends: the list is incomplete.
      [first.orderId]: () => Promise.resolve(page([driver], "loop")),
      [`${first.orderId}@loop`]: () => Promise.resolve(page([], "loop")),
      [second.orderId]: () =>
        failing ? Promise.reject(new TenantApiError("network")) : Promise.resolve(page([driver])),
      [third.orderId]: () => Promise.reject(new TenantApiError("conflict", "CONFLICT")),
    };
    const { controller } = setup(api);
    await controller.load([first, second, third]);
    expect(controller.current.phase).toBe("ready");
    expect(controller.current.drivers[first.orderId]).toEqual({ kind: "failed", message: bulkDriversTooManyMessage, retryable: false });
    expect(controller.current.drivers[second.orderId]).toEqual({ kind: "failed", message: bulkDriversUnavailableMessage, retryable: true });
    expect(controller.current.drivers[third.orderId]).toEqual({
      kind: "failed",
      message: "Esta orden ya no admite asignación.",
      retryable: false,
    });

    failing = false;
    await controller.retryLoad(second.orderId);
    expect(controller.current.drivers[second.orderId]).toEqual({ kind: "ready", drivers: [driver] });

    controller.remove(first.orderId);
    controller.remove(third.orderId);
    expect(controller.current.orders).toEqual([second]);
    expect(Object.keys(controller.current.drivers)).toEqual([second.orderId]);
  });

  it("gives up on an order after the page limit", async () => {
    const api = new FakeApi();
    let pageNumber = 0;
    api.lists = { [first.orderId]: () => Promise.resolve(page([driver], `p-${++pageNumber}`)) };
    for (let index = 1; index < maximumDriverPagesPerOrder + 2; index += 1)
      api.lists[`${first.orderId}@p-${index}`] = () => Promise.resolve(page([], `p-${++pageNumber}`));
    const { controller } = setup(api);
    await controller.load([first]);
    expect(api.calls).toHaveLength(maximumDriverPagesPerOrder);
    expect(controller.current.drivers[first.orderId]).toMatchObject({ kind: "failed", retryable: false });
  });

  it("assigns one order after another, each with its own Idempotency-Key, then reads the inbox again", async () => {
    const api = new FakeApi();
    const firstAnswer = deferred<Assignment>();
    api.assignAnswers = { [first.orderId]: [() => firstAnswer.promise] };
    const { controller, onChanged } = setup(api);
    await controller.load([first, second]);
    const running = controller.execute({ driver: choiceFor(controller.current), costs: costs([[first, 4_500], [second, 0]]) });
    await flush();
    // The second order is sent only after the first one answered.
    expect(api.assignCalls.map((call) => call.orderId)).toEqual([first.orderId]);
    expect(controller.current.results[first.orderId]).toEqual({ kind: "sending" });
    expect(controller.current.results[second.orderId]).toEqual({ kind: "pending" });
    firstAnswer.resolve(assignment(first.orderId, driver.driver_id, 4_500));
    await running;

    expect(api.assignCalls).toEqual([
      { orderId: first.orderId, driverId: driver.driver_id, costCents: 4_500, key: "assign-driver-uuid-1" },
      { orderId: second.orderId, driverId: driver.driver_id, costCents: 0, key: "assign-driver-uuid-2" },
    ]);
    expect(controller.current.phase).toBe("done");
    expect(controller.current.results).toEqual({
      [first.orderId]: { kind: "assigned" },
      [second.orderId]: { kind: "assigned" },
    });
    expect(controller.current.message).toBe("Se asignaron las 2 órdenes.");
    expect(controller.current.canRetry).toBe(false);
    expect(onChanged).toHaveBeenCalledTimes(1);
  });

  it("keeps going after a rejection of one order and shows its message, never hiding the failure", async () => {
    const api = new FakeApi();
    api.assignAnswers = {
      [first.orderId]: [() => Promise.reject(new TenantApiError("conflict", "DRIVER_INELIGIBLE"))],
      [third.orderId]: [() => Promise.reject(new TenantApiError("not_found"))],
    };
    const { controller, onChanged } = setup(api);
    await controller.load([first, second, third]);
    await controller.execute({
      driver: choiceFor(controller.current),
      costs: costs([[first, 100], [second, 200], [third, 300]]),
    });
    expect(api.assignCalls.map((call) => call.orderId)).toEqual([first.orderId, second.orderId, third.orderId]);
    expect(controller.current.results).toEqual({
      [first.orderId]: { kind: "failed", message: assignmentConflictMessages.DRIVER_INELIGIBLE, retryable: false },
      [second.orderId]: { kind: "assigned" },
      [third.orderId]: { kind: "failed", message: bulkNotFoundMessage, retryable: false },
    });
    expect(controller.current.message).toBe("Se asignaron 1 de 3 órdenes. Revisa el motivo de las demás en su fila.");
    expect(controller.current.canRetry).toBe(false);
    expect(onChanged).toHaveBeenCalledTimes(1);
    // A definitive answer leaves nothing to resend.
    await controller.retry();
    expect(api.assignCalls).toHaveLength(3);
  });

  it("stops on a network failure, sends nothing more and retries with the same key and payload", async () => {
    const api = new FakeApi();
    api.assignAnswers = { [second.orderId]: [() => Promise.reject(new TenantApiError("network"))] };
    const { controller, onChanged } = setup(api);
    await controller.load([first, second, third]);
    await controller.execute({
      driver: choiceFor(controller.current),
      costs: costs([[first, 1_000], [second, 2_000], [third, 3_000]]),
    });
    expect(api.assignCalls.map((call) => call.orderId)).toEqual([first.orderId, second.orderId]);
    expect(controller.current.results[first.orderId]).toEqual({ kind: "assigned" });
    expect(controller.current.results[second.orderId]).toMatchObject({ kind: "failed", retryable: true });
    expect(controller.current.results[third.orderId]).toEqual({ kind: "not_sent" });
    expect(controller.current.message).toBe(
      `Se asignaron 1 de 3 órdenes. Revisa el motivo de las demás en su fila. ${bulkStoppedRetryMessage}`,
    );
    expect(controller.current.canRetry).toBe(true);
    expect(onChanged).toHaveBeenCalledTimes(1);

    await controller.retry();
    // The assigned order is never sent again; the failed one reuses its key, the unsent one gets its own.
    expect(api.assignCalls.slice(2)).toEqual([
      { orderId: second.orderId, driverId: driver.driver_id, costCents: 2_000, key: api.assignCalls[1].key },
      { orderId: third.orderId, driverId: driver.driver_id, costCents: 3_000, key: "assign-driver-uuid-3" },
    ]);
    expect(new Set(api.assignCalls.map((call) => call.key)).size).toBe(3);
    expect(Object.values(controller.current.results)).toEqual([
      { kind: "assigned" },
      { kind: "assigned" },
      { kind: "assigned" },
    ]);
    expect(controller.current.message).toBe("Se asignaron las 3 órdenes.");
    expect(controller.current.canRetry).toBe(false);
    expect(onChanged).toHaveBeenCalledTimes(2);
  });

  it("stops when MFA is required and offers the step-up back to the inbox", async () => {
    const api = new FakeApi();
    api.assignAnswers = {
      [first.orderId]: [() => Promise.reject(new TenantApiError("forbidden", "MFA_REQUIRED", true))],
    };
    const { controller } = setup(api);
    await controller.load([first, second]);
    await controller.execute({ driver: choiceFor(controller.current), costs: costs([[first, 1], [second, 2]]) });
    expect(api.assignCalls).toHaveLength(1);
    expect(controller.current.results[second.orderId]).toEqual({ kind: "not_sent" });
    expect(controller.current.stepUpHref).toBe("/login?mfa=required&return_url=%2Fops%2Finbox%3Fqueue%3Dunassigned");
    expect(controller.current.canRetry).toBe(false);
    expect(controller.current.message).toContain("El envío se detuvo:");
  });

  it("sends nothing for a run that misses an order, a cost in cents or uses a blocked driver", async () => {
    const api = new FakeApi();
    api.lists = { [second.orderId]: () => Promise.resolve(page([driver, { ...other, eligible: false, ineligibility_reasons: ["DOCUMENT_EXPIRED"] }])) };
    const { controller } = setup(api);
    await controller.load([first, second]);
    const eligible = choiceFor(controller.current);
    await controller.execute({ driver: eligible, costs: costs([[first, 100]]) });
    await controller.execute({ driver: eligible, costs: costs([[first, 100], [second, 12.5]]) });
    await controller.execute({ driver: eligible, costs: costs([[first, 100], [second, -1]]) });
    await controller.execute({ driver: choiceFor(controller.current, other.driver_id), costs: costs([[first, 1], [second, 1]]) });
    expect(api.assignCalls).toEqual([]);
    expect(controller.current.phase).toBe("ready");
  });

  it("aborts the request in flight when the panel closes and ignores its late answer", async () => {
    const api = new FakeApi();
    const pending = deferred<Assignment>();
    api.assignAnswers = { [first.orderId]: [() => pending.promise] };
    const { controller, states, onChanged } = setup(api);
    await controller.load([first, second]);
    const running = controller.execute({ driver: choiceFor(controller.current), costs: costs([[first, 1], [second, 2]]) });
    await flush();
    controller.dispose();
    expect(api.signals.at(-1)?.aborted).toBe(true);
    const seen = states.length;
    pending.resolve(assignment(first.orderId, driver.driver_id, 1));
    await running;
    expect(api.assignCalls).toHaveLength(1);
    expect(states).toHaveLength(seen);
    expect(onChanged).not.toHaveBeenCalled();
  });
});

describe("bulk assignment under the SEC-004 limit (owner answer Q10)", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-13T12:00:00Z"));
  });
  afterEach(() => vi.useRealTimers());

  it("fails only the order that met the 429, makes the rest wait for the same wait, and re-enables its retry at 0", async () => {
    const waits = new RateLimitWaits();
    const api = new FakeApi();
    api.lists[second.orderId] = () => {
      waits.record("assignable_drivers", 2);
      return Promise.reject(new TenantApiError("rate_limited", null, false, 2));
    };
    const controller = new BulkAssignmentController(api, () => undefined, () => undefined, "/ops/inbox", () => "uuid", {
      waits,
    });
    const loading = controller.load([first, second, third]);
    await vi.advanceTimersByTimeAsync(0);
    expect(api.calls).toEqual([`list ${first.orderId}`, `list ${second.orderId}`]);
    expect(controller.current.drivers[second.orderId]).toEqual({
      kind: "failed",
      message: bulkDriversUnavailableMessage,
      retryable: true,
    });
    expect(controller.rateLimited).toBe(true);

    // The third order is not read during the wait.
    await vi.advanceTimersByTimeAsync(1_999);
    expect(api.calls).toHaveLength(2);
    await vi.advanceTimersByTimeAsync(1);
    await loading;
    expect(api.calls).toEqual([`list ${first.orderId}`, `list ${second.orderId}`, `list ${third.orderId}`]);
    expect(controller.current.phase).toBe("ready");
    expect(controller.rateLimited).toBe(false);

    // The order that met the limit is not read again by itself; its "Reintentar" works again.
    await vi.advanceTimersByTimeAsync(60_000);
    expect(api.calls).toHaveLength(3);
    delete api.lists[second.orderId];
    await controller.retryLoad(second.orderId);
    expect(api.calls).toHaveLength(4);
    expect(controller.current.drivers[second.orderId]?.kind).toBe("ready");
  });

  it("refuses 'Reintentar' while the wait lasts", async () => {
    const waits = new RateLimitWaits();
    const api = new FakeApi();
    let limited = true;
    api.lists[first.orderId] = () => {
      if (!limited) return Promise.resolve(page([driver]));
      waits.record("assignable_drivers", 5);
      return Promise.reject(new TenantApiError("rate_limited", null, false, 5));
    };
    const controller = new BulkAssignmentController(api, () => undefined, () => undefined, "/ops/inbox", () => "uuid", {
      waits,
    });
    await controller.load([first]);
    limited = false;
    await controller.retryLoad(first.orderId);
    expect(api.calls).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(5_000);
    await controller.retryLoad(first.orderId);
    expect(api.calls).toHaveLength(2);
    expect(controller.current.drivers[first.orderId]?.kind).toBe("ready");
  });
});
