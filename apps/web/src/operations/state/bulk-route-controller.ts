import type { BulkRouteApi, CreateRouteBody } from "../api/bulk-route-api";
import { TenantApiError } from "../api/tenant-request";
import type { BulkOrder } from "../contracts/bulk-assignment";
import {
  bulkRouteRunSummary,
  createdRouteText,
  createRouteConflictMessages,
  createRouteNotFoundMessage,
  isDraftRouteOf,
  isTodaysRoute,
  routeStopConflictMessages,
  routeStopDefaultConflictMessage,
  routeStopNotFoundMessage,
  todayInMazatlan,
  unconfirmedRouteText,
  type BulkRouteDriver,
} from "../contracts/bulk-route";
import { cityLabel } from "../contracts/locations";
import type { ManualRoute, ManualRouteDetail } from "../contracts/manual-route";
import { PendingSubmissions } from "./pending-submissions";
import { describeFailure } from "./tenant-error-messages";

/**
 * UI-PHASE3-INBOX-BULK-ADD-TO-ROUTE-2026-10-11: state of "Agregar estas órdenes a una ruta…", the
 * step a finished "Asignar seleccionadas…" offers with the orders it assigned and its driver.
 *
 * The driver's DRAFT routes are read with listRoutes (every page, one after another). After the
 * confirmation, addRouteStop runs for one order at a time, each with its own Idempotency-Key kept
 * with the same payload (route and expected_version) until the server answers definitively, so a
 * retry after a network or server failure resends that key. expected_version is the version just
 * read and then the version of each authoritative answer. A VERSION_CONFLICT means someone else
 * changed the route: the run stops, the routes are read again and the remaining orders need a new
 * confirmation. The generic CONFLICT and the uniform 404 also cover a route that left DRAFT or is
 * gone, so after one of them getRoute must show the same DRAFT route at the same version before the
 * next order is sent; otherwise the run stops the same way. The first run fixes the route for the
 * rest of the batch. Each order keeps its own result, a stop is never hidden, and the inbox reads
 * REST again after every run. Nothing is stored in the browser and nothing is logged.
 *
 * UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11 ("Sí, en el paso (Recommended)"): when the driver has
 * no DRAFT route for today (and no run fixed one), every order of the batch is read with getOrder,
 * one after another, for its city_id. One shared city offers "Crear ruta nueva": createRoute with the
 * driver, that city, no service area and today's date in Mazatlán, under an Idempotency-Key kept
 * only to resend that same request after a network or server failure. The created route is selected
 * and the step goes on to its usual preview, confirmation and addRouteStop run. Orders in different
 * cities get no button, and an order that cannot be read fails the read like the routes. A success
 * is never invented: after a failure that cannot say whether the route exists, the driver's DRAFT
 * routes are read again before the button comes back.
 *
 * UI-PHASE3-ROUTES-DAILY-2026-10-12 ("Ruta por día (Recommended)"): routes never leave DRAFT (AI-05
 * has no operation for it), so "today" decides. "Crear ruta nueva" is offered when the driver has no
 * DRAFT route whose scheduled_for is today in America/Mazatlan (from the injectable clock; a route
 * without a date is not today's), also next to older DRAFT routes, which can still be chosen. The
 * read keeps the date it was made for, so the step lists today's routes first. After an ambiguous
 * failure only a DRAFT route dated today counts as found.
 */
export type BulkRoutesLoad =
  | { readonly kind: "loading" }
  /** `today`: the Mazatlán date (`YYYY-MM-DD`) the routes were read for; today's routes are listed first. */
  | { readonly kind: "ready"; readonly routes: readonly ManualRoute[]; readonly today: string }
  | { readonly kind: "failed"; readonly message: string; readonly stepUpHref: string | null };

/** "Crear ruta nueva", where the step says the driver has no DRAFT route for today. */
export type BulkRouteCreation =
  /** Not offered: the routes are loading or failed, the driver has a DRAFT route for today, or a run fixed the route. */
  | { readonly kind: "none" }
  /** The orders are in different cities: a route covers one city, so routes are created in Rutas. */
  | { readonly kind: "mixed_cities" }
  /** A DRAFT route of the driver in the orders' city, for today; `cityName` is null when listCities failed. */
  | { readonly kind: "offered"; readonly cityId: string; readonly cityName: string | null }
  /** createRoute is in flight: the button waits and nothing else reads or sends. */
  | { readonly kind: "creating"; readonly cityId: string; readonly cityName: string | null };

