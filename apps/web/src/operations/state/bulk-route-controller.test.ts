import { describe, expect, it, vi } from "vitest";
import type { BulkRouteApi } from "../api/bulk-route-api";
import { TenantApiError } from "../api/tenant-request";
import type { BulkOrder } from "../contracts/bulk-assignment";
import { routeStopConflictMessages, routeStopNotFoundMessage } from "../contracts/bulk-route";
import type { ManualRoute, ManualRouteDetail, ManualRoutePage } from "../contracts/manual-route";
import { syntheticUuid } from "../contracts/ui-001-screens.fixtures";
import {
  BulkRouteController,
  bulkRouteChangedMessage,
  bulkRouteStoppedRetryMessage,
  bulkRoutesTooManyMessage,
  bulkRoutesUnavailableMessage,
  bulkRouteUnexpectedAnswerMessage,
  maximumRoutePages,
  sendableRouteOrders,
  type BulkRouteState,
} from "./bulk-route-controller";

const driver = { driverId: syntheticUuid(0x601), driverReference: "DRV-0a1b2c3d" };

function bulkOrder(counter: number): BulkOrder {
  return {
    orderId: syntheticUuid(0x100 + counter),
    publicId: `ORD_${String(counter).padStart(22, "0")}`,
    zoneName: "Centro",
    totalCents: 9_000,
  };
}

const [first, second, third] = [bulkOrder(1), bulkOrder(2), bulkOrder(3)];

function route(counter: number, overrides: Partial<ManualRoute> = {}): ManualRoute {
  return {
    id: syntheticUuid(0x800 + counter),
    status: "DRAFT",
    version: 3,
    driver_id: driver.driverId,
    city_id: syntheticUuid(0x900),
    service_area_id: null,
    scheduled_for: "2026-10-12",
    assignment_cost_cents_total: 0,
    stop_count: 1,
    ...overrides,
  };
}

const target = route(1);
const otherRoute = route(2, { scheduled_for: null });

