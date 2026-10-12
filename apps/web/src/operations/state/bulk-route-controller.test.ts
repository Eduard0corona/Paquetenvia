import { describe, expect, it, vi } from "vitest";
import type { BulkRouteApi, CreateRouteBody } from "../api/bulk-route-api";
import { OperationsApiError } from "../api/operations-api";
import { TenantApiError } from "../api/tenant-request";
import type { BulkOrder } from "../contracts/bulk-assignment";
import {
  createRouteConflictMessages,
  createRouteNotFoundMessage,
  routeStopConflictMessages,
  routeStopNotFoundMessage,
  unconfirmedRouteText,
} from "../contracts/bulk-route";
import type { City } from "../contracts/locations";
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

interface CreateCall {
  readonly body: CreateRouteBody;
  readonly key: string;
}

/** UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11: the orders' city and the one other city of the tests. */
const cityA = syntheticUuid(0x900);
const cityB = syntheticUuid(0x901);
const culiacan: City = {
  id: cityA,
  name: "Culiacán",
  state_code: "SIN",
  country_code: "MX",
  timezone: "America/Mazatlan",
};

/** The route createRoute answers for a body: DRAFT at version 1, without stops. */
function createdRoute(body: CreateRouteBody): ManualRoute {
  return {
    id: syntheticUuid(0x8ff),
    status: "DRAFT",
    version: 1,
    driver_id: body.driverId,
    city_id: body.cityId,
    service_area_id: null,
    scheduled_for: body.scheduledFor,
    assignment_cost_cents_total: 0,
    stop_count: 0,
  };
}

/**
 * Answers listDraftRoutes per cursor, getRoute and addRouteStop per order, recording every call in
 * order. An addRouteStop without a scripted answer succeeds like the server (the route one version
 * later) and a getRoute without one returns the route at the last version the fake answered.
 * getOrder answers every order in Culiacán, listCities lists it, and a createRoute without a
 * scripted answer creates the route of its body.
 */
class FakeApi implements BulkRouteApi {
  public readonly calls: string[] = [];
  public readonly addCalls: AddCall[] = [];
  public readonly createCalls: CreateCall[] = [];
  public readonly signals: AbortSignal[] = [];
  public lists: Record<string, () => Promise<ManualRoutePage>> = {};
  public addAnswers: Record<string, (() => Promise<ManualRouteDetail>)[]> = {};
  public routeAnswers: (() => Promise<ManualRouteDetail>)[] = [];
  public orderAnswers: Record<string, () => Promise<{ readonly id: string; readonly city_id: string }>> = {};
  public cityAnswer: () => Promise<readonly City[]> = () => Promise.resolve([culiacan]);
  public createAnswers: ((body: CreateRouteBody) => Promise<ManualRoute>)[] = [];
  private serverVersion = target.version;

  public getOrder(orderId: string, signal?: AbortSignal): Promise<{ readonly id: string; readonly city_id: string }> {
    this.calls.push(`order ${orderId}`);
    if (signal !== undefined) this.signals.push(signal);
    const answer = this.orderAnswers[orderId];
    return answer === undefined ? Promise.resolve({ id: orderId, city_id: cityA }) : answer();
  }

  public listCities(signal?: AbortSignal): Promise<readonly City[]> {
    this.calls.push("cities");
    if (signal !== undefined) this.signals.push(signal);
    return this.cityAnswer();
  }

  public createRoute(body: CreateRouteBody, key: string, signal?: AbortSignal): Promise<ManualRoute> {
    this.calls.push("create");
    this.createCalls.push({ body, key });
    if (signal !== undefined) this.signals.push(signal);
    const answer = this.createAnswers.shift();
    return answer === undefined ? Promise.resolve(createdRoute(body)) : answer(body);
  }

  public listDraftRoutes(driverId: string, cursor: string | null, signal?: AbortSignal): Promise<ManualRoutePage> {
    this.calls.push(`list ${driverId}${cursor === null ? "" : `@${cursor}`}`);
    if (signal !== undefined) this.signals.push(signal);
    const answer = this.lists[cursor ?? ""];
    return answer === undefined ? Promise.resolve(page([target, otherRoute])) : answer();
  }