/** The outcome of the last "Crear ruta nueva": created, refused or not confirmed. */
export interface BulkRouteNotice {
  readonly tone: "info" | "warn";
  readonly text: string;
  /** `/login?mfa=required&return_url=…` when only MFA was missing. */
  readonly stepUpHref: string | null;
}

export type BulkRouteRowResult =
  /** Part of the run, waiting for its turn. */
  | { readonly kind: "pending" }
  | { readonly kind: "sending" }
  | { readonly kind: "added" }
  /** VERSION_CONFLICT: not added; it goes again with the next confirmation. */
  | { readonly kind: "route_changed" }
  | { readonly kind: "failed"; readonly message: string; readonly retryable: boolean }
  /** The run stopped before this order was sent. */
  | { readonly kind: "not_sent" };

export interface BulkRouteState {
  /** "choosing": route and preview, before the first run or after a version conflict. */
  readonly phase: "choosing" | "running" | "done";
  readonly orders: readonly BulkOrder[];
  readonly routes: BulkRoutesLoad;
  /** The route of the runs: fixed by the first confirmation, refreshed when the routes are read again. */
  readonly route: ManualRoute | null;
  readonly results: Readonly<Record<string, BulkRouteRowResult>>;
  /** The outcome of the last run, or why it stopped. */
  readonly message: string | null;
  /** `/login?mfa=required&return_url=…` when the run stopped only for MFA. */
  readonly stepUpHref: string | null;
  /** Some orders can be sent again: the run stopped on a network or server failure. */
  readonly canRetry: boolean;
  /** UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11: "Crear ruta nueva". */
  readonly creation: BulkRouteCreation;
  readonly creationNotice: BulkRouteNotice | null;
  /** The route "Crear ruta nueva" created; the step selects it for the preview. */
  readonly createdRouteId: string | null;
}

export function initialBulkRouteState(orders: readonly BulkOrder[]): BulkRouteState {
  return {
    phase: "choosing",
    orders,
    routes: { kind: "loading" },
    route: null,
    results: {},
    message: null,
    stepUpHref: null,
    canRetry: false,
    creation: { kind: "none" },
    creationNotice: null,
    createdRouteId: null,
  };
}

/** The orders the next confirmation sends: never sent, left unsent by a stop or stopped by a route change. */
export function sendableRouteOrders(state: BulkRouteState): readonly BulkOrder[] {
  return state.orders.filter((order) => {
    const result = state.results[order.orderId];
    return result === undefined || result.kind === "not_sent" || result.kind === "route_changed";
  });
}

/** listRoutes pages (the server's page size) read before giving up on listing the driver's routes. */
export const maximumRoutePages = 5;

export const bulkRoutesUnavailableMessage = "No fue posible cargar las rutas en borrador del repartidor. Intenta de nuevo.";
export const bulkRoutesTooManyMessage =
  "No fue posible revisar todas las rutas en borrador del repartidor; agrega las órdenes desde Rutas.";
export const bulkRouteChangedMessage =
  "La ruta cambió mientras se agregaban las órdenes y el envío se detuvo. Revisa la ruta actualizada y confirma de nuevo para agregar las pendientes.";
export const bulkRouteStoppedRetryMessage =
  "El envío se detuvo porque el servidor no respondió. Puedes reintentar las órdenes pendientes: se reenvían sin duplicarse.";
export const bulkRouteUnexpectedAnswerMessage =
  "La respuesta del servidor no fue la esperada; revisa la ruta en Rutas antes de volver a intentarlo.";

class RouteListIncompleteError extends Error {
  public constructor() {
    super("The driver's draft routes could not be read completely.");
    this.name = "RouteListIncompleteError";
  }
}

/** An order of the batch could not be read: whether a route can be created stays unknown. */
class OrderCityUnreadableError extends Error {
  public constructor() {
    super("An order of the batch could not be read.");
    this.name = "OrderCityUnreadableError";
  }
}

