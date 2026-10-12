import { describe, expect, it } from "vitest";
import type { CreateRouteInput, RoutesApi } from "../api/routes-api";
import { OperationsApiError } from "../api/operations-api";
import { TenantApiError } from "../api/tenant-request";
import {
  createRouteNotFoundMessage,
  routeStopConflictMessages,
  routeStopDefaultConflictMessage,
  routeStopNotFoundMessage,
} from "../contracts/bulk-route";
import type { ManualRoute, ManualRouteDetail, ManualRoutePage } from "../contracts/manual-route";
import { orderNotFoundMessage } from "../contracts/order-transitions";
import { bearerSession, syntheticUuid } from "../contracts/ui-001-screens.fixtures";
import {
  ManualRoutesController,
  maximumGuideReads,
  maximumRouteListPages,
  removeStopConflictMessage,
  reorderConflictMessage,
  reorderStopSetMessage,
  routeChangedMessage,
  routeChangedUnreadMessage,
  routeChangeUnconfirmedMessage,
  routeCreateUnconfirmedMessage,
  routeLoadFailedMessage,
  routeMutationFailure,
  routeNotFoundMessage,
  routeOrderPrefilledMessage,
  routePlannerCreateConflictMessages,
  routePlannerInvalidRequestMessage,
  routeSavedMessage,
  routeSavedStaleMessage,
  routesLoadFailedMessage,
  routesNoSessionMessage,
  routesRealtimeFailedMessage,
  type ManualRoutesState,
  type RouteMutation,
} from "./manual-routes-controller";
import { orderSearchFailedMessage } from "./order-search";

const session = bearerSession();
const driverId = syntheticUuid(0x601);
const cityId = syntheticUuid(0x900);
const uuidText = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/;
const mfaHref = (returnUrl: string) => `/login?mfa=required&return_url=${encodeURIComponent(returnUrl)}`;

function orderOf(counter: number): string {
  return syntheticUuid(0x100 + counter);
}

function guideOf(counter: number): string {
  return `ORD_${String(counter).padStart(22, "0")}`;
}

function summary(counter: number, overrides: Partial<ManualRoute> = {}): ManualRoute {
  return {
    id: syntheticUuid(0x800 + counter),
    status: "DRAFT",
    version: 3,
    driver_id: driverId,
    city_id: cityId,
    service_area_id: null,
    scheduled_for: "2026-10-12",
    assignment_cost_cents_total: 0,
    stop_count: 0,
    ...overrides,
  };
}