  public getRoute(routeId: string, signal?: AbortSignal): Promise<ManualRouteDetail> {
    this.calls.push(`get ${routeId}`);
    if (signal !== undefined) this.signals.push(signal);
    const answer = this.routeAnswers.shift();
    if (answer !== undefined) return answer();
    return Promise.resolve(detail(routeId, this.serverVersion, target.stop_count + this.serverVersion - target.version));
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
    this.serverVersion = version;
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

function setup(
  api = new FakeApi(),
  orders: readonly BulkOrder[] = [first, second, third],
  clock: { now: Date } = { now: new Date("2026-10-12T18:00:00Z") },
) {
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
    () => clock.now,
  );
  return { controller, api, states, onChanged, clock };
}

/** A driver without DRAFT routes: the case "Crear ruta nueva" is offered in. */
function withoutRoutes(api = new FakeApi()): FakeApi {
  api.lists = { "": () => Promise.resolve(page([])) };
  return api;
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
    expect(controller.current.routes).toEqual({ kind: "ready", routes: [target, otherRoute], today: "2026-10-12" });
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
    expect(controller.current.routes).toEqual({ kind: "ready", routes: [target], today: "2026-10-12" });
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
    // The generic CONFLICT may be the route's: it is read (still DRAFT, same version) before the next
    // order. INVALID_REQUEST answers for the order alone, and after the last order nothing is read.
    expect(api.calls.slice(1)).toEqual([
      `add ${first.orderId}`,
      `get ${target.id}`,
      `add ${second.orderId}`,
      `add ${third.orderId}`,
    ]);
    expect(api.addCalls.map((call) => call.expectedVersion)).toEqual([3, 3, 3]);
    expect(controller.current.phase).toBe("done");
    expect(controller.current.message).toBe("No se agregó ninguna orden a la ruta. Revisa el motivo en cada fila.");
  });

  it("stops when a generic conflict hides a route that left DRAFT and sends nothing more", async () => {
    const api = new FakeApi();
    api.addAnswers = { [second.orderId]: [() => Promise.reject(new TenantApiError("conflict", "CONFLICT"))] };
    // Someone planned the route while the batch was sending.
    api.routeAnswers = [() => Promise.resolve({ ...detail(target.id, 4, 2), status: "PLANNED" })];
    const { controller, onChanged } = setup(api);
    await controller.loadRoutes();
    api.lists = { "": () => Promise.resolve(page([otherRoute])) };
    await controller.execute(target.id);

    expect(api.calls.filter((call) => !call.startsWith("list"))).toEqual([
      `add ${first.orderId}`,
      `add ${second.orderId}`,
      `get ${target.id}`,
    ]);
    expect(controller.current.results).toEqual({
      [first.orderId]: { kind: "added" },
      [second.orderId]: { kind: "failed", message: routeStopConflictMessages.CONFLICT, retryable: false },
      [third.orderId]: { kind: "not_sent" },
    });
    expect(controller.current.phase).toBe("choosing");
    expect(controller.current.message).toBe(
      `Se agregó 1 de 3 órdenes a la ruta. Revisa el motivo de las demás en su fila. ${bulkRouteChangedMessage}`,
    );
    // The routes were read again and the route is no longer among the DRAFT ones: nothing is sent.
    expect(controller.current.routes).toEqual({ kind: "ready", routes: [otherRoute], today: "2026-10-12" });
    expect(sendableRouteOrders(controller.current)).toEqual([third]);
    await controller.execute(target.id);
    await controller.execute(otherRoute.id);
    expect(api.addCalls).toHaveLength(2);
    expect(onChanged).toHaveBeenCalledTimes(1);
  });

  it("stops after a 404 when the route changed meanwhile, and sends the rest after a new confirmation", async () => {
    const api = new FakeApi();
    api.addAnswers = { [first.orderId]: [() => Promise.reject(new TenantApiError("not_found"))] };
    api.routeAnswers = [() => Promise.resolve(detail(target.id, 9, 5))];
    const { controller } = setup(api);
    await controller.loadRoutes();
    api.lists = { "": () => Promise.resolve(page([{ ...target, version: 9, stop_count: 5 }])) };
    await controller.execute(target.id);
    expect(controller.current.results).toEqual({
      [first.orderId]: { kind: "failed", message: routeStopNotFoundMessage, retryable: false },
      [second.orderId]: { kind: "not_sent" },
      [third.orderId]: { kind: "not_sent" },
    });
    expect(controller.current.phase).toBe("choosing");
    expect(controller.current.route).toMatchObject({ id: target.id, version: 9 });

    // The order the server refused is not sent again; the others go with the version read.
    await controller.execute(target.id);
    expect(api.addCalls.slice(1).map((call) => [call.orderId, call.expectedVersion])).toEqual([
      [second.orderId, 9],
      [third.orderId, 10],
    ]);
    expect(controller.current.phase).toBe("done");
    expect(controller.current.message).toBe("Se agregaron 2 de 3 órdenes a la ruta. Revisa el motivo de las demás en su fila.");
  });

  it("stops when the route cannot be read after a generic conflict", async () => {
    const api = new FakeApi();
    api.addAnswers = { [first.orderId]: [() => Promise.reject(new TenantApiError("conflict", "CONFLICT"))] };
    api.routeAnswers = [() => Promise.reject(new TenantApiError("network"))];
    const { controller } = setup(api);
    await controller.loadRoutes();
    await controller.execute(target.id);
    expect(api.addCalls.map((call) => call.orderId)).toEqual([first.orderId]);
    expect(controller.current.results[second.orderId]).toEqual({ kind: "not_sent" });
    expect(controller.current.results[third.orderId]).toEqual({ kind: "not_sent" });
    expect(controller.current.phase).toBe("choosing");
    expect(controller.current.message).toContain(bulkRouteChangedMessage);
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
    expect(controller.current.routes).toEqual({ kind: "ready", routes: [otherRoute], today: "2026-10-12" });
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

describe("Crear ruta nueva in the step (UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11)", () => {
  it("is offered only when the driver has no DRAFT route, after reading every order one after another", async () => {
    const api = withoutRoutes();
    const firstOrder = deferred<{ readonly id: string; readonly city_id: string }>();
    api.orderAnswers = { [first.orderId]: () => firstOrder.promise };
    const { controller } = setup(api);
    const loading = controller.loadRoutes();
    await flush();
    // The second order is read only after the first one answered; nothing is offered meanwhile.
    expect(api.calls).toEqual([`list ${driver.driverId}`, `order ${first.orderId}`]);
    expect(controller.current.routes).toEqual({ kind: "loading" });
    expect(controller.current.creation).toEqual({ kind: "none" });
    firstOrder.resolve({ id: first.orderId, city_id: cityA });
    await loading;
    expect(api.calls).toEqual([
      `list ${driver.driverId}`,
      `order ${first.orderId}`,
      `order ${second.orderId}`,
      `order ${third.orderId}`,
      "cities",
    ]);
    expect(controller.current.routes).toEqual({ kind: "ready", routes: [], today: "2026-10-12" });
    expect(controller.current.creation).toEqual({ kind: "offered", cityId: cityA, cityName: "Culiacán, SIN" });
    // Nothing is created before the button is pressed.
    expect(api.createCalls).toEqual([]);
  });

  it("is never offered when the driver has a DRAFT route for today: no order is even read", async () => {
    const { controller, api } = setup();
    await controller.loadRoutes();
    // target is dated 2026-10-12, today in Mazatlán at 18:00 UTC.
    expect(controller.current.routes).toEqual({ kind: "ready", routes: [target, otherRoute], today: "2026-10-12" });
    expect(controller.current.creation).toEqual({ kind: "none" });
    expect(api.calls).toEqual([`list ${driver.driverId}`]);
    await controller.createRoute();
    expect(api.createCalls).toEqual([]);
  });

  it("says the orders are in different cities and offers nothing", async () => {
    const api = withoutRoutes();
    api.orderAnswers = { [third.orderId]: () => Promise.resolve({ id: third.orderId, city_id: cityB }) };
    const { controller } = setup(api);
    await controller.loadRoutes();
    expect(controller.current.routes).toEqual({ kind: "ready", routes: [], today: "2026-10-12" });
    expect(controller.current.creation).toEqual({ kind: "mixed_cities" });
    // No city name is needed: the step names no city.
    expect(api.calls).not.toContain("cities");
    await controller.createRoute();
    expect(api.createCalls).toEqual([]);
  });

  it("offers nothing when an order cannot be read and shows the step's unavailable message", async () => {
    for (const failure of [new OperationsApiError("not_found"), new OperationsApiError("network"), new TenantApiError("invalid")]) {
      const api = withoutRoutes();
      api.orderAnswers = { [second.orderId]: () => Promise.reject(failure) };
      const { controller } = setup(api);
      await controller.loadRoutes();
      expect(controller.current.routes).toEqual({ kind: "failed", message: bulkRoutesUnavailableMessage, stepUpHref: null });
      expect(controller.current.creation).toEqual({ kind: "none" });
      // The read stops at the order that failed.
      expect(api.calls).toEqual([`list ${driver.driverId}`, `order ${first.orderId}`, `order ${second.orderId}`]);
      await controller.createRoute();
      expect(api.createCalls).toEqual([]);
    }
  });

  it("reads the orders again only until they answered once, and leaves the city name out when it cannot be read", async () => {
    const api = withoutRoutes();
    let failing = true;
    api.orderAnswers = {
      [first.orderId]: () =>
        failing ? Promise.reject(new OperationsApiError("unavailable")) : Promise.resolve({ id: first.orderId, city_id: cityA }),
    };
    api.cityAnswer = () => Promise.reject(new TenantApiError("network"));
    const { controller } = setup(api);
    await controller.loadRoutes();
    expect(controller.current.routes).toMatchObject({ kind: "failed", message: bulkRoutesUnavailableMessage });
    failing = false;
    await controller.loadRoutes();
    expect(controller.current.creation).toEqual({ kind: "offered", cityId: cityA, cityName: null });
    // An order keeps its city: "Buscar rutas de nuevo" reads the routes (and the missing name) again, not the orders.
    api.cityAnswer = () => Promise.resolve([{ ...culiacan, id: cityB }]);
    const before = api.calls.filter((call) => call.startsWith("order")).length;
    await controller.loadRoutes();
    expect(api.calls.filter((call) => call.startsWith("order"))).toHaveLength(before);
    // A list without the orders' city leaves the name out too.
    expect(controller.current.creation).toEqual({ kind: "offered", cityId: cityA, cityName: null });
  });

  it("creates the route for the driver and the orders' city, with no area and today's date in Mazatlán", async () => {
    const api = withoutRoutes();
    // 05:30 UTC on 12 October is still 11 October in Mazatlán (UTC-7), whatever the device's zone says.
    const { controller } = setup(api, [first, second, third], { now: new Date("2026-10-12T05:30:00Z") });
    await controller.loadRoutes();
    await controller.createRoute();
    expect(api.createCalls).toEqual([
      {
        body: { driverId: driver.driverId, cityId: cityA, scheduledFor: "2026-10-11" },
        key: "create-route-uuid-1",
      },
    ]);
  });

  it("takes the new date once midnight passes in Mazatlán", async () => {
    const api = withoutRoutes();
    const { controller } = setup(api, [first], { now: new Date("2026-10-12T07:00:00Z") });
    await controller.loadRoutes();
    await controller.createRoute();
    expect(api.createCalls[0]?.body.scheduledFor).toBe("2026-10-12");
  });

  it("sends one request however often it is pressed while in flight", async () => {
    const api = withoutRoutes();
    const answer = deferred<ManualRoute>();
    api.createAnswers = [() => answer.promise];
    const { controller } = setup(api);
    await controller.loadRoutes();
    const creating = controller.createRoute();
    expect(controller.current.creation).toEqual({ kind: "creating", cityId: cityA, cityName: "Culiacán, SIN" });
    await controller.createRoute();
    await controller.createRoute();
    // Nor does "Buscar rutas de nuevo" read anything while the route is being created.
    await controller.loadRoutes();
    expect(api.createCalls).toHaveLength(1);
    expect(api.calls.filter((call) => call.startsWith("list"))).toHaveLength(1);
    answer.resolve(createdRoute(api.createCalls[0]!.body));
    await creating;
    await controller.createRoute();
    expect(api.createCalls).toHaveLength(1);
  });

  it("goes straight to the preview with the created route selected, then adds the orders from its version", async () => {
    const api = withoutRoutes();
    const { controller, onChanged } = setup(api);
    await controller.loadRoutes();
    await controller.createRoute();
    const created = createdRoute(api.createCalls[0]!.body);
    expect(controller.current.phase).toBe("choosing");
    expect(controller.current.routes).toEqual({ kind: "ready", routes: [created], today: "2026-10-12" });
    expect(controller.current.createdRouteId).toBe(created.id);
    expect(controller.current.creation).toEqual({ kind: "none" });
    expect(controller.current.creationNotice).toEqual({
      tone: "info",
      text: `Se creó «Ruta del 12 de octubre de 2026» en borrador para ${driver.driverReference}. Revisa la vista previa y confirma para agregar las órdenes.`,
      stepUpHref: null,
    });
    // Nothing was added: the preview and its confirmation come first.
    expect(api.addCalls).toEqual([]);
    expect(onChanged).not.toHaveBeenCalled();

    await controller.execute(created.id);
    expect(api.addCalls).toEqual([
      { routeId: created.id, orderId: first.orderId, expectedVersion: 1, key: "add-route-stop-uuid-2" },
      { routeId: created.id, orderId: second.orderId, expectedVersion: 2, key: "add-route-stop-uuid-3" },
      { routeId: created.id, orderId: third.orderId, expectedVersion: 3, key: "add-route-stop-uuid-4" },
    ]);
    expect(controller.current.phase).toBe("done");
    expect(controller.current.message).toBe("Se agregaron las 3 órdenes a la ruta.");
    expect(controller.current.creationNotice).toBeNull();
    expect(onChanged).toHaveBeenCalledTimes(1);
  });

  it("keeps the button after a refusal, with its es-MX reason and a new key for the next request", async () => {
    const refusals: readonly [TenantApiError, string, string | null][] = [
      [new TenantApiError("conflict", "DRIVER_INELIGIBLE"), createRouteConflictMessages.DRIVER_INELIGIBLE ?? "", null],
      [new TenantApiError("conflict", "INVALID_REQUEST"), createRouteConflictMessages.INVALID_REQUEST ?? "", null],
      [new TenantApiError("not_found"), createRouteNotFoundMessage, null],
      [new TenantApiError("unauthorized"), "No se creó la ruta: Tu sesión no es válida; inicia sesión de nuevo.", null],
      [new TenantApiError("forbidden"), "No se creó la ruta: Tu rol no permite esta acción en la organización activa.", null],
      [
        new TenantApiError("forbidden", "MFA_REQUIRED", true),
        "No se creó la ruta: Esta acción requiere verificar tu identidad (MFA).",
        "/login?mfa=required&return_url=%2Fops%2Finbox%3Fqueue%3Dunassigned",
      ],
    ];
    for (const [error, text, stepUpHref] of refusals) {
      const api = withoutRoutes();
      api.createAnswers = [() => Promise.reject(error)];
      const { controller } = setup(api);
      await controller.loadRoutes();
      await controller.createRoute();
      expect(controller.current.creationNotice, text).toEqual({ tone: "warn", text, stepUpHref });
      expect(controller.current.creation).toEqual({ kind: "offered", cityId: cityA, cityName: "Culiacán, SIN" });
      expect(controller.current.routes).toEqual({ kind: "ready", routes: [], today: "2026-10-12" });
      // A refusal created nothing for sure: the routes are not read again and no success is shown.
      expect(api.calls.filter((call) => call.startsWith("list"))).toHaveLength(1);
      expect(controller.current.createdRouteId).toBeNull();
      // Pressing again is a new request with a new key, and it clears the reason.
      await controller.createRoute();
      expect(api.createCalls.map((call) => call.key)).toEqual(["create-route-uuid-1", "create-route-uuid-2"]);
      expect(controller.current.createdRouteId).toBe(syntheticUuid(0x8ff));
    }
    for (const [code, message] of Object.entries(createRouteConflictMessages)) {
      expect(message, code).not.toContain(code);
      expect(message, code).not.toMatch(/[A-Z]{2,}_[A-Z]|[0-9a-f]{8}-[0-9a-f]{4}/);
    }
  });

  it("after no answer, reads the DRAFT routes again before offering it again, then resends the same key", async () => {
    for (const failure of [new TenantApiError("network"), new TenantApiError("unavailable")]) {
      const api = withoutRoutes();
      api.createAnswers = [() => Promise.reject(failure)];
      const { controller } = setup(api);
      await controller.loadRoutes();
      await controller.createRoute();
      // Never a success by guess: the routes were listed again and none appeared.
      expect(api.calls.filter((call) => call.startsWith("list"))).toHaveLength(2);
      expect(controller.current.createdRouteId).toBeNull();
      expect(controller.current.creation).toEqual({ kind: "offered", cityId: cityA, cityName: "Culiacán, SIN" });
      expect(controller.current.creationNotice).toEqual({
        tone: "warn",
        text: unconfirmedRouteText("none", true, driver.driverReference),
        stepUpHref: null,
      });
      expect(controller.current.creationNotice?.text).toContain("se reenvía la misma solicitud sin duplicarla");
      await controller.createRoute();
      expect(api.createCalls).toHaveLength(2);
      expect(api.createCalls[1]).toEqual(api.createCalls[0]);
      expect(controller.current.createdRouteId).toBe(syntheticUuid(0x8ff));
      expect(controller.current.creationNotice?.tone).toBe("info");
    }
  });

  it("finds a route created anyway after an ambiguous failure and never creates another", async () => {
    const api = withoutRoutes();
    api.createAnswers = [
      (body) => {
        // The server created the route, but the answer never arrived.
        api.lists = { "": () => Promise.resolve(page([createdRoute(body)])) };
        return Promise.reject(new TenantApiError("network"));
      },
    ];
    const { controller } = setup(api);
    await controller.loadRoutes();
    await controller.createRoute();
    const created = createdRoute(api.createCalls[0]!.body);
    expect(controller.current.routes).toEqual({ kind: "ready", routes: [created], today: "2026-10-12" });
    expect(controller.current.creation).toEqual({ kind: "none" });
    expect(controller.current.creationNotice).toEqual({
      tone: "warn",
      text: unconfirmedRouteText("found", true, driver.driverReference),
      stepUpHref: null,
    });
    // The route found is chosen like any DRAFT route; creating again is not possible.
    await controller.createRoute();
    expect(api.createCalls).toHaveLength(1);
    await controller.execute(created.id);
    expect(api.addCalls.map((call) => [call.routeId, call.expectedVersion])).toEqual([
      [created.id, 1],
      [created.id, 2],
      [created.id, 3],
    ]);
  });

  it("treats a generic conflict, an unknown code or an unexpected answer as unconfirmed, with a new key next time", async () => {
    for (const failure of [
      new TenantApiError("conflict", "CONFLICT"),
      new TenantApiError("conflict"),
      new TenantApiError("conflict", "SOMETHING_NEW"),
      new TenantApiError("invalid"),
    ]) {
      const api = withoutRoutes();
      api.createAnswers = [() => Promise.reject(failure)];
      const { controller } = setup(api);
      await controller.loadRoutes();
      await controller.createRoute();
      expect(api.calls.filter((call) => call.startsWith("list"))).toHaveLength(2);
      expect(controller.current.creationNotice?.text).toBe(unconfirmedRouteText("none", false, driver.driverReference));
      expect(controller.current.creationNotice?.text).not.toContain("misma solicitud");
      await controller.createRoute();
      expect(api.createCalls.map((call) => call.key)).toEqual(["create-route-uuid-1", "create-route-uuid-2"]);
    }
  });

  it("offers nothing while the routes cannot be read again after an ambiguous failure", async () => {
    const api = withoutRoutes();
    api.createAnswers = [
      () => {
        api.lists = { "": () => Promise.reject(new TenantApiError("network")) };
        return Promise.reject(new TenantApiError("unavailable"));
      },
    ];
    const { controller } = setup(api);
    await controller.loadRoutes();
    await controller.createRoute();
    expect(controller.current.routes).toEqual({ kind: "failed", message: bulkRoutesUnavailableMessage, stepUpHref: null });
    expect(controller.current.creation).toEqual({ kind: "none" });
    expect(controller.current.creationNotice?.text).toBe(unconfirmedRouteText("unread", true, driver.driverReference));
    await controller.createRoute();
    expect(api.createCalls).toHaveLength(1);
    // Once the routes are read (still none), the button comes back with the same request.
    api.lists = { "": () => Promise.resolve(page([])) };
    await controller.loadRoutes();
    expect(controller.current.creation.kind).toBe("offered");
    expect(controller.current.creationNotice?.text).toBe(unconfirmedRouteText("none", true, driver.driverReference));
    await controller.createRoute();
    expect(api.createCalls[1]).toEqual(api.createCalls[0]);
  });

  it("uses a new key when the retry is another request: the day changed in Mazatlán", async () => {
    const api = withoutRoutes();
    api.createAnswers = [() => Promise.reject(new TenantApiError("network"))];
    const { controller, clock } = setup(api, [first], { now: new Date("2026-10-12T06:59:59Z") });
    await controller.loadRoutes();
    await controller.createRoute();
    clock.now = new Date("2026-10-12T07:00:00Z");
    await controller.createRoute();
    expect(api.createCalls).toEqual([
      { body: { driverId: driver.driverId, cityId: cityA, scheduledFor: "2026-10-11" }, key: "create-route-uuid-1" },
      { body: { driverId: driver.driverId, cityId: cityA, scheduledFor: "2026-10-12" }, key: "create-route-uuid-2" },
    ]);
  });

  it("aborts the order reads and the request in flight when the step closes, ignoring late answers", async () => {
    const reading = withoutRoutes();
    const order = deferred<{ readonly id: string; readonly city_id: string }>();
    reading.orderAnswers = { [first.orderId]: () => order.promise };
    const readRun = setup(reading);
    const loading = readRun.controller.loadRoutes();
    await flush();
    readRun.controller.dispose();
    expect(reading.signals.at(-1)?.aborted).toBe(true);
    const seenWhileReading = readRun.states.length;
    order.resolve({ id: first.orderId, city_id: cityA });
    await loading;
    expect(readRun.states).toHaveLength(seenWhileReading);
    expect(reading.calls.filter((call) => call.startsWith("order"))).toHaveLength(1);

    const creating = withoutRoutes();
    const answer = deferred<ManualRoute>();
    creating.createAnswers = [() => answer.promise];
    const createRun = setup(creating);
    await createRun.controller.loadRoutes();
    const pending = createRun.controller.createRoute();
    createRun.controller.dispose();
    expect(creating.signals.at(-1)?.aborted).toBe(true);
    const seen = createRun.states.length;
    answer.resolve(createdRoute(creating.createCalls[0]!.body));
    await pending;
    expect(createRun.states).toHaveLength(seen);
    expect(createRun.controller.current.createdRouteId).toBeNull();
  });

  it("is not offered once a run fixed a route that later left DRAFT", async () => {
    const api = new FakeApi();
    api.addAnswers = { [first.orderId]: [() => Promise.reject(new TenantApiError("conflict", "VERSION_CONFLICT"))] };
    const { controller } = setup(api);
    await controller.loadRoutes();
    api.lists = { "": () => Promise.resolve(page([])) };
    await controller.execute(target.id);
    expect(controller.current.route).not.toBeNull();
    expect(controller.current.routes).toEqual({ kind: "ready", routes: [], today: "2026-10-12" });
    expect(controller.current.creation).toEqual({ kind: "none" });
    expect(api.calls.some((call) => call.startsWith("order"))).toBe(false);
  });
});

describe("a route per day (UI-PHASE3-ROUTES-DAILY-2026-10-12)", () => {
  const yesterday = route(3, { scheduled_for: "2026-10-11", version: 7, stop_count: 5 });
  const undated = route(4, { scheduled_for: null, stop_count: 0 });
  const tomorrow = route(5, { scheduled_for: "2026-10-13", stop_count: 0 });

  /** A driver whose DRAFT routes are all older, undated or later: none is dated today. */
  function withOlderRoutes(routes: readonly ManualRoute[] = [tomorrow, yesterday, undated], api = new FakeApi()): FakeApi {
    api.lists = { "": () => Promise.resolve(page(routes)) };
    return api;
  }

  it("offers Crear ruta nueva next to older DRAFT routes when none is dated today, and an older one can still be chosen", async () => {
    const api = withOlderRoutes();
    const { controller } = setup(api);
    await controller.loadRoutes();
    // The server's order is kept in the state; the step lists today's first (draftRouteChoices).
    expect(controller.current.routes).toEqual({ kind: "ready", routes: [tomorrow, yesterday, undated], today: "2026-10-12" });
    expect(controller.current.creation).toEqual({ kind: "offered", cityId: cityA, cityName: "Culiacán, SIN" });
    expect(api.calls).toEqual([
      `list ${driver.driverId}`,
      `order ${first.orderId}`,
      `order ${second.orderId}`,
      `order ${third.orderId}`,
      "cities",
    ]);
    // Choosing yesterday's route sends the orders there, from its version; nothing is created.
    await controller.execute(yesterday.id);
    expect(api.createCalls).toEqual([]);
    expect(api.addCalls.map((call) => [call.routeId, call.expectedVersion])).toEqual([
      [yesterday.id, 7],
      [yesterday.id, 8],
      [yesterday.id, 9],
    ]);
    expect(controller.current.creation).toEqual({ kind: "none" });
  });

  it("never counts a route without a date as today's", async () => {
    const api = withOlderRoutes([undated]);
    const { controller } = setup(api);
    await controller.loadRoutes();
    expect(controller.current.creation.kind).toBe("offered");
    await controller.createRoute();
    expect(api.createCalls[0]?.body.scheduledFor).toBe("2026-10-12");
  });

  it("follows the Mazatlán day: at 06:30 UTC on the 12th, a route of the 12th is not today's yet", async () => {
    const dawn = withOlderRoutes([route(6, { scheduled_for: "2026-10-12" })]);
    const early = setup(dawn, [first], { now: new Date("2026-10-12T06:30:00Z") });
    await early.controller.loadRoutes();
    expect(early.controller.current.routes).toMatchObject({ kind: "ready", today: "2026-10-11" });
    expect(early.controller.current.creation.kind).toBe("offered");
    await early.controller.createRoute();
    expect(dawn.createCalls[0]?.body.scheduledFor).toBe("2026-10-11");

    // From 07:00 UTC the 12th is today in Mazatlán and its route is today's route.
    const morning = withOlderRoutes([route(6, { scheduled_for: "2026-10-12" })]);
    const later = setup(morning, [first], { now: new Date("2026-10-12T07:00:00Z") });
    await later.controller.loadRoutes();
    expect(later.controller.current.routes).toMatchObject({ kind: "ready", today: "2026-10-12" });
    expect(later.controller.current.creation).toEqual({ kind: "none" });
    expect(morning.calls.some((call) => call.startsWith("order"))).toBe(false);
  });

  it("lists the created route first, before the older ones, and selects it", async () => {
    const api = withOlderRoutes([yesterday, undated]);
    const { controller } = setup(api);
    await controller.loadRoutes();
    await controller.createRoute();
    const created = createdRoute(api.createCalls[0]!.body);
    expect(controller.current.routes).toEqual({ kind: "ready", routes: [created, yesterday, undated], today: "2026-10-12" });
    expect(controller.current.createdRouteId).toBe(created.id);
    expect(controller.current.creation).toEqual({ kind: "none" });
  });

  it("after an ambiguous failure counts as found only a DRAFT route dated today", async () => {
    // Re-listed with only the older routes: nothing of today, so the button comes back next to them.
    const missing = withOlderRoutes([yesterday]);
    missing.createAnswers = [() => Promise.reject(new TenantApiError("network"))];
    const notFound = setup(missing);
    await notFound.controller.loadRoutes();
    await notFound.controller.createRoute();
    expect(missing.calls.filter((call) => call.startsWith("list"))).toHaveLength(2);
    expect(notFound.controller.current.creationNotice?.text).toBe(unconfirmedRouteText("none", true, driver.driverReference));
    expect(notFound.controller.current.creation.kind).toBe("offered");
    expect(notFound.controller.current.routes).toMatchObject({ kind: "ready", routes: [yesterday] });

    // Re-listed with a route of today (created anyway): found, and no route is offered again.
    const created = withOlderRoutes([yesterday]);
    created.createAnswers = [
      (body) => {
        created.lists = { "": () => Promise.resolve(page([createdRoute(body), yesterday])) };
        return Promise.reject(new TenantApiError("unavailable"));
      },
    ];
    const found = setup(created);
    await found.controller.loadRoutes();
    await found.controller.createRoute();
    expect(found.controller.current.creationNotice).toEqual({
      tone: "warn",
      text: unconfirmedRouteText("found", true, driver.driverReference),
      stepUpHref: null,
    });
    expect(found.controller.current.creation).toEqual({ kind: "none" });
    await found.controller.createRoute();
    expect(created.createCalls).toHaveLength(1);
    // The found route is chosen like any other: nothing is selected for the dispatcher.
    expect(found.controller.current.createdRouteId).toBeNull();

    // A route of another day appearing after the failure is not the one asked for.
    const otherDay = withOlderRoutes([yesterday]);
    otherDay.createAnswers = [
      () => {
        otherDay.lists = { "": () => Promise.resolve(page([tomorrow, yesterday])) };
        return Promise.reject(new TenantApiError("conflict", "CONFLICT"));
      },
    ];
    const other = setup(otherDay);
    await other.controller.loadRoutes();
    await other.controller.createRoute();
    expect(other.controller.current.creationNotice?.text).toBe(unconfirmedRouteText("none", false, driver.driverReference));
    expect(other.controller.current.creation.kind).toBe("offered");
  });

  it("creates nothing when the day changed and the new day already has a route: the routes are read again", async () => {
    const api = withOlderRoutes([tomorrow]);
    const { controller, clock } = setup(api, [first], { now: new Date("2026-10-13T06:59:00Z") });
    await controller.loadRoutes();
    // 23:59 of the 12th in Mazatlán: the 13th's route is not today's.
    expect(controller.current.creation.kind).toBe("offered");
    clock.now = new Date("2026-10-13T07:01:00Z");
    await controller.createRoute();
    expect(api.createCalls).toEqual([]);
    expect(api.calls.filter((call) => call.startsWith("list"))).toHaveLength(2);
    expect(controller.current.routes).toEqual({ kind: "ready", routes: [tomorrow], today: "2026-10-13" });
    expect(controller.current.creation).toEqual({ kind: "none" });
  });

  it("keeps a fixed route's behaviour: a run fixes the route and nothing is offered afterwards", async () => {
    const api = withOlderRoutes([yesterday]);
    api.addAnswers = { [first.orderId]: [() => Promise.reject(new TenantApiError("conflict", "VERSION_CONFLICT"))] };
    const { controller } = setup(api);
    await controller.loadRoutes();
    expect(controller.current.creation.kind).toBe("offered");
    await controller.execute(yesterday.id);
    // The run stopped and the routes were read again: the fixed route stays, no route is offered.
    expect(controller.current.phase).toBe("choosing");
    expect(controller.current.route?.id).toBe(yesterday.id);
    expect(controller.current.creation).toEqual({ kind: "none" });
  });
});