/** The single key scope of "Crear ruta nueva": one route request per step. */
const createRouteScope = "create-route";

const none: BulkRouteCreation = { kind: "none" };

/** What the orders of the batch say about their city; read once, since an order keeps its city. */
type OrdersCity = { readonly kind: "same"; readonly cityId: string } | { readonly kind: "mixed" };

type CreateFailure =
  /** createRoute refused the route for sure: nothing was created. */
  | { readonly kind: "refused"; readonly message: string; readonly stepUpHref: string | null }
  /**
   * The route may exist anyway: the DRAFT routes are read again before creation is offered again.
   * `sameRequest`: no answer or a server failure, so the same request keeps its key to be resent.
   */
  | { readonly kind: "unconfirmed"; readonly sameRequest: boolean };

interface SendFailure {
  readonly result: BulkRouteRowResult;
  /** Worth resending with the same key and payload. */
  readonly retryable: boolean;
  /** No later order is sent: the failure is not about this order alone. */
  readonly stop: boolean;
  /** VERSION_CONFLICT, or a route found changed: the route must be read and confirmed again. */
  readonly routeChanged: boolean;
  /**
   * A generic CONFLICT (or an unknown code) or the uniform 404: the server uses them for this order
   * and also for a route that left DRAFT or is gone, so the route is read before the next order.
   */
  readonly checkRoute: boolean;
  readonly message: string;
  readonly stepUpHref: string | null;
}

/** 409 codes that answer for the order alone (AI-05 RoutingConflict); any other one may be the route's. */
const orderConflictCodes: ReadonlySet<string> = new Set(["DRIVER_INELIGIBLE", "INVALID_REQUEST"]);

/** The run stops because the route changed or cannot be confirmed unchanged: read it again, confirm again. */
const routeChangedStop: SendFailure = {
  result: { kind: "route_changed" },
  retryable: false,
  stop: true,
  routeChanged: true,
  checkRoute: false,
  message: bulkRouteChangedMessage,
  stepUpHref: null,
};

export class BulkRouteController {
  private state: BulkRouteState;
  private disposed = false;
  private readonly abort = new AbortController();
  private readonly keys: PendingSubmissions;
  private readonly createKeys: PendingSubmissions;
  private routesGeneration = 0;
  private ordersCity: OrdersCity | null = null;
  private cityName: string | null = null;
  /** The last "Crear ruta nueva" could not say whether the route exists; `sameRequest` keeps its key. */
  private unconfirmed: { readonly sameRequest: boolean } | null = null;

  public constructor(
    private readonly api: BulkRouteApi,
    private readonly driver: BulkRouteDriver,
    orders: readonly BulkOrder[],
    private readonly onChange: (state: BulkRouteState) => void,
    /** The inbox reads the queue and the counts again. */
    private readonly onChanged: () => void,
    /** Where the MFA step-up returns (the inbox view). */
    private readonly returnUrl: string,
    randomUuid: () => string = () => crypto.randomUUID(),
    /** The clock "Crear ruta nueva" takes today's Mazatlán date from. */
    private readonly now: () => Date = () => new Date(),
  ) {
    this.state = initialBulkRouteState(orders);
    this.keys = new PendingSubmissions(() => `add-route-stop-${randomUuid()}`);
    this.createKeys = new PendingSubmissions(() => `create-route-${randomUuid()}`);
  }

  public get current(): BulkRouteState {
    return this.state;
  }