function page(items: readonly ManualRoute[], next_cursor: string | null = null): ManualRoutePage {
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

interface AddCall {
  readonly routeId: string;
  readonly orderId: string;
  readonly expectedVersion: number;
  readonly key: string;
}

/**
 * Answers listDraftRoutes per cursor and addRouteStop per order, recording every call in order.
 * An addRouteStop without a scripted answer succeeds like the server: the route one version later.
 */
class FakeApi implements BulkRouteApi {
  public readonly calls: string[] = [];
  public readonly addCalls: AddCall[] = [];
  public readonly signals: AbortSignal[] = [];
  public lists: Record<string, () => Promise<ManualRoutePage>> = {};
  public addAnswers: Record<string, (() => Promise<ManualRouteDetail>)[]> = {};

  public listDraftRoutes(driverId: string, cursor: string | null, signal?: AbortSignal): Promise<ManualRoutePage> {
    this.calls.push(`list ${driverId}${cursor === null ? "" : `@${cursor}`}`);
    if (signal !== undefined) this.signals.push(signal);
    const answer = this.lists[cursor ?? ""];
    return answer === undefined ? Promise.resolve(page([target, otherRoute])) : answer();
  }

  public addRouteStop(
    routeId: string,
    orderId: string,
    expectedVersion: number,
    key: string,
    signal?: AbortSignal,
  ): Promise<ManualRouteDetail> {
    this.calls.push(`add ${orderId}`);
    this.addCalls.push({ routeId, orderId, expectedVersion, key });
    if (signal !== undefined) this.signals.push(signal);
    const answer = this.addAnswers[orderId]?.shift();
    if (answer !== undefined) return answer();
    // Every version after the one listed added one stop.
    const version = expectedVersion + 1;
    return Promise.resolve(detail(routeId, version, target.stop_count + version - target.version));
  }
}

function detail(routeId: string, version: number, stopCount: number): ManualRouteDetail {
  return {
    ...target,
    id: routeId,
    version,
    stop_count: stopCount,
    stops: Array.from({ length: stopCount }, (_, index) => ({
      id: syntheticUuid(0xa00 + index),
      order_id: syntheticUuid(0x180 + index),
      sequence: index + 1,
      stop_type: "DELIVERY" as const,
      status: "PENDING" as const,
    })),
  };
}

function setup(api = new FakeApi(), orders: readonly BulkOrder[] = [first, second, third]) {
  const states: BulkRouteState[] = [];
  const onChanged = vi.fn();
  let counter = 0;
  const controller = new BulkRouteController(
    api,
    driver,
    orders,
    (state) => states.push(state),
    onChanged,
    "/ops/inbox?queue=unassigned",
    () => `uuid-${++counter}`,
  );
  return { controller, api, states, onChanged };
}

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

describe("bulk add to route controller (UI-PHASE3-INBOX-BULK-ADD-TO-ROUTE-2026-10-11)", () => {
  it("reads every page of the driver's DRAFT routes, one after another", async () => {
    const firstPage = deferred<ManualRoutePage>();
    const api = new FakeApi();
    api.lists = { "": () => firstPage.promise, "next-1": () => Promise.resolve(page([otherRoute])) };
    const { controller } = setup(api);
    expect(controller.current.routes).toEqual({ kind: "loading" });
    const loading = controller.loadRoutes();
    await flush();
    expect(api.calls).toEqual([`list ${driver.driverId}`]);
    firstPage.resolve(page([target], "next-1"));
    await loading;
    expect(api.calls).toEqual([`list ${driver.driverId}`, `list ${driver.driverId}@next-1`]);
    expect(controller.current.routes).toEqual({ kind: "ready", routes: [target, otherRoute] });
    expect(controller.current.phase).toBe("choosing");
    // Nothing is sent before a confirmation.
    expect(api.addCalls).toEqual([]);
  });

  it("says when the routes cannot be read and reads them again on request", async () => {
    const api = new FakeApi();
    let failing = true;
    api.lists = { "": () => (failing ? Promise.reject(new TenantApiError("network")) : Promise.resolve(page([target]))) };
    const { controller } = setup(api);
    await controller.loadRoutes();
    expect(controller.current.routes).toEqual({ kind: "failed", message: bulkRoutesUnavailableMessage, stepUpHref: null });
    failing = false;
    await controller.loadRoutes();
    expect(controller.current.routes).toEqual({ kind: "ready", routes: [target] });
  });

  it("gives up after a repeated cursor or the page limit", async () => {
    const looping = new FakeApi();
    looping.lists = { "": () => Promise.resolve(page([target], "loop")), loop: () => Promise.resolve(page([], "loop")) };
    const loopingRun = setup(looping);
    await loopingRun.controller.loadRoutes();
    expect(loopingRun.controller.current.routes).toEqual({ kind: "failed", message: bulkRoutesTooManyMessage, stepUpHref: null });

    const endless = new FakeApi();
    let pageNumber = 0;
    endless.lists = { "": () => Promise.resolve(page([target], `p-${++pageNumber}`)) };
    for (let index = 1; index < maximumRoutePages + 2; index += 1)
      endless.lists[`p-${index}`] = () => Promise.resolve(page([], `p-${++pageNumber}`));
    const endlessRun = setup(endless);
    await endlessRun.controller.loadRoutes();
    expect(endless.calls).toHaveLength(maximumRoutePages);
    expect(endlessRun.controller.current.routes).toMatchObject({ kind: "failed", message: bulkRoutesTooManyMessage });
  });

  it("adds one order after another, each with its own key and the version of the previous answer", async () => {
    const api = new FakeApi();
    const firstAnswer = deferred<ManualRouteDetail>();
    api.addAnswers = { [first.orderId]: [() => firstAnswer.promise] };
    const { controller, onChanged } = setup(api);
    await controller.loadRoutes();
    const running = controller.execute(target.id);
    await flush();
    // The second order is sent only after the first one answered.
    expect(api.addCalls.map((call) => call.orderId)).toEqual([first.orderId]);
    expect(controller.current.phase).toBe("running");
    expect(controller.current.results[first.orderId]).toEqual({ kind: "sending" });
    expect(controller.current.results[second.orderId]).toEqual({ kind: "pending" });
    firstAnswer.resolve(detail(target.id, 4, 2));
    await running;

    expect(api.addCalls).toEqual([
      { routeId: target.id, orderId: first.orderId, expectedVersion: 3, key: "add-route-stop-uuid-1" },
      { routeId: target.id, orderId: second.orderId, expectedVersion: 4, key: "add-route-stop-uuid-2" },
      { routeId: target.id, orderId: third.orderId, expectedVersion: 5, key: "add-route-stop-uuid-3" },
    ]);
    expect(controller.current.phase).toBe("done");
    expect(Object.values(controller.current.results)).toEqual([{ kind: "added" }, { kind: "added" }, { kind: "added" }]);
    // The last answer is the authoritative route: one version and one stop per order later.
    expect(controller.current.route).toMatchObject({ id: target.id, version: 6, stop_count: 4 });
    expect(controller.current.route).not.toHaveProperty("stops");
    expect(controller.current.message).toBe("Se agregaron las 3 órdenes a la ruta.");
    expect(controller.current.canRetry).toBe(false);
    expect(onChanged).toHaveBeenCalledTimes(1);
  });

  it("keeps going after a rejection of one order and shows its message, never hiding the failure", async () => {
    const api = new FakeApi();
    api.addAnswers = {
      [first.orderId]: [() => Promise.reject(new TenantApiError("conflict", "DRIVER_INELIGIBLE"))],
      [third.orderId]: [() => Promise.reject(new TenantApiError("not_found"))],
    };
    const { controller, onChanged } = setup(api);
    await controller.loadRoutes();
    await controller.execute(target.id);
    expect(api.addCalls.map((call) => [call.orderId, call.expectedVersion])).toEqual([
      [first.orderId, 3],
      // A refused order changes nothing on the route: the next one keeps the version.
      [second.orderId, 3],
      [third.orderId, 4],
    ]);
    expect(controller.current.results).toEqual({
      [first.orderId]: { kind: "failed", message: routeStopConflictMessages.DRIVER_INELIGIBLE, retryable: false },
      [second.orderId]: { kind: "added" },
      [third.orderId]: { kind: "failed", message: routeStopNotFoundMessage, retryable: false },
    });
    expect(controller.current.phase).toBe("done");
    expect(controller.current.message).toBe("Se agregó 1 de 3 órdenes a la ruta. Revisa el motivo de las demás en su fila.");
    expect(controller.current.canRetry).toBe(false);
    expect(onChanged).toHaveBeenCalledTimes(1);
    // A definitive answer leaves nothing to resend.
    await controller.retry();
    expect(api.addCalls).toHaveLength(3);
  });

  it("shows the message of every conflict code, and a default for an unknown one", async () => {
    const api = new FakeApi();
    api.addAnswers = {
      [first.orderId]: [() => Promise.reject(new TenantApiError("conflict", "CONFLICT"))],
      [second.orderId]: [() => Promise.reject(new TenantApiError("conflict", "INVALID_REQUEST"))],
      [third.orderId]: [() => Promise.reject(new TenantApiError("conflict", "SOMETHING_NEW"))],
    };
    const { controller } = setup(api);
    await controller.loadRoutes();
    await controller.execute(target.id);
    expect(Object.values(controller.current.results).map((result) => ("message" in result ? result.message : null))).toEqual([
      routeStopConflictMessages.CONFLICT,
      routeStopConflictMessages.INVALID_REQUEST,
      "La orden no se pudo agregar a esta ruta.",
    ]);
    expect(controller.current.message).toBe("No se agregó ninguna orden a la ruta. Revisa el motivo en cada fila.");
  });

  it("stops when the route changed, reads it again and sends the rest only after a new confirmation", async () => {
    const api = new FakeApi();
    api.addAnswers = { [second.orderId]: [() => Promise.reject(new TenantApiError("conflict", "VERSION_CONFLICT"))] };
    const { controller, onChanged } = setup(api);
    await controller.loadRoutes();
    // Someone else added two stops: the routes read again carry version 7.
    api.lists = { "": () => Promise.resolve(page([{ ...target, version: 7, stop_count: 4 }, otherRoute])) };
    await controller.execute(target.id);

    expect(api.addCalls.map((call) => call.orderId)).toEqual([first.orderId, second.orderId]);
    expect(controller.current.results).toEqual({
      [first.orderId]: { kind: "added" },
      [second.orderId]: { kind: "route_changed" },
      [third.orderId]: { kind: "not_sent" },
    });
    expect(controller.current.phase).toBe("choosing");
    expect(controller.current.message).toBe(
      `Se agregó 1 de 3 órdenes a la ruta. Revisa el motivo de las demás en su fila. ${bulkRouteChangedMessage}`,
    );
    expect(api.calls.filter((call) => call.startsWith("list"))).toHaveLength(2);
    expect(controller.current.route).toMatchObject({ id: target.id, version: 7, stop_count: 4 });
    expect(sendableRouteOrders(controller.current)).toEqual([second, third]);
    expect(onChanged).toHaveBeenCalledTimes(1);

    // The route is fixed: another one is never used for the rest of the batch.
    await controller.execute(otherRoute.id);
    expect(api.addCalls).toHaveLength(2);

    await controller.execute(target.id);
    // A new payload (the version just read) gets new keys; the refused key is never reused.
    expect(api.addCalls.slice(2)).toEqual([
      { routeId: target.id, orderId: second.orderId, expectedVersion: 7, key: "add-route-stop-uuid-3" },
      { routeId: target.id, orderId: third.orderId, expectedVersion: 8, key: "add-route-stop-uuid-4" },
    ]);
    expect(Object.values(controller.current.results)).toEqual([{ kind: "added" }, { kind: "added" }, { kind: "added" }]);
    expect(controller.current.phase).toBe("done");
    expect(controller.current.message).toBe("Se agregaron las 3 órdenes a la ruta.");
    expect(onChanged).toHaveBeenCalledTimes(2);
  });

  it("sends nothing more when the route left DRAFT while it was read again", async () => {
    const api = new FakeApi();
    api.addAnswers = { [first.orderId]: [() => Promise.reject(new TenantApiError("conflict", "VERSION_CONFLICT"))] };
    const { controller } = setup(api);
    await controller.loadRoutes();
    api.lists = { "": () => Promise.resolve(page([otherRoute])) };
    await controller.execute(target.id);
    expect(controller.current.phase).toBe("choosing");
    expect(controller.current.routes).toEqual({ kind: "ready", routes: [otherRoute] });
    // The fixed route is no longer listed: neither it nor another route receives the orders.
    await controller.execute(target.id);
    await controller.execute(otherRoute.id);
    expect(api.addCalls).toHaveLength(1);
  });

  it("stops on a network failure, sends nothing more and retries with the same key and payload", async () => {
    const api = new FakeApi();
    api.addAnswers = { [second.orderId]: [() => Promise.reject(new TenantApiError("network"))] };
    const { controller, onChanged } = setup(api);
    await controller.loadRoutes();
    await controller.execute(target.id);
    expect(api.addCalls.map((call) => call.orderId)).toEqual([first.orderId, second.orderId]);
    expect(controller.current.results[first.orderId]).toEqual({ kind: "added" });
    expect(controller.current.results[second.orderId]).toMatchObject({ kind: "failed", retryable: true });
    expect(controller.current.results[third.orderId]).toEqual({ kind: "not_sent" });
    expect(controller.current.phase).toBe("done");
    expect(controller.current.message).toBe(
      `Se agregó 1 de 3 órdenes a la ruta. Revisa el motivo de las demás en su fila. ${bulkRouteStoppedRetryMessage}`,
    );
    expect(controller.current.canRetry).toBe(true);
    expect(onChanged).toHaveBeenCalledTimes(1);

    await controller.retry();
    // The added order is never sent again; the failed one reuses its key and expected_version, the
    // unsent one gets its own key and the version of the retried answer.
    expect(api.addCalls.slice(2)).toEqual([
      { routeId: target.id, orderId: second.orderId, expectedVersion: 4, key: api.addCalls[1]?.key },
      { routeId: target.id, orderId: third.orderId, expectedVersion: 5, key: "add-route-stop-uuid-3" },
    ]);
    expect(new Set(api.addCalls.map((call) => call.key)).size).toBe(3);
    expect(Object.values(controller.current.results)).toEqual([{ kind: "added" }, { kind: "added" }, { kind: "added" }]);
    expect(controller.current.message).toBe("Se agregaron las 3 órdenes a la ruta.");
    expect(controller.current.canRetry).toBe(false);
    expect(onChanged).toHaveBeenCalledTimes(2);
  });

  it("stops when MFA is required and offers the step-up back to the inbox", async () => {
    const api = new FakeApi();
    api.addAnswers = { [first.orderId]: [() => Promise.reject(new TenantApiError("forbidden", "MFA_REQUIRED", true))] };
    const { controller } = setup(api);
    await controller.loadRoutes();
    await controller.execute(target.id);
    expect(api.addCalls).toHaveLength(1);
    expect(controller.current.results[second.orderId]).toEqual({ kind: "not_sent" });
    expect(controller.current.results[third.orderId]).toEqual({ kind: "not_sent" });
    expect(controller.current.stepUpHref).toBe("/login?mfa=required&return_url=%2Fops%2Finbox%3Fqueue%3Dunassigned");
    expect(controller.current.canRetry).toBe(false);
    expect(controller.current.message).toContain("El envío se detuvo: Esta acción requiere verificar tu identidad");
  });

  it("stops on an unexpected answer without offering to resend it", async () => {
    const api = new FakeApi();
    api.addAnswers = { [first.orderId]: [() => Promise.reject(new TenantApiError("invalid"))] };
    const { controller } = setup(api);
    await controller.loadRoutes();
    await controller.execute(target.id);
    expect(controller.current.results[first.orderId]).toEqual({
      kind: "failed",
      message: bulkRouteUnexpectedAnswerMessage,
      retryable: false,
    });
    expect(controller.current.results[second.orderId]).toEqual({ kind: "not_sent" });
    expect(controller.current.canRetry).toBe(false);
  });

  it("sends nothing to a route that was not listed, is not DRAFT or belongs to another driver", async () => {
    const api = new FakeApi();
    const planned = route(3, { status: "PLANNED" });
    const foreign = route(4, { driver_id: syntheticUuid(0x602) });
    api.lists = { "": () => Promise.resolve(page([target])) };
    const { controller } = setup(api);
    // Before the routes are read nothing can be sent.
    await controller.execute(target.id);
    await controller.loadRoutes();
    await controller.execute(otherRoute.id);
    await controller.execute(planned.id);
    await controller.execute(foreign.id);
    expect(api.addCalls).toEqual([]);
    expect(controller.current.phase).toBe("choosing");
  });

  it("aborts the request in flight when the step closes and ignores its late answer", async () => {
    const api = new FakeApi();
    const pending = deferred<ManualRouteDetail>();
    api.addAnswers = { [first.orderId]: [() => pending.promise] };
    const { controller, states, onChanged } = setup(api);
    await controller.loadRoutes();
    const running = controller.execute(target.id);
    await flush();
    controller.dispose();
    expect(api.signals.at(-1)?.aborted).toBe(true);
    const seen = states.length;
    pending.resolve(detail(target.id, 4, 2));
    await running;
    expect(api.addCalls).toHaveLength(1);
    expect(states).toHaveLength(seen);
    expect(onChanged).not.toHaveBeenCalled();
  });
});