function detailOf(route: ManualRoute, orders: readonly number[]): ManualRouteDetail {
  return {
    ...route,
    stop_count: orders.length,
    stops: orders.map((order, index) => ({
      id: syntheticUuid(0xa00 + order),
      order_id: orderOf(order),
      sequence: index + 1,
      stop_type: "DELIVERY" as const,
      status: "PENDING" as const,
    })),
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

/**
 * A server of route details: listRoutes answers every route in one page unless pages are scripted, getRoute answers
 * the stored detail, each change applies itself one version later unless an answer is scripted, every order of the
 * synthetic counters has the guía ORD_000…<counter> and listOrders finds it.
 */
class FakeRoutesApi implements RoutesApi {
  public readonly calls: string[] = [];
  public readonly keys: string[] = [];
  public readonly signals: AbortSignal[] = [];
  public readonly routes = new Map<string, ManualRouteDetail>();
  public pages: Record<string, () => Promise<ManualRoutePage>> | null = null;
  public detailAnswers: (() => Promise<ManualRouteDetail>)[] = [];
  public changeAnswers: (() => Promise<never>)[] = [];
  public createAnswer: ((input: CreateRouteInput) => Promise<ManualRoute>) | null = null;
  public lookupAnswer: ((publicId: string) => Promise<string | null>) | null = null;
  public guideAnswers: Record<string, () => Promise<string>> = {};

  public constructor(routes: readonly ManualRouteDetail[] = []) {
    for (const route of routes) this.routes.set(route.id, route);
  }

  public list(cursor: string | null, signal?: AbortSignal): Promise<ManualRoutePage> {
    this.calls.push(`list${cursor === null ? "" : `@${cursor}`}`);
    this.track(signal);
    if (this.pages !== null) {
      const answer = this.pages[cursor ?? ""];
      if (answer === undefined) return Promise.reject(new Error(`No page ${cursor}`));
      return answer();
    }
    return Promise.resolve({ items: [...this.routes.values()].map(withoutStops), next_cursor: null });
  }

  public get(routeId: string, signal?: AbortSignal): Promise<ManualRouteDetail> {
    this.calls.push(`get ${routeId}`);
    this.track(signal);
    const scripted = this.detailAnswers.shift();
    if (scripted !== undefined) return scripted();
    const route = this.routes.get(routeId);
    return route === undefined ? Promise.reject(new TenantApiError("not_found")) : Promise.resolve(route);
  }

  public create(input: CreateRouteInput, key: string, signal?: AbortSignal): Promise<ManualRoute> {
    this.calls.push("create");
    this.keys.push(key);
    this.track(signal);
    if (this.createAnswer !== null) return this.createAnswer(input);
    const route = detailOf(
      summary(0x7f, {
        version: 1,
        driver_id: input.driverId,
        city_id: input.cityId,
        service_area_id: input.serviceAreaId,
        scheduled_for: input.scheduledFor,
      }),
      [],
    );
    this.routes.set(route.id, route);
    return Promise.resolve(withoutStops(route));
  }

  public addStop(routeId: string, orderId: string, expectedVersion: number, key: string, signal?: AbortSignal): Promise<ManualRouteDetail> {
    this.calls.push(`add ${routeId} ${orderId} v${expectedVersion}`);
    return this.change(routeId, expectedVersion, key, signal, (route) => [
      ...route.stops,
      { id: syntheticUuid(0xb00 + route.stops.length), order_id: orderId, sequence: route.stops.length + 1, stop_type: "DELIVERY", status: "PENDING" },
    ]);
  }

  public removeStop(routeId: string, stopId: string, expectedVersion: number, key: string, signal?: AbortSignal): Promise<ManualRouteDetail> {
    this.calls.push(`remove ${routeId} ${stopId} v${expectedVersion}`);
    return this.change(routeId, expectedVersion, key, signal, (route) =>
      route.stops.filter((stop) => stop.id !== stopId).map((stop, index) => ({ ...stop, sequence: index + 1 })),
    );
  }

  public reorder(routeId: string, stopIds: readonly string[], expectedVersion: number, key: string, signal?: AbortSignal): Promise<ManualRouteDetail> {
    this.calls.push(`reorder ${routeId} v${expectedVersion}`);
    return this.change(routeId, expectedVersion, key, signal, (route) =>
      stopIds.map((stopId, index) => ({ ...route.stops.find((stop) => stop.id === stopId)!, sequence: index + 1 })),
    );
  }

  public findOrderIdByPublicId(publicId: string, signal?: AbortSignal): Promise<string | null> {
    this.calls.push(`lookup ${publicId}`);
    this.track(signal);
    if (this.lookupAnswer !== null) return this.lookupAnswer(publicId);
    const counter = Number(publicId.slice(4));
    return Promise.resolve(Number.isSafeInteger(counter) && counter > 0 ? orderOf(counter) : null);
  }

  public getOrderGuide(orderId: string, signal?: AbortSignal): Promise<string> {
    this.calls.push(`guide ${orderId}`);
    this.track(signal);
    const scripted = this.guideAnswers[orderId];
    if (scripted !== undefined) return scripted();
    return Promise.resolve(guideOf(Number.parseInt(orderId.slice(-4), 16) - 0x100));
  }

  private change(
    routeId: string,
    expectedVersion: number,
    key: string,
    signal: AbortSignal | undefined,
    stops: (route: ManualRouteDetail) => ManualRouteDetail["stops"],
  ): Promise<ManualRouteDetail> {
    this.keys.push(key);
    this.track(signal);
    const scripted = this.changeAnswers.shift();
    if (scripted !== undefined) return scripted();
    const route = this.routes.get(routeId);
    if (route === undefined) return Promise.reject(new TenantApiError("not_found"));
    if (route.version !== expectedVersion) return Promise.reject(new TenantApiError("conflict", "VERSION_CONFLICT"));
    const next = stops(route);
    const updated: ManualRouteDetail = { ...route, version: route.version + 1, stop_count: next.length, stops: next };
    this.routes.set(routeId, updated);
    return Promise.resolve(updated);
  }

  private track(signal: AbortSignal | undefined): void {
    if (signal !== undefined) this.signals.push(signal);
  }
}

function withoutStops(route: ManualRouteDetail): ManualRoute {
  return {
    id: route.id,
    status: route.status,
    version: route.version,
    driver_id: route.driver_id,
    city_id: route.city_id,
    service_area_id: route.service_area_id,
    scheduled_for: route.scheduled_for,
    assignment_cost_cents_total: route.assignment_cost_cents_total,
    stop_count: route.stop_count,
  };
}

function setup(api = new FakeRoutesApi(), query = "", sessionInstalled = true) {
  let counter = 0;
  const states: ManualRoutesState[] = [];
  const controller = new ManualRoutesController({
    readSession: () => (sessionInstalled ? session : null),
    createApi: () => api,
    readQuery: () => new URLSearchParams(query),
    randomUuid: () => `key-${++counter}`,
  });
  controller.subscribe(() => states.push(controller.getSnapshot()));
  return { controller, api, states, state: () => controller.getSnapshot() };
}

const routeA = detailOf(summary(1, { version: 4 }), [1, 2, 3]);
const routeB = detailOf(summary(2, { version: 2, scheduled_for: null }), [4]);

describe("route planner list (UI-PHASE3-ROUTES-NO-IDS-2026-10-12)", () => {
  it("reads every page of listRoutes one after another, in the server's order, without repeats", async () => {
    const api = new FakeRoutesApi();
    const second = deferred<ManualRoutePage>();
    api.pages = {
      "": () => Promise.resolve({ items: [summary(1), summary(2)], next_cursor: "QQ==" }),
      "QQ==": () => second.promise,
      "Qg==": () => Promise.resolve({ items: [summary(4)], next_cursor: null }),
    };
    const { controller, state } = setup(api);
    const starting = controller.start();
    await flush();
    // The second page is asked only after the first answered; the third only after the second.
    expect(api.calls).toEqual(["list", "list@QQ=="]);
    second.resolve({ items: [summary(2), summary(3)], next_cursor: "Qg==" });
    expect(await starting).toBe(session);
    expect(api.calls).toEqual(["list", "list@QQ==", "list@Qg=="]);
    expect(state().routes.map((route) => route.id)).toEqual([1, 2, 3, 4].map((counter) => syntheticUuid(0x800 + counter)));
    expect(state().routesTruncated).toBe(false);
    expect(state().loading).toBe(false);
    expect(state().notice).toBeNull();
  });

  it("stops at the page limit and says some routes are not shown", async () => {
    const api = new FakeRoutesApi();
    api.pages = Object.fromEntries(
      Array.from({ length: maximumRouteListPages + 1 }, (_, page) => [
        page === 0 ? "" : `cA${page}`,
        () => Promise.resolve({ items: [summary(page + 1)], next_cursor: `cA${page + 1}` }),
      ]),
    );
    const { controller, state } = setup(api);
    expect(await controller.start()).toBe(session);
    expect(api.calls).toHaveLength(maximumRouteListPages);
    expect(state().routes).toHaveLength(maximumRouteListPages);
    expect(state().routesTruncated).toBe(true);
  });

  it("fails the read when a cursor repeats, and the realtime hub does not start", async () => {
    const api = new FakeRoutesApi();
    api.pages = {
      "": () => Promise.resolve({ items: [summary(1)], next_cursor: "QQ==" }),
      "QQ==": () => Promise.resolve({ items: [summary(2)], next_cursor: "QQ==" }),
    };
    const { controller, state } = setup(api);
    expect(await controller.start()).toBeNull();
    expect(api.calls).toEqual(["list", "list@QQ=="]);
    expect(state().notice).toEqual({ tone: "warn", text: routesLoadFailedMessage, stepUpHref: null });
  });

  it("says why the list cannot be read: session, role, MFA with the step-up back to Rutas, or anything else", async () => {
    const cases: readonly [unknown, string, string | null][] = [
      [new TenantApiError("unauthorized"), "Tu sesión no es válida; inicia sesión de nuevo.", null],
      [new TenantApiError("forbidden"), "Tu rol no permite esta acción en la organización activa.", null],
      [new TenantApiError("forbidden", "MFA_REQUIRED", true), "Esta acción requiere verificar tu identidad (MFA).", mfaHref("/ops/routes")],
      [new TenantApiError("unavailable"), routesLoadFailedMessage, null],
      [new TenantApiError("invalid"), routesLoadFailedMessage, null],
    ];
    for (const [error, text, stepUpHref] of cases) {
      const api = new FakeRoutesApi();
      api.pages = { "": () => Promise.reject(error) };
      const { controller, state } = setup(api);
      await controller.start();
      expect(state().notice, text).toEqual({ tone: "warn", text, stepUpHref });
    }
  });

  it("opens the route ?routeId= names only when it is listed", async () => {
    const listed = setup(new FakeRoutesApi([routeA, routeB]), `routeId=${routeB.id}`);
    await listed.controller.start();
    expect(listed.state().selected?.id).toBe(routeB.id);
    const unknown = setup(new FakeRoutesApi([routeA]), `routeId=${syntheticUuid(0x8ee)}`);
    await unknown.controller.start();
    expect(unknown.state().selected).toBeNull();
    expect(unknown.api.calls.some((call) => call.startsWith("get"))).toBe(false);
  });

  it("asks to sign in without a session and reads nothing", async () => {
    const { controller, api, state } = setup(new FakeRoutesApi([routeA]), "", false);
    expect(await controller.start()).toBeNull();
    expect(state().hasSession).toBe(false);
    await controller.create({ driverId, cityId, serviceAreaId: null, scheduledFor: null });
    expect(state().notice).toEqual({ tone: "warn", text: routesNoSessionMessage, stepUpHref: null });
    expect(api.calls).toEqual([]);
  });

  it("asks REST again on RouteChanged and resynchronizes from it on reconnection", async () => {
    const api = new FakeRoutesApi([routeA, routeB]);
    const { controller, state } = setup(api, `routeId=${routeA.id}`);
    await controller.start();
    api.calls.length = 0;
    await controller.routeChanged(routeB.id);
    // Another route changed: the list only.
    expect(api.calls).toEqual(["list"]);
    api.routes.set(routeA.id, { ...routeA, version: 9 });
    await controller.routeChanged(routeA.id);
    expect(api.calls).toEqual(["list", "list", `get ${routeA.id}`]);
    expect(state().selected?.version).toBe(9);
    expect(await controller.resynchronize()).toEqual({
      aggregate_versions: { [routeA.id]: 9, [routeB.id]: routeB.version },
    });
    api.pages = { "": () => Promise.reject(new TenantApiError("network")) };
    await controller.routeChanged(routeA.id);
    expect(state().notice).toEqual({ tone: "warn", text: routesRealtimeFailedMessage, stepUpHref: null });
    await expect(controller.resynchronize()).rejects.toBeInstanceOf(TenantApiError);
  });
});

describe("route planner detail (UI-PHASE3-ROUTES-NO-IDS-2026-10-12)", () => {
  it("names every stop by its guía, read one order after another and kept while the screen is open", async () => {
    const api = new FakeRoutesApi([routeA, routeB]);
    const firstGuide = deferred<string>();
    api.guideAnswers = { [orderOf(1)]: () => firstGuide.promise };
    const { controller, state } = setup(api);
    await controller.start();
    await controller.select(routeA.id);
    // Only the first order is read until it answers.
    expect(api.calls.filter((call) => call.startsWith("guide"))).toEqual([`guide ${orderOf(1)}`]);
    expect(state().guides).toEqual({
      [orderOf(1)]: { kind: "loading" },
      [orderOf(2)]: { kind: "loading" },
      [orderOf(3)]: { kind: "loading" },
    });
    firstGuide.resolve(guideOf(1));
    await flush();
    expect(api.calls.filter((call) => call.startsWith("guide"))).toEqual([1, 2, 3].map((order) => `guide ${orderOf(order)}`));
    expect(state().guides).toEqual({
      [orderOf(1)]: { kind: "ready", publicId: guideOf(1) },
      [orderOf(2)]: { kind: "ready", publicId: guideOf(2) },
      [orderOf(3)]: { kind: "ready", publicId: guideOf(3) },
    });
    // Opening another route and coming back reads nothing already known.
    await controller.select(routeB.id);
    await flush();
    await controller.select(routeA.id);
    await flush();
    expect(api.calls.filter((call) => call.startsWith("guide"))).toEqual([1, 2, 3, 4].map((order) => `guide ${orderOf(order)}`));
    expect(state().guides[orderOf(2)]).toEqual({ kind: "ready", publicId: guideOf(2) });
  });

  it("shows Guía no disponible for an order that cannot be read and reads it again with the route", async () => {
    const api = new FakeRoutesApi([routeA]);
    let failing = true;
    api.guideAnswers = {
      [orderOf(2)]: () => (failing ? Promise.reject(new OperationsApiError("not_found")) : Promise.resolve(guideOf(2))),
    };
    const { controller, state } = setup(api, `routeId=${routeA.id}`);
    await controller.start();
    await flush();
    expect(state().guides[orderOf(2)]).toEqual({ kind: "unavailable" });
    expect(state().guides[orderOf(3)]).toEqual({ kind: "ready", publicId: guideOf(3) });
    failing = false;
    await controller.refresh();
    await flush();
    expect(state().guides[orderOf(2)]).toEqual({ kind: "ready", publicId: guideOf(2) });
  });

  it("cancels the read in flight when another route is opened, and ignores its late answer", async () => {
    const api = new FakeRoutesApi([routeA, routeB]);
    const slow = deferred<string>();
    api.guideAnswers = { [orderOf(1)]: () => slow.promise };
    const { controller, state } = setup(api, `routeId=${routeA.id}`);
    await controller.start();
    const signal = api.signals.at(-1);
    await controller.select(routeB.id);
    expect(signal?.aborted).toBe(true);
    slow.resolve(guideOf(1));
    await flush();
    expect(state().selected?.id).toBe(routeB.id);
    expect(state().guides).toEqual({ [orderOf(4)]: { kind: "ready", publicId: guideOf(4) } });
    // Orders 2 and 3 of the other route were never read.
    expect(api.calls.filter((call) => call.startsWith("guide"))).toEqual([`guide ${orderOf(1)}`, `guide ${orderOf(4)}`]);
  });

  it("reads at most the first stops of a route and shows the rest as not available", async () => {
    const orders = Array.from({ length: maximumGuideReads + 2 }, (_, index) => index + 1);
    const big = detailOf(summary(9), orders);
    const api = new FakeRoutesApi([big]);
    const { controller, state } = setup(api, `routeId=${big.id}`);
    await controller.start();
    for (let index = 0; index < maximumGuideReads + 5; index += 1) await flush();
    expect(api.calls.filter((call) => call.startsWith("guide"))).toHaveLength(maximumGuideReads);
    expect(state().guides[orderOf(maximumGuideReads)]).toEqual({ kind: "ready", publicId: guideOf(maximumGuideReads) });
    expect(state().guides[orderOf(maximumGuideReads + 1)]).toEqual({ kind: "unavailable" });
    expect(state().guides[orderOf(maximumGuideReads + 2)]).toEqual({ kind: "unavailable" });
  });

  it("never shows an older version over a newer one, nor another route's late answer", async () => {
    const api = new FakeRoutesApi([routeA, routeB]);
    const { controller, state } = setup(api, `routeId=${routeA.id}`);
    await controller.start();
    const late = deferred<ManualRouteDetail>();
    api.detailAnswers = [() => late.promise];
    const reading = controller.routeChanged(routeA.id);
    api.guideAnswers = {};
    await controller.reorder([routeA.stops[1]!.id, routeA.stops[0]!.id, routeA.stops[2]!.id]);
    expect(state().selected?.version).toBe(5);
    late.resolve(routeA);
    await reading;
    expect(state().selected?.version).toBe(5);
    // Opening B while A's answer is late: A's answer is dropped.
    const lateA = deferred<ManualRouteDetail>();
    api.detailAnswers = [() => lateA.promise];
    const refreshing = controller.refresh();
    await flush();
    await controller.select(routeB.id);
    lateA.resolve({ ...routeA, version: 99 });
    await refreshing;
    expect(state().selected?.id).toBe(routeB.id);
  });

  it("says the route cannot be read or is gone, keeping the route shown", async () => {
    const api = new FakeRoutesApi([routeA, routeB]);
    const { controller, state } = setup(api, `routeId=${routeA.id}`);
    await controller.start();
    api.detailAnswers = [() => Promise.reject(new TenantApiError("not_found"))];
    await controller.select(routeB.id);
    expect(state().notice).toEqual({ tone: "warn", text: routeNotFoundMessage, stepUpHref: null });
    expect(state().selected?.id).toBe(routeA.id);
    api.detailAnswers = [() => Promise.reject(new TenantApiError("network"))];
    await controller.select(routeB.id);
    expect(state().notice).toEqual({ tone: "warn", text: routeLoadFailedMessage, stepUpHref: null });
    api.detailAnswers = [() => Promise.reject(new TenantApiError("forbidden", "MFA_REQUIRED", true))];
    await controller.select(routeB.id);
    expect(state().notice).toEqual({
      tone: "warn",
      text: "Esta acción requiere verificar tu identidad (MFA).",
      stepUpHref: mfaHref(`/ops/routes?routeId=${routeB.id}`),
    });
    // A later RouteChanged of the route shown still updates it.
    api.routes.set(routeA.id, { ...routeA, version: 7 });
    await controller.routeChanged(routeA.id);
    expect(state().selected?.version).toBe(7);
  });
});

describe("Agregar entrega by guía (UI-PHASE3-ROUTES-NO-IDS-2026-10-12)", () => {
  async function opened(api = new FakeRoutesApi([routeA, routeB])) {
    const run = setup(api, `routeId=${routeA.id}`);
    await run.controller.start();
    await flush();
    run.api.calls.length = 0;
    return run;
  }

  it("refuses a value that cannot be a guía before calling, like the top-bar search", async () => {
    const { controller, api, state } = await opened();
    for (const typed of ["", "ORD_123", "PQ-000123", syntheticUuid(0x101), "ord_0000000000000000000001"]) {
      controller.setGuide(typed);
      await controller.addStop();
      expect(state().notice, typed).toEqual({ tone: "warn", text: orderNotFoundMessage, stepUpHref: null });
    }
    expect(api.calls).toEqual([]);
    expect(state().mutating).toBe(false);
  });

  it("says No encontramos esa guía when listOrders has none, and sends nothing", async () => {
    const api = new FakeRoutesApi([routeA]);
    api.lookupAnswer = () => Promise.resolve(null);
    const { controller, state } = await opened(api);
    controller.setGuide(guideOf(7));
    await controller.addStop();
    expect(api.calls).toEqual([`lookup ${guideOf(7)}`]);
    expect(state().notice).toEqual({ tone: "warn", text: orderNotFoundMessage, stepUpHref: null });
    expect(state().guide).toBe(guideOf(7));
  });

  it("fails closed on more than one order or another guía, and on any failed search", async () => {
    for (const failure of [new TenantApiError("invalid"), new TenantApiError("network"), new TenantApiError("unavailable")]) {
      const api = new FakeRoutesApi([routeA]);
      api.lookupAnswer = () => Promise.reject(failure);
      const { controller, state } = await opened(api);
      controller.setGuide(` ${guideOf(7)} `);
      await controller.addStop();
      expect(api.calls).toEqual([`lookup ${guideOf(7)}`]);
      expect(state().notice).toEqual({ tone: "warn", text: orderSearchFailedMessage, stepUpHref: null });
    }
    const api = new FakeRoutesApi([routeA]);
    api.lookupAnswer = () => Promise.reject(new TenantApiError("forbidden", "MFA_REQUIRED", true));
    const { controller, state } = await opened(api);
    controller.setGuide(guideOf(7));
    await controller.addStop();
    expect(state().notice).toEqual({
      tone: "warn",
      text: "Esta acción requiere verificar tu identidad (MFA).",
      stepUpHref: mfaHref(`/ops/routes?routeId=${routeA.id}`),
    });
  });

  it("adds the order found with the route's version and a fresh key, then clears the field and reads the list", async () => {
    const { controller, api, state } = await opened();
    controller.setGuide(` ${guideOf(7)}`);
    await controller.addStop();
    expect(api.calls).toEqual([`lookup ${guideOf(7)}`, `add ${routeA.id} ${orderOf(7)} v4`, "list"]);
    expect(api.keys).toEqual(["key-1"]);
    expect(state().selected?.version).toBe(5);
    expect(state().guide).toBe("");
    expect(state().notice).toEqual({ tone: "info", text: routeSavedMessage, stepUpHref: null });
    // The guía just typed names the new stop without reading the order again.
    await flush();
    expect(state().guides[orderOf(7)]).toEqual({ kind: "ready", publicId: guideOf(7) });
    expect(api.calls.filter((call) => call === `guide ${orderOf(7)}`)).toEqual([]);
    // Every change is a new request with a new key, as before.
    controller.setGuide(guideOf(8));
    await controller.addStop();
    expect(api.keys).toEqual(["key-1", "key-2"]);
  });

  it("sends nothing when another route was opened while the guía was being searched", async () => {
    const api = new FakeRoutesApi([routeA, routeB]);
    const lookup = deferred<string | null>();
    api.lookupAnswer = () => lookup.promise;
    const { controller, state } = await opened(api);
    controller.setGuide(guideOf(7));
    const adding = controller.addStop();
    await controller.select(routeB.id);
    lookup.resolve(orderOf(7));
    await adding;
    expect(api.calls.some((call) => call.startsWith("add"))).toBe(false);
    expect(state().mutating).toBe(false);
  });

  it("waits for one change at a time", async () => {
    const api = new FakeRoutesApi([routeA]);
    const lookup = deferred<string | null>();
    api.lookupAnswer = () => lookup.promise;
    const { controller, state } = await opened(api);
    controller.setGuide(guideOf(7));
    const adding = controller.addStop();
    expect(state().mutating).toBe(true);
    await controller.addStop();
    await controller.removeStop(routeA.stops[0]!.id);
    expect(api.calls).toEqual([`lookup ${guideOf(7)}`]);
    lookup.resolve(orderOf(7));
    await adding;
    expect(state().mutating).toBe(false);
  });

  it("prefills the guía of ?orderId= read with getOrder, and leaves the field empty when it cannot be read", async () => {
    const prefilled = setup(new FakeRoutesApi([routeA]), `orderId=${orderOf(9)}`);
    await prefilled.controller.start();
    expect(prefilled.api.calls).toEqual(["list", `guide ${orderOf(9)}`]);
    expect(prefilled.state().guide).toBe(guideOf(9));
    expect(prefilled.state().notice).toEqual({ tone: "info", text: routeOrderPrefilledMessage, stepUpHref: null });

    const unreadable = new FakeRoutesApi([routeA]);
    unreadable.guideAnswers = { [orderOf(9)]: () => Promise.reject(new OperationsApiError("not_found")) };
    const empty = setup(unreadable, `orderId=${orderOf(9)}`);
    await empty.controller.start();
    expect(empty.state().guide).toBe("");
    expect(empty.state().notice).toBeNull();

    // Something that is not an order id is never read.
    const malformed = setup(new FakeRoutesApi([routeA]), "orderId=not-an-id");
    await malformed.controller.start();
    expect(malformed.api.calls).toEqual(["list"]);
    expect(malformed.state().guide).toBe("");
  });

  it("does not overwrite a guía already typed, and starts from empty after a session change", async () => {
    const api = new FakeRoutesApi([routeA]);
    const slow = deferred<string>();
    api.guideAnswers = { [orderOf(9)]: () => slow.promise };
    const { controller, state } = setup(api, `orderId=${orderOf(9)}`);
    const starting = controller.start();
    await flush();
    controller.setGuide(guideOf(5));
    slow.resolve(guideOf(9));
    await starting;
    expect(state().guide).toBe(guideOf(5));
    api.guideAnswers = { [orderOf(9)]: () => Promise.reject(new OperationsApiError("not_found")) };
    await controller.start();
    expect(state().guide).toBe("");
    expect(state().selected).toBeNull();
    expect(state().guides).toEqual({});
  });
});

describe("Rutas errors by reason (UI-PHASE3-ROUTES-NO-IDS-2026-10-12)", () => {
  const routeReturn = `/ops/routes?routeId=${routeA.id}`;
  const sessionCases: readonly [TenantApiError, string, string | null][] = [
    [new TenantApiError("unauthorized"), "Tu sesión no es válida; inicia sesión de nuevo.", null],
    [new TenantApiError("forbidden"), "Tu rol no permite esta acción en la organización activa.", null],
    [new TenantApiError("forbidden", "MFA_REQUIRED", true), "Esta acción requiere verificar tu identidad (MFA).", mfaHref(routeReturn)],
  ];

  it("maps every code of every change to its es-MX reason; only VERSION_CONFLICT says the route changed", () => {
    const expected: Record<RouteMutation, readonly [unknown, string, boolean][]> = {
      create: [
        [new TenantApiError("conflict", "DRIVER_INELIGIBLE"), routePlannerCreateConflictMessages.DRIVER_INELIGIBLE!, false],
        [new TenantApiError("conflict", "INVALID_REQUEST"), routePlannerCreateConflictMessages.INVALID_REQUEST!, false],
        [new TenantApiError("not_found"), createRouteNotFoundMessage, false],
        [new TenantApiError("conflict", "CONFLICT"), routeCreateUnconfirmedMessage, false],
        [new TenantApiError("conflict", "VERSION_CONFLICT"), routeCreateUnconfirmedMessage, false],
        [new TenantApiError("conflict"), routeCreateUnconfirmedMessage, false],
        [new TenantApiError("network"), routeCreateUnconfirmedMessage, false],
        [new TenantApiError("unavailable"), routeCreateUnconfirmedMessage, false],
        [new TenantApiError("invalid"), routeCreateUnconfirmedMessage, false],
        [new Error("anything"), routeCreateUnconfirmedMessage, false],
      ],
      add: [
        [new TenantApiError("conflict", "VERSION_CONFLICT"), routeChangedMessage, true],
        [new TenantApiError("conflict", "DRIVER_INELIGIBLE"), routeStopConflictMessages.DRIVER_INELIGIBLE!, false],
        [new TenantApiError("conflict", "CONFLICT"), routeStopConflictMessages.CONFLICT!, false],
        [new TenantApiError("conflict", "INVALID_REQUEST"), routePlannerInvalidRequestMessage, false],
        [new TenantApiError("conflict", "SOMETHING_NEW"), routeStopDefaultConflictMessage, false],
        [new TenantApiError("conflict"), routeStopDefaultConflictMessage, false],
        [new TenantApiError("not_found"), routeStopNotFoundMessage, false],
        [new TenantApiError("network"), routeChangeUnconfirmedMessage, false],
        [new TenantApiError("invalid"), routeChangeUnconfirmedMessage, false],
      ],
      remove: [
        [new TenantApiError("conflict", "VERSION_CONFLICT"), routeChangedMessage, true],
        [new TenantApiError("conflict", "CONFLICT"), removeStopConflictMessage, false],
        [new TenantApiError("conflict", "SOMETHING_NEW"), removeStopConflictMessage, false],
        [new TenantApiError("conflict", "INVALID_REQUEST"), routePlannerInvalidRequestMessage, false],
        [new TenantApiError("not_found"), routeStopNotFoundMessage, false],
        [new TenantApiError("unavailable"), routeChangeUnconfirmedMessage, false],
      ],
      reorder: [
        [new TenantApiError("conflict", "VERSION_CONFLICT"), routeChangedMessage, true],
        [new TenantApiError("conflict", "INVALID_STOP_SET"), reorderStopSetMessage, false],
        [new TenantApiError("conflict", "CONFLICT"), reorderConflictMessage, false],
        [new TenantApiError("conflict", "INVALID_REQUEST"), routePlannerInvalidRequestMessage, false],
        [new TenantApiError("not_found"), routeNotFoundMessage, false],
        [new TenantApiError("network"), routeChangeUnconfirmedMessage, false],
      ],
    };
    for (const [mutation, cases] of Object.entries(expected) as [RouteMutation, readonly [unknown, string, boolean][]][]) {
      for (const [error, text, reload] of cases)
        expect(routeMutationFailure(error, mutation, routeReturn), `${mutation} ${String(error)}`).toEqual({
          text,
          stepUpHref: null,
          reload,
        });
      for (const [error, text, stepUpHref] of sessionCases)
        expect(routeMutationFailure(error, mutation, routeReturn)).toEqual({ text, stepUpHref, reload: false });
    }
    // Only one message says the route changed, and no message carries a code, an id or a status number.
    const texts = Object.values(expected).flat().map(([, text]) => text);
    expect(texts.filter((text) => text.includes("cambió mientras la editabas"))).toEqual(
      Object.values(expected).flat().filter(([, , reload]) => reload).map(() => routeChangedMessage),
    );
    for (const text of texts) {
      expect(text).not.toMatch(/[A-Z]{2,}_[A-Z]|\b(409|404|403|503)\b/);
      expect(text).not.toMatch(uuidText);
      // The inbox's words never reach Rutas.
      expect(text).not.toMatch(/bandeja|estas órdenes/);
    }
  });

  it("reads the route again only after VERSION_CONFLICT, and says so", async () => {
    const api = new FakeRoutesApi([routeA]);
    const { controller, state } = setup(api, `routeId=${routeA.id}`);
    await controller.start();
    await flush();
    // Someone else changed the route on the server.
    api.routes.set(routeA.id, { ...routeA, version: 6 });
    api.calls.length = 0;
    await controller.removeStop(routeA.stops[0]!.id);
    expect(api.calls).toEqual([`remove ${routeA.id} ${routeA.stops[0]!.id} v4`, `get ${routeA.id}`]);
    expect(state().selected?.version).toBe(6);
    expect(state().notice).toEqual({ tone: "warn", text: routeChangedMessage, stepUpHref: null });
    // Other codes say their reason and read nothing.
    api.calls.length = 0;
    api.changeAnswers = [() => Promise.reject(new TenantApiError("conflict", "INVALID_STOP_SET"))];
    await controller.reorder(state().selected!.stops.map((stop) => stop.id).reverse());
    expect(api.calls).toEqual([`reorder ${routeA.id} v6`]);
    expect(state().notice).toEqual({ tone: "warn", text: reorderStopSetMessage, stepUpHref: null });
    // A VERSION_CONFLICT whose route cannot be read again says that too.
    api.changeAnswers = [() => Promise.reject(new TenantApiError("conflict", "VERSION_CONFLICT"))];
    api.detailAnswers = [() => Promise.reject(new TenantApiError("network"))];
    await controller.reorder(state().selected!.stops.map((stop) => stop.id));
    expect(state().notice).toEqual({ tone: "warn", text: routeChangedUnreadMessage, stepUpHref: null });
  });

  it("offers Verificar identidad back to Rutas and the route shown when only MFA is missing", async () => {
    const api = new FakeRoutesApi([routeA]);
    const { controller, state } = setup(api, `routeId=${routeA.id}`);
    await controller.start();
    for (const action of [
      () => controller.create({ driverId, cityId, serviceAreaId: null, scheduledFor: "2026-10-12" }),
      () => controller.removeStop(routeA.stops[0]!.id),
      () => controller.reorder(routeA.stops.map((stop) => stop.id)),
    ]) {
      api.changeAnswers = [() => Promise.reject(new TenantApiError("forbidden", "MFA_REQUIRED", true))];
      api.createAnswer = () => Promise.reject(new TenantApiError("forbidden", "MFA_REQUIRED", true));
      await action();
      expect(state().notice).toEqual({
        tone: "warn",
        text: "Esta acción requiere verificar tu identidad (MFA).",
        stepUpHref: mfaHref(`/ops/routes?routeId=${routeA.id}`),
      });
    }
    // Without a route shown it returns to Rutas.
    const empty = setup(new FakeRoutesApi());
    await empty.controller.start();
    empty.api.createAnswer = () => Promise.reject(new TenantApiError("forbidden", "MFA_REQUIRED", true));
    await empty.controller.create({ driverId, cityId, serviceAreaId: null, scheduledFor: null });
    expect(empty.state().notice?.stepUpHref).toBe(mfaHref("/ops/routes"));
  });

  it("creates a route with a fresh key, opens it and never says it was saved when the answer was not valid", async () => {
    const api = new FakeRoutesApi([routeA]);
    const { controller, state } = setup(api);
    await controller.start();
    await controller.create({ driverId, cityId, serviceAreaId: null, scheduledFor: "2026-10-12" });
    expect(api.keys).toEqual(["key-1"]);
    expect(state().selected?.id).toBe(syntheticUuid(0x87f));
    expect(state().notice).toEqual({ tone: "info", text: routeSavedMessage, stepUpHref: null });
    api.createAnswer = () => Promise.reject(new TenantApiError("network"));
    await controller.create({ driverId, cityId, serviceAreaId: null, scheduledFor: "2026-10-12" });
    expect(api.keys).toEqual(["key-1", "key-2"]);
    expect(state().notice).toEqual({ tone: "warn", text: routeCreateUnconfirmedMessage, stepUpHref: null });
  });

  it("keeps Cambio guardado when the list cannot be read after a change", async () => {
    const api = new FakeRoutesApi([routeA]);
    const { controller, state } = setup(api, `routeId=${routeA.id}`);
    await controller.start();
    api.pages = { "": () => Promise.reject(new TenantApiError("unavailable")) };
    await controller.reorder([routeA.stops[2]!.id, routeA.stops[1]!.id, routeA.stops[0]!.id]);
    expect(state().selected?.version).toBe(5);
    expect(state().notice).toEqual({ tone: "warn", text: routeSavedStaleMessage, stepUpHref: null });
  });
});