  /**
   * Reads every DRAFT route of the driver, one page after another. When none is dated today in
   * Mazatlán and no run fixed a route, the orders' city decides whether "Crear ruta nueva" is offered.
   */
  public async loadRoutes(): Promise<void> {
    if (this.disposed || this.state.phase !== "choosing" || this.state.creation.kind === "creating") return;
    const generation = ++this.routesGeneration;
    this.set({ ...this.state, routes: { kind: "loading" }, creation: none });
    try {
      const routes = await this.readAllRoutes();
      if (this.disposed || generation !== this.routesGeneration) return;
      const today = todayInMazatlan(this.now());
      const fixed = this.state.route;
      // The fixed route takes the version and stops just read; a route no longer listed stays as it was.
      const route = fixed === null ? null : (routes.find((item) => item.id === fixed.id) ?? fixed);
      const hasToday = routes.some((item) => isTodaysRoute(item, today));
      const creation = fixed === null && !hasToday ? await this.creationOffer() : none;
      if (this.disposed || generation !== this.routesGeneration) return;
      this.set({
        ...this.state,
        routes: { kind: "ready", routes, today },
        route,
        creation,
        // After an ambiguous failure only a DRAFT route dated today is the route that may have been created.
        creationNotice: this.unconfirmedNotice(hasToday ? "found" : "none") ?? this.state.creationNotice,
      });
    } catch (error: unknown) {
      if (this.disposed || generation !== this.routesGeneration) return;
      this.set({
        ...this.state,
        routes: { kind: "failed", ...routesFailure(error, this.returnUrl) },
        creation: none,
        creationNotice: this.unconfirmedNotice("unread") ?? this.state.creationNotice,
      });
    }
  }

  /**
   * "Crear ruta nueva": createRoute with the batch's driver, the orders' city, no service area and
   * today's date in Mazatlán. The created route is selected and the step stays on its preview; a
   * refusal keeps the button with its es-MX reason; a failure that cannot say whether the route
   * exists reads the DRAFT routes again before the button comes back.
   */
  public async createRoute(): Promise<void> {
    const creation = this.state.creation;
    if (this.disposed || this.state.phase !== "choosing" || this.state.route !== null) return;
    if (creation.kind !== "offered" || this.state.routes.kind !== "ready") return;
    const scheduledFor = todayInMazatlan(this.now());
    // The rule holds when it is pressed too: if the day changed in Mazatlán since the routes were read and the new
    // day already has a listed DRAFT route, nothing is created and the routes are read again for that day.
    if (this.state.routes.routes.some((item) => isTodaysRoute(item, scheduledFor))) {
      await this.loadRoutes();
      return;
    }
    const body: CreateRouteBody = {
      driverId: this.driver.driverId,
      cityId: creation.cityId,
      scheduledFor,
    };
    // The same request keeps its key until a definitive answer; another day or city is another request.
    const prepared = this.createKeys.prepare(createRouteScope, `${body.driverId}|${body.cityId}|${body.scheduledFor}`, () => body);
    this.unconfirmed = null;
    this.set({ ...this.state, creation: { ...creation, kind: "creating" }, creationNotice: null });
    let route: ManualRoute;
    try {
      route = await this.api.createRoute(prepared.payload, prepared.key, this.abort.signal);
    } catch (error: unknown) {
      if (this.disposed) return;
      const failure = createFailure(error, this.returnUrl);
      if (failure.kind === "refused") {
        this.createKeys.settle(createRouteScope);
        this.set({
          ...this.state,
          creation: { ...creation, kind: "offered" },
          creationNotice: { tone: "warn", text: failure.message, stepUpHref: failure.stepUpHref },
        });
        return;
      }
      // Only no answer or a server failure keeps the key, to resend this same request.
      if (!failure.sameRequest) this.createKeys.settle(createRouteScope);
      // Never a success by guess: a route created anyway must be found, not created twice.
      this.unconfirmed = { sameRequest: failure.sameRequest };
      this.set({ ...this.state, creation: { ...creation, kind: "offered" } });
      await this.loadRoutes();
      return;
    }
    if (this.disposed) return;
    this.createKeys.settle(createRouteScope);
    // Straight to the preview: the created route is the driver's newest DRAFT route and today's only one, listed
    // first (the server lists newest first) before the older ones, and it is selected.
    const older = this.state.routes.kind === "ready" ? this.state.routes.routes : [];
    this.set({
      ...this.state,
      routes: { kind: "ready", routes: [route, ...older.filter((item) => item.id !== route.id)], today: body.scheduledFor },
      creation: none,
      createdRouteId: route.id,
      creationNotice: { tone: "info", text: createdRouteText(route, this.driver.driverReference), stepUpHref: null },
    });
  }

