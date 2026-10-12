import type { BulkRouteApi } from "../api/bulk-route-api";
import { TenantApiError } from "../api/tenant-request";
import type { BulkOrder } from "../contracts/bulk-assignment";
import {
  bulkRouteRunSummary,
  isDraftRouteOf,
  routeStopConflictMessages,
  routeStopDefaultConflictMessage,
  routeStopNotFoundMessage,
  type BulkRouteDriver,
} from "../contracts/bulk-route";
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
 */
export type BulkRoutesLoad =
  | { readonly kind: "loading" }
  | { readonly kind: "ready"; readonly routes: readonly ManualRoute[] }
  | { readonly kind: "failed"; readonly message: string; readonly stepUpHref: string | null };

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
  private routesGeneration = 0;

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
  ) {
    this.state = initialBulkRouteState(orders);
    this.keys = new PendingSubmissions(() => `add-route-stop-${randomUuid()}`);
  }

  public get current(): BulkRouteState {
    return this.state;
  }

  /** Reads every DRAFT route of the driver, one page after another. */
  public async loadRoutes(): Promise<void> {
    if (this.disposed || this.state.phase !== "choosing") return;
    const generation = ++this.routesGeneration;
    this.set({ ...this.state, routes: { kind: "loading" } });
    try {
      const routes = await this.readAllRoutes();
      if (this.disposed || generation !== this.routesGeneration) return;
      const fixed = this.state.route;
      // The fixed route takes the version and stops just read; a route no longer listed stays as it was.
      const route = fixed === null ? null : (routes.find((item) => item.id === fixed.id) ?? fixed);
      this.set({ ...this.state, routes: { kind: "ready", routes }, route });
    } catch (error: unknown) {
      if (this.disposed || generation !== this.routesGeneration) return;
      this.set({ ...this.state, routes: { kind: "failed", ...routesFailure(error, this.returnUrl) } });
    }
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
    this.set({ ...this.state, phase: "running", route, results, message: null, stepUpHref: null, canRetry: false });
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
  if (error instanceof TenantApiError && (error.retryable || error.category === "invalid"))
    return { message: bulkRoutesUnavailableMessage, stepUpHref: null };
  return describeFailure(error, returnUrl);
}