  /**
   * Sends the confirmed run: every sendable order to the route, one after another. Only a DRAFT
   * route of the driver that was just listed is accepted, and after the first run only that route.
   */
  public async execute(routeId: string): Promise<void> {
    if (this.disposed || this.state.phase !== "choosing" || this.state.routes.kind !== "ready") return;
    if (this.state.route !== null && this.state.route.id !== routeId) return;
    const route = this.state.routes.routes.find((item) => item.id === routeId);
    if (route === undefined || !isDraftRouteOf(route, this.driver.driverId)) return;
    const orderIds = sendableRouteOrders(this.state).map((order) => order.orderId);
    if (orderIds.length === 0) return;
    const results = { ...this.state.results };
    for (const orderId of orderIds) results[orderId] = { kind: "pending" };
    // A run fixes the route: "Crear ruta nueva" and its notices are over.
    this.unconfirmed = null;
    this.set({
      ...this.state,
      phase: "running",
      route,
      results,
      message: null,
      stepUpHref: null,
      canRetry: false,
      creation: none,
      creationNotice: null,
    });
    await this.send(orderIds);
  }

  /** Sends again, with the same keys and payloads, the orders a stopped run left without an answer. */
  public async retry(): Promise<void> {
    if (this.disposed || this.state.phase !== "done" || !this.state.canRetry) return;
    const orderIds = this.state.orders
      .map((order) => order.orderId)
      .filter((orderId) => isResendable(this.state.results[orderId]));
    if (orderIds.length === 0) return;
    const results = { ...this.state.results };
    for (const orderId of orderIds) results[orderId] = { kind: "pending" };
    this.set({ ...this.state, phase: "running", results, message: null, stepUpHref: null, canRetry: false });
    await this.send(orderIds);
  }

  public dispose(): void {
    this.disposed = true;
    this.abort.abort();
    this.keys.clear();
    this.createKeys.clear();
  }

  /**
   * Whether "Crear ruta nueva" is offered once the driver has no DRAFT route for today: every order of
   * the batch is read with getOrder, one after another (cancelled with the step), for its city_id. One
   * shared city offers it, named by listCities when that read works; different cities never do. An
   * order that cannot be read fails the whole read, so the button is never offered on a guess.
   */
  private async creationOffer(): Promise<BulkRouteCreation> {
    if (this.ordersCity === null) {
      const cities = new Set<string>();
      for (const order of this.state.orders) {
        // A closed step reads nothing more.
        if (this.disposed) return none;
        try {
          cities.add((await this.api.getOrder(order.orderId, this.abort.signal)).city_id);
        } catch {
          throw new OrderCityUnreadableError();
        }
      }
      const [cityId] = cities;
      this.ordersCity = cities.size === 1 && cityId !== undefined ? { kind: "same", cityId } : { kind: "mixed" };
    }
    if (this.ordersCity.kind === "mixed") return { kind: "mixed_cities" };
    const cityId = this.ordersCity.cityId;
    if (this.cityName === null) this.cityName = await this.readCityName(cityId);
    return { kind: "offered", cityId, cityName: this.cityName };
  }

  /** The city's name and state from listCities; null (the name is left out) when it cannot be read. */
  private async readCityName(cityId: string): Promise<string | null> {
    try {
      const city = (await this.api.listCities(this.abort.signal)).find((item) => item.id === cityId);
      return city === undefined ? null : cityLabel(city);
    } catch {
      return null;
    }
  }

  /** The notice of a creation left unconfirmed, once the DRAFT routes were read again (or not). */
  private unconfirmedNotice(outcome: "found" | "none" | "unread"): BulkRouteNotice | null {
    if (this.unconfirmed === null) return null;
    return {
      tone: "warn",
      text: unconfirmedRouteText(outcome, this.unconfirmed.sameRequest, this.driver.driverReference),
      stepUpHref: null,
    };
  }

  private async send(orderIds: readonly string[]): Promise<void> {
    let stopped: SendFailure | null = null;
    for (const [index, orderId] of orderIds.entries()) {
      if (this.disposed) return;
      const route = this.state.route;
      if (route === null) return;
      if (stopped !== null) {
        this.setResult(orderId, { kind: "not_sent" });
        continue;
      }
      // expected_version is the route's version: the one listed, then each authoritative answer's. A
      // stopped run changes no version, so a retry rebuilds the same fingerprint and reuses the key.
      const expectedVersion = route.version;
      const prepared = this.keys.prepare(orderId, `${route.id}|${expectedVersion}`, () => ({
        routeId: route.id,
        expectedVersion,
      }));
      this.setResult(orderId, { kind: "sending" });
      try {
        const detail = await this.api.addRouteStop(
          prepared.payload.routeId,
          orderId,
          prepared.payload.expectedVersion,
          prepared.key,
          this.abort.signal,
        );
        if (this.disposed) return;
        this.keys.settle(orderId);
        // The answer is the authoritative route: its version and stops replace what was read.
        this.set({
          ...this.state,
          route: routeOf(detail),
          results: { ...this.state.results, [orderId]: { kind: "added" } },
        });
      } catch (error: unknown) {
        if (this.disposed) return;
        const failure = this.sendFailure(error);
        if (!failure.retryable) this.keys.settle(orderId);
        this.setResult(orderId, failure.result);
        if (failure.stop) {
          stopped = failure;
        } else if (failure.checkRoute && index < orderIds.length - 1) {
          // The answer may hide a route that left DRAFT, is gone or changed: before sending another
          // order, the route must still be the driver's DRAFT route at the version just used.
          const unchanged = await this.isUnchanged(route);
          if (this.disposed) return;
          if (!unchanged) stopped = routeChangedStop;
        }
      }
    }
    if (this.disposed) return;
    const results = this.state.results;
    const added = this.state.orders.filter((order) => results[order.orderId]?.kind === "added").length;
    const summary = bulkRouteRunSummary(added, this.state.orders.length);
    if (stopped?.routeChanged === true) {
      // Someone else changed the route (or it cannot be confirmed unchanged): read it again and ask
      // for a new confirmation; a route no longer listed as DRAFT receives nothing more.
      this.set({ ...this.state, phase: "choosing", message: `${summary} ${bulkRouteChangedMessage}`, stepUpHref: null, canRetry: false });
      this.onChanged();
      await this.loadRoutes();
      return;
    }
    const canRetry =
      (stopped === null || stopped.retryable) &&
      this.state.orders.some((order) => isResendable(results[order.orderId]));
    const stop =
      stopped === null ? null : stopped.retryable ? bulkRouteStoppedRetryMessage : `El envío se detuvo: ${stopped.message}`;
    this.set({
      ...this.state,
      phase: "done",
      message: stop === null ? summary : `${summary} ${stop}`,
      stepUpHref: stopped?.stepUpHref ?? null,
      canRetry,
    });
    // REST is the authority: the queue and the counts are read again after every run.
    this.onChanged();
  }

  private sendFailure(error: unknown): SendFailure {
    if (error instanceof TenantApiError) {
      if (error.category === "conflict" && error.code === "VERSION_CONFLICT") return routeChangedStop;
      // Any other 409 or the uniform 404 answers for this order and the next order is sent; all but
      // DRIVER_INELIGIBLE and INVALID_REQUEST may also be the route's, so the route is checked first.
      if (error.category === "conflict" || error.category === "not_found") {
        const message =
          error.category === "not_found"
            ? routeStopNotFoundMessage
            : ((error.code === null ? undefined : routeStopConflictMessages[error.code]) ?? routeStopDefaultConflictMessage);
        return {
          result: { kind: "failed", message, retryable: false },
          retryable: false,
          stop: false,
          routeChanged: false,
          checkRoute: error.category === "not_found" || error.code === null || !orderConflictCodes.has(error.code),
          message,
          stepUpHref: null,
        };
      }
      if (error.category === "invalid")
        return {
          result: { kind: "failed", message: bulkRouteUnexpectedAnswerMessage, retryable: false },
          retryable: false,
          stop: true,
          routeChanged: false,
          checkRoute: false,
          message: bulkRouteUnexpectedAnswerMessage,
          stepUpHref: null,
        };
    }
    // Session, role, MFA, network or server: it would repeat for every order, so the run stops here.
    const view = describeFailure(error, this.returnUrl);
    const retryable = error instanceof TenantApiError && error.retryable;
    return {
      result: { kind: "failed", message: view.message, retryable },
      retryable,
      stop: true,
      routeChanged: false,
      checkRoute: false,
      message: view.message,
      stepUpHref: view.stepUpHref,
    };
  }

  /**
   * getRoute after a generic conflict: true only when the route is still the driver's DRAFT route at
   * the version the failed order used. A route that left DRAFT, is gone, changed or cannot be read
   * counts as changed.
   */
  private async isUnchanged(route: ManualRoute): Promise<boolean> {
    try {
      const current = await this.api.getRoute(route.id, this.abort.signal);
      return isDraftRouteOf(current, this.driver.driverId) && current.version === route.version;
    } catch {
      return false;
    }
  }

  /** Every page of the driver's DRAFT routes; a repeated cursor or too many pages leave it incomplete. */
  private async readAllRoutes(): Promise<readonly ManualRoute[]> {
    const routes: ManualRoute[] = [];
    const known = new Set<string>();
    const cursors = new Set<string>();
    let cursor: string | null = null;
    for (let page = 0; page < maximumRoutePages; page += 1) {
      const result = await this.api.listDraftRoutes(this.driver.driverId, cursor, this.abort.signal);
      for (const route of result.items) {
        if (known.has(route.id)) continue;
        known.add(route.id);
        routes.push(route);
      }
      if (result.next_cursor === null) return routes;
      if (cursors.has(result.next_cursor)) throw new RouteListIncompleteError();
      cursors.add(result.next_cursor);
      cursor = result.next_cursor;
    }
    throw new RouteListIncompleteError();
  }

  private setResult(orderId: string, result: BulkRouteRowResult): void {
    this.set({ ...this.state, results: { ...this.state.results, [orderId]: result } });
  }

  private set(state: BulkRouteState): void {
    this.state = state;
    if (!this.disposed) this.onChange(state);
  }
}

/** The route of an addRouteStop answer without its stops (the inbox never shows them). */
function routeOf(detail: ManualRouteDetail): ManualRoute {
  return {
    id: detail.id,
    status: detail.status,
    version: detail.version,
    driver_id: detail.driver_id,
    city_id: detail.city_id,
    service_area_id: detail.service_area_id,
    scheduled_for: detail.scheduled_for,
    assignment_cost_cents_total: detail.assignment_cost_cents_total,
    stop_count: detail.stop_count,
  };
}

function isResendable(result: BulkRouteRowResult | undefined): boolean {
  return result !== undefined && (result.kind === "not_sent" || (result.kind === "failed" && result.retryable));
}

function routesFailure(
  error: unknown,
  returnUrl: string,
): { readonly message: string; readonly stepUpHref: string | null } {
  if (error instanceof RouteListIncompleteError) return { message: bulkRoutesTooManyMessage, stepUpHref: null };
  // An order of the batch that cannot be read leaves the step as unavailable as an unreadable route list.
  if (error instanceof OrderCityUnreadableError) return { message: bulkRoutesUnavailableMessage, stepUpHref: null };
  if (error instanceof TenantApiError && (error.retryable || error.category === "invalid"))
    return { message: bulkRoutesUnavailableMessage, stepUpHref: null };
  return describeFailure(error, returnUrl);
}

/**
 * What a createRoute failure means. Session, role and MFA (with the step-up back to the inbox),
 * the uniform 404, DRIVER_INELIGIBLE and INVALID_REQUEST refuse the route for sure. No answer or a
 * server failure keeps the key to resend the same request; neither it, an unexpected answer nor a
 * generic CONFLICT (the server also answers it when its store fails) can say whether the route
 * exists, so the DRAFT routes are read again first.
 */
function createFailure(error: unknown, returnUrl: string): CreateFailure {
  if (!(error instanceof TenantApiError)) return { kind: "unconfirmed", sameRequest: false };
  if (error.retryable) return { kind: "unconfirmed", sameRequest: true };
  if (error.category === "not_found") return { kind: "refused", message: createRouteNotFoundMessage, stepUpHref: null };
  if (error.category === "conflict") {
    const refusal = error.code === null ? undefined : createRouteConflictMessages[error.code];
    return refusal === undefined
      ? { kind: "unconfirmed", sameRequest: false }
      : { kind: "refused", message: refusal, stepUpHref: null };
  }
  if (error.category === "invalid") return { kind: "unconfirmed", sameRequest: false };
  const view = describeFailure(error, returnUrl);
  return { kind: "refused", message: `No se creó la ruta: ${view.message}`, stepUpHref: view.stepUpHref };
}
