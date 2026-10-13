import type { CreateRouteInput, RoutesApi } from "../api/routes-api";
import { isCanonicalUuid, TenantApiError } from "../api/tenant-request";
import {
  createRouteNotFoundMessage,
  routeStopConflictMessages,
  routeStopDefaultConflictMessage,
  routeStopNotFoundMessage,
} from "../contracts/bulk-route";
import type { ManualRoute, ManualRouteDetail, ManualRoutePage } from "../contracts/manual-route";
import { normalizePublicIdInput, orderNotFoundMessage } from "../contracts/order-transitions";
import type { OperationsSession } from "../session/operations-session";
import { ExternalStore } from "./external-store";
import { orderSearchFailedMessage, orderSearchRateLimit } from "./order-search";
import { ScreenReloads, type ReloadTimers } from "./screen-reloads";
import { describeFailure, isRateLimited } from "./tenant-error-messages";
import { rateLimitWaits, type RateLimitWaits } from "../../lib/rate-limit-wait";

/**
 * UI-PHASE3-ROUTES-NO-IDS-2026-10-12 (project owner, 2026-10-12, "Sí, todo (Recommended)": "Mensaje por motivo y
 * verificar identidad, fechas legibles y todas las páginas, "Agregar entrega" por número de guía (PQ-…), y el detalle
 * muestra fecha, DRV- y guía en lugar de IDs."): state of the route planner (`/ops/routes`).
 *
 * - The list is every page of listRoutes, one after another, in the server's order, up to
 *   {@link maximumRouteListPages}; beyond that the screen says some routes are not shown, and a repeated cursor fails
 *   the read.
 * - Every change says why it was refused, by its AI-05 problem code; only VERSION_CONFLICT says the route changed,
 *   and it reads the route again. Session, role and MFA use describeFailure, with "Verificar identidad" back to
 *   `/ops/routes` and the route shown. Every change keeps its own fresh Idempotency-Key, as before.
 * - "Agregar entrega" takes the guía (`ORD_…`, the order public_id): the top-bar search validation first, then
 *   listOrders?public_id= (none is "No encontramos esa guía"; more than one or another guía fails closed), then
 *   addRouteStop with that order and the route's expected_version. `?orderId=` prefills the guía read with getOrder.
 * - The detail names each stop by its guía, read with getOrder one order after another (at most
 *   {@link maximumGuideReads} per route, cancelled when another route is opened or the screen closes, cached by order
 *   while the screen is open); an order that cannot be read is "Guía no disponible".
 *
 * Ids are keys only, never text. Everything lives in memory: nothing is stored in the browser nor logged, and a
 * session or organization change starts from empty.
 */
export const routesPath = "/ops/routes";

/** listRoutes pages (50 routes each) read, one after another, before the list is cut. */
export const maximumRouteListPages = 10;
/** Stops of a route whose guía is read; the rest show "Guía no disponible". */
export const maximumGuideReads = 100;

export const routeSavedMessage = "Cambio guardado.";
export const routeSavedStaleMessage = "Cambio guardado. No fue posible actualizar las rutas; usa Actualizar.";
export const routesNoSessionMessage = "Inicia una sesión de Operaciones.";
export const routesLoadFailedMessage = "No fue posible cargar las rutas. Usa Actualizar para reintentar.";
export const routesRealtimeFailedMessage = "No fue posible actualizar las rutas. Usa Actualizar para reintentar.";
export const routeLoadFailedMessage = "No fue posible cargar la ruta. Usa Actualizar para reintentar.";
export const routeNotFoundMessage = "La ruta ya no está disponible.";
export const routeOrderPrefilledMessage = "Selecciona una ruta para agregar la orden indicada.";
/** VERSION_CONFLICT only: the route was read again. */
export const routeChangedMessage =
  "La ruta cambió mientras la editabas. Se cargó la versión más reciente; revisa y vuelve a intentar.";
export const routeChangedUnreadMessage =
  "La ruta cambió mientras la editabas y no se pudo cargar la versión más reciente. Usa Actualizar para reintentar.";
/** INVALID_REQUEST of a stop change (the inbox text names the inbox). */
export const routePlannerInvalidRequestMessage = "El servidor no aceptó la solicitud. Actualiza la ruta e intenta de nuevo.";
/** No valid answer to a stop change: it may or may not be saved, and a retry would be another request. */
export const routeChangeUnconfirmedMessage =
  "No hubo respuesta válida del servidor y no se sabe si el cambio se guardó. Usa Actualizar para revisar la ruta " +
  "antes de volver a intentarlo.";
/** No valid answer to createRoute, or a generic conflict (the server also answers it when its store fails). */
export const routeCreateUnconfirmedMessage =
  "No se pudo confirmar si se creó la ruta. Usa Actualizar para revisar la lista antes de volver a intentarlo.";
/** createRoute 409 codes that refuse the route for sure, in the words of Rutas (driver, city and area chosen here). */
export const routePlannerCreateConflictMessages: Readonly<Record<string, string>> = {
  DRIVER_INELIGIBLE:
    "No se creó la ruta: el repartidor no puede tomar rutas en esa ciudad o área de servicio. Puede que su ciudad " +
    "sea otra o que hoy no esté habilitado (su estado o sus documentos).",
  INVALID_REQUEST: "No se creó la ruta: el servidor no aceptó la solicitud. Revisa los datos e intenta de nuevo.",
};
export const removeStopConflictMessage =
  "No se pudo retirar la parada: la ruta ya no está en borrador o cambió. Usa Actualizar para revisarla.";
export const reorderStopSetMessage =
  "El nuevo orden no coincide con las paradas actuales de la ruta. Usa Actualizar y vuelve a ordenarlas.";
export const reorderConflictMessage =
  "No se pudo cambiar el orden: la ruta ya no está en borrador o cambió. Usa Actualizar para revisarla.";

/** The guía of a stop: being read, read, or not available (never the order id). */
export type RouteGuide =
  | { readonly kind: "loading" }
  | { readonly kind: "ready"; readonly publicId: string }
  | { readonly kind: "unavailable" };

export interface RoutesNotice {
  readonly tone: "info" | "warn";
  readonly text: string;
  /** `/login?mfa=required&return_url=…` when only MFA was missing. */
  readonly stepUpHref: string | null;
}

export interface ManualRoutesState {
  /** A session is installed; without one nothing is read and every change asks to sign in. */
  readonly hasSession: boolean;
  /** Every page of listRoutes read, in the server's order. */
  readonly routes: readonly ManualRoute[];
  /** More routes exist than {@link maximumRouteListPages} pages hold; the list says some are not shown. */
  readonly routesTruncated: boolean;
  readonly selected: ManualRouteDetail | null;
  /** The guía of each stop of the selected route, by order id. */
  readonly guides: Readonly<Record<string, RouteGuide>>;
  /** The "Guía (ORD_…)" field of "Agregar entrega". */
  readonly guide: string;
  readonly loading: boolean;
  readonly mutating: boolean;
  readonly notice: RoutesNotice | null;
  readonly connected: boolean;
}

export interface ManualRoutesDependencies {
  readonly readSession: () => OperationsSession | null;
  readonly createApi: (session: OperationsSession) => RoutesApi;
  /** The page query: `routeId` opens a listed route, `orderId` prefills that order's guía. */
  readonly readQuery: () => URLSearchParams;
  /** Idempotency-Key source: a fresh key for every change, as the screen always did. */
  readonly randomUuid?: () => string;
  /** SEC-004: the tab's server waits (tests pass their own). */
  readonly waits?: RateLimitWaits;
  /** The clock and timers of the realtime spacing (tests pass fake ones). */
  readonly timers?: Partial<ReloadTimers>;
}

export type RouteMutation = "create" | "add" | "remove" | "reorder";

const initialState: ManualRoutesState = {
  hasSession: false,
  routes: [],
  routesTruncated: false,
  selected: null,
  guides: {},
  guide: "",
  loading: false,
  mutating: false,
  notice: null,
  connected: false,
};

class RepeatedRouteCursorError extends Error {
  public constructor() {
    super("listRoutes repeated a cursor.");
    this.name = "RepeatedRouteCursorError";
  }
}

interface GuideWorker {
  readonly routeId: string;
  readonly abort: AbortController;
  inFlight: string | null;
}

export class ManualRoutesController extends ExternalStore<ManualRoutesState> {
  private api: RoutesApi | null = null;
  private generation = 0;
  private listGeneration = 0;
  private abort = new AbortController();
  private readonly randomUuid: () => string;
  /** The route shown or being opened; an answer for another route is dropped. */
  private targetRouteId: string | null = null;
  private readonly guideCache = new Map<string, string>();
  private readonly guideFailures = new Set<string>();
  private guideQueue: string[] = [];
  private guideWorker: GuideWorker | null = null;
  /**
   * SEC-004-RELOADS-AND-BULK-2026-10-13, owner answer Q9 ("el panel"): RouteChanged notices are gathered into at most
   * one read every 3 seconds. The routes have no limit, so no server wait pauses them.
   */
  private readonly realtime: ScreenReloads<"routes">;
  private changedRoutes = new Set<string>();

  public constructor(private readonly dependencies: ManualRoutesDependencies) {
    super(initialState);
    this.randomUuid = dependencies.randomUuid ?? (() => crypto.randomUUID());
    this.realtime = new ScreenReloads({
      operations: [],
      timers: dependencies.timers,
      reload: () => void this.readChangedRoutes(),
    });
  }

  /** A RouteChanged notice: read with the others of the same 3 seconds (owner answer Q9). */
  public notice(routeId: string): void {
    if (this.api === null) return;
    this.changedRoutes.add(routeId);
    this.realtime.notice("routes");
  }

  /** The gathered notices: the list once, and the route shown when one of them named it. */
  private async readChangedRoutes(): Promise<void> {
    const changed = this.changedRoutes;
    this.changedRoutes = new Set();
    await this.readAfterNotices(changed);
  }

  /**
   * Starts (again) for the installed session: every page of the routes, the route `?routeId=` names when it is
   * listed, and the guía of `?orderId=`. Returns the session once the routes were read (the realtime hub then
   * connects), or null.
   */
  public async start(): Promise<OperationsSession | null> {
    this.reset();
    const session = this.dependencies.readSession();
    const api = session === null ? null : this.dependencies.createApi(session);
    this.api = api;
    this.update({ ...initialState, hasSession: api !== null });
    if (api === null || session === null) return null;
    const generation = this.generation;
    const query = this.dependencies.readQuery();
    this.update({ loading: true });
    let listed = false;
    try {
      const routes = await this.readRoutes(api);
      if (generation !== this.generation) return null;
      listed = true;
      const routeId = query.get("routeId");
      if (routeId !== null && routes.some((route) => route.id === routeId)) {
        try {
          await this.loadDetail(api, routeId, generation, true);
        } catch (error: unknown) {
          if (generation === this.generation) this.restoreTarget(detailFailure(error, this.returnUrl()));
        }
      }
    } catch (error: unknown) {
      if (generation === this.generation) this.update({ notice: listFailure(error, this.returnUrl()) });
    } finally {
      if (generation === this.generation) this.update({ loading: false });
    }
    if (generation !== this.generation) return null;
    await this.prefill(api, query.get("orderId"), generation);
    return generation === this.generation && listed ? session : null;
  }

  /** Leaves the screen: cancels every read and forgets everything. */
  public stop(): void {
    this.reset();
    this.api = null;
    this.update(initialState);
  }

  /** "Actualizar": every page of the routes, then the route shown. */
  public async refresh(): Promise<void> {
    const api = this.api;
    if (api === null) return;
    const generation = this.generation;
    this.update({ loading: true });
    try {
      await this.readRoutes(api);
    } catch (error: unknown) {
      if (generation === this.generation) this.update({ notice: listFailure(error, this.returnUrl()), loading: false });
      return;
    }
    const selected = this.getSnapshot().selected;
    try {
      if (selected !== null) await this.loadDetail(api, selected.id, generation);
      if (generation === this.generation) this.update({ notice: null });
    } catch (error: unknown) {
      if (generation === this.generation) this.update({ notice: detailFailure(error, this.returnUrl()) });
    } finally {
      if (generation === this.generation) this.update({ loading: false });
    }
  }

  /** Opens a route of the list. */
  public async select(routeId: string): Promise<void> {
    const api = this.api;
    if (api === null) return;
    const generation = this.generation;
    this.update({ loading: true });
    try {
      await this.loadDetail(api, routeId, generation, true);
      if (generation === this.generation) this.update({ notice: null });
    } catch (error: unknown) {
      if (generation === this.generation) this.restoreTarget(detailFailure(error, this.returnUrl(routeId)));
    } finally {
      if (generation === this.generation) this.update({ loading: false });
    }
  }

  public setGuide(value: string): void {
    this.update({ guide: value });
  }

  /** "Crear ruta (borrador)": createRoute, then the list read again and the new route opened. */
  public async create(input: CreateRouteInput): Promise<void> {
    const api = this.begin();
    if (api === null) return;
    const generation = this.generation;
    try {
      const route = await api.create(input, this.randomUuid(), this.abort.signal);
      if (generation !== this.generation) return;
      await this.afterSave(api, generation, route.id);
    } catch (error: unknown) {
      if (generation === this.generation) await this.fail(api, error, "create", null, generation);
    } finally {
      this.end(generation);
    }
  }

  /**
   * "Agregar entrega": the typed guía is checked like the top-bar search, resolved with listOrders?public_id= and
   * added with addRouteStop at the route's current version. Nothing is sent when another route was opened meanwhile.
   */
  public async addStop(): Promise<void> {
    const route = this.getSnapshot().selected;
    if (route === null) return;
    // SEC-004 (owner answer Q13): "Agregar entrega" stays disabled during the server's wait for listOrders.
    if ((this.dependencies.waits ?? rateLimitWaits).isWaiting(orderSearchRateLimit)) return;
    const api = this.begin();
    if (api === null) return;
    const generation = this.generation;
    try {
      const publicId = normalizePublicIdInput(this.getSnapshot().guide);
      // A value that cannot be a guía is never sent; it reads like an unknown one.
      if (publicId === null) {
        this.update({ notice: warn(orderNotFoundMessage) });
        return;
      }
      let orderId: string | null;
      try {
        orderId = await api.findOrderIdByPublicId(publicId, this.abort.signal);
      } catch (error: unknown) {
        // SEC-004 (owner answer Q13): a 429 shows the common message with its countdown; the typed guía stays.
        if (generation === this.generation)
          this.update({ notice: isRateLimited(error) ? null : lookupFailure(error, this.returnUrl(route.id)) });
        return;
      }
      if (generation !== this.generation) return;
      if (orderId === null) {
        this.update({ notice: warn(orderNotFoundMessage) });
        return;
      }
      const current = this.getSnapshot().selected;
      if (current === null || current.id !== route.id || this.targetRouteId !== route.id) return;
      this.guideCache.set(orderId, publicId);
      const detail = await api.addStop(current.id, orderId, current.version, this.randomUuid(), this.abort.signal);
      if (generation !== this.generation) return;
      this.applyDetail(detail);
      this.update({ guide: "" });
      await this.afterSave(api, generation);
    } catch (error: unknown) {
      if (generation === this.generation) await this.fail(api, error, "add", route.id, generation);
    } finally {
      this.end(generation);
    }
  }

  /** "Retirar", after its confirmation: removeRouteStop at the route's current version. */
  public async removeStop(stopId: string): Promise<void> {
    const route = this.getSnapshot().selected;
    if (route === null) return;
    const api = this.begin();
    if (api === null) return;
    const generation = this.generation;
    try {
      const detail = await api.removeStop(route.id, stopId, route.version, this.randomUuid(), this.abort.signal);
      if (generation !== this.generation) return;
      this.applyDetail(detail);
      await this.afterSave(api, generation);
    } catch (error: unknown) {
      if (generation === this.generation) await this.fail(api, error, "remove", route.id, generation);
    } finally {
      this.end(generation);
    }
  }

  /** "Subir", "Bajar" or a drop: reorderRouteStops with every stop, at the route's current version. */
  public async reorder(stopIds: readonly string[]): Promise<void> {
    const route = this.getSnapshot().selected;
    if (route === null) return;
    const api = this.begin();
    if (api === null) return;
    const generation = this.generation;
    try {
      const detail = await api.reorder(route.id, stopIds, route.version, this.randomUuid(), this.abort.signal);
      if (generation !== this.generation) return;
      this.applyDetail(detail);
      await this.afterSave(api, generation);
    } catch (error: unknown) {
      if (generation === this.generation) await this.fail(api, error, "reorder", route.id, generation);
    } finally {
      this.end(generation);
    }
  }

  /** RouteChanged only asks for REST: the list, and the route when it is the one shown. */
  public async routeChanged(routeId: string): Promise<void> {
    await this.readAfterNotices(new Set([routeId]));
  }

  private async readAfterNotices(changed: ReadonlySet<string>): Promise<void> {
    const api = this.api;
    if (api === null) return;
    const generation = this.generation;
    this.realtime.reloaded();
    try {
      await this.readRoutes(api);
      const selected = this.getSnapshot().selected;
      if (selected !== null && changed.has(selected.id)) await this.loadDetail(api, selected.id, generation);
    } catch {
      if (generation === this.generation) this.update({ notice: warn(routesRealtimeFailedMessage) });
    }
  }

  /** The REST read of a realtime reconnection; a failure is the hub's to report. */
  public async resynchronize(): Promise<{ readonly aggregate_versions: Record<string, number> }> {
    const api = this.api;
    if (api === null) throw new TenantApiError("unauthorized");
    const generation = this.generation;
    const routes = await this.readRoutes(api);
    const selected = this.getSnapshot().selected;
    if (selected !== null) await this.loadDetail(api, selected.id, generation);
    return { aggregate_versions: Object.fromEntries(routes.map((route) => [route.id, route.version])) };
  }

  public setConnected(connected: boolean): void {
    this.update({ connected });
  }

  /** The hub could not read REST again after a reconnection. */
  public realtimeFailed(): void {
    this.update({ connected: false, notice: warn(routesRealtimeFailedMessage) });
  }

  /** Where "Verificar identidad" returns: Rutas, with the route shown. */
  private returnUrl(routeId: string | null = this.getSnapshot().selected?.id ?? null): string {
    return routeId === null ? routesPath : `${routesPath}?routeId=${encodeURIComponent(routeId)}`;
  }

  private begin(): RoutesApi | null {
    if (this.getSnapshot().mutating) return null;
    if (this.api === null) {
      this.update({ notice: warn(routesNoSessionMessage) });
      return null;
    }
    this.update({ mutating: true, notice: null });
    return this.api;
  }

  private end(generation: number): void {
    if (generation === this.generation) this.update({ mutating: false });
  }

  /** "Cambio guardado.", then the list read again (and a new route opened); a failed read does not hide the change. */
  private async afterSave(api: RoutesApi, generation: number, open?: string): Promise<void> {
    this.update({ notice: info(routeSavedMessage) });
    let stale = false;
    try {
      await this.readRoutes(api);
    } catch {
      stale = true;
    }
    if (open !== undefined) {
      try {
        await this.loadDetail(api, open, generation, true);
      } catch {
        stale = true;
        if (generation === this.generation) this.targetRouteId = this.getSnapshot().selected?.id ?? null;
      }
    }
    if (stale && generation === this.generation) this.update({ notice: warn(routeSavedStaleMessage) });
  }

  /** The es-MX reason of a refused change; a VERSION_CONFLICT reads the route again first. */
  private async fail(
    api: RoutesApi,
    error: unknown,
    mutation: RouteMutation,
    routeId: string | null,
    generation: number,
  ): Promise<void> {
    const failure = routeMutationFailure(error, mutation, this.returnUrl());
    if (failure.reload && routeId !== null) {
      try {
        await this.loadDetail(api, routeId, generation);
      } catch {
        if (generation === this.generation) this.update({ notice: warn(routeChangedUnreadMessage) });
        return;
      }
    }
    if (generation === this.generation)
      this.update({ notice: { tone: "warn", text: failure.text, stepUpHref: failure.stepUpHref } });
  }

  private restoreTarget(notice: RoutesNotice): void {
    this.targetRouteId = this.getSnapshot().selected?.id ?? null;
    this.update({ notice });
  }

  /** Every page of listRoutes, one after another; only the newest read of the session is shown. */
  private async readRoutes(api: RoutesApi): Promise<readonly ManualRoute[]> {
    const generation = this.generation;
    const listGeneration = ++this.listGeneration;
    const routes: ManualRoute[] = [];
    const known = new Set<string>();
    const cursors = new Set<string>();
    let cursor: string | null = null;
    let truncated = false;
    for (let page = 0; ; page += 1) {
      if (page === maximumRouteListPages) {
        truncated = true;
        break;
      }
      const result: ManualRoutePage = await api.list(cursor, this.abort.signal);
      for (const route of result.items) {
        if (known.has(route.id)) continue;
        known.add(route.id);
        routes.push(route);
      }
      if (result.next_cursor === null) break;
      if (cursors.has(result.next_cursor)) throw new RepeatedRouteCursorError();
      cursors.add(result.next_cursor);
      cursor = result.next_cursor;
    }
    if (generation === this.generation && listGeneration === this.listGeneration)
      this.update({ routes, routesTruncated: truncated });
    return routes;
  }

  private async loadDetail(api: RoutesApi, routeId: string, generation: number, open = false): Promise<void> {
    if (open) this.targetRouteId = routeId;
    const detail = await api.get(routeId, this.abort.signal);
    if (generation === this.generation) this.applyDetail(detail);
  }

  /** Shows an authoritative detail of the route shown or being opened; an older version is never shown over a newer one. */
  private applyDetail(detail: ManualRouteDetail): void {
    if (detail.id !== this.targetRouteId) return;
    const { selected: shown, notice } = this.getSnapshot();
    if (shown !== null && shown.id === detail.id && shown.version > detail.version) return;
    // A route is open now: the ?orderId= cue to pick one no longer applies, whenever the guía arrived.
    this.update(notice?.text === routeOrderPrefilledMessage ? { selected: detail, notice: null } : { selected: detail });
    this.requestGuides(detail);
  }

  /**
   * `?orderId=`: that order's guía, read with getOrder, fills an empty field; an order that cannot be read leaves it
   * empty. The cue "Selecciona una ruta…" is shown only while no route is open nor being opened.
   */
  private async prefill(api: RoutesApi, orderId: string | null, generation: number): Promise<void> {
    if (orderId === null || !isCanonicalUuid(orderId)) return;
    let publicId: string;
    try {
      publicId = await api.getOrderGuide(orderId, this.abort.signal);
    } catch {
      return;
    }
    if (generation !== this.generation) return;
    this.guideCache.set(orderId, publicId);
    const state = this.getSnapshot();
    if (state.guide !== "") return;
    const choosing = state.selected === null && this.targetRouteId === null;
    this.update(
      choosing && state.notice === null
        ? { guide: publicId, notice: info(routeOrderPrefilledMessage) }
        : { guide: publicId },
    );
  }

  /**
   * The guía of every stop of the route shown, up to {@link maximumGuideReads}: cached ones at once, the others read
   * with getOrder one after another. Opening another route cancels the read in flight; a failed read is tried again
   * the next time the route is read.
   */
  private requestGuides(detail: ManualRouteDetail): void {
    const api = this.api;
    if (api === null) return;
    if (this.guideWorker !== null && this.guideWorker.routeId !== detail.id) {
      this.guideWorker.abort.abort();
      this.guideWorker = null;
    }
    const inFlight = this.guideWorker?.inFlight ?? null;
    this.guideQueue = detail.stops
      .slice(0, maximumGuideReads)
      .map((stop) => stop.order_id)
      .filter((orderId) => !this.guideCache.has(orderId) && orderId !== inFlight);
    for (const orderId of this.guideQueue) this.guideFailures.delete(orderId);
    this.publishGuides();
    if (this.guideWorker === null && this.guideQueue.length > 0) void this.readGuides(api, detail.id);
  }

  private async readGuides(api: RoutesApi, routeId: string): Promise<void> {
    const generation = this.generation;
    const worker: GuideWorker = { routeId, abort: new AbortController(), inFlight: null };
    this.guideWorker = worker;
    const signal = AbortSignal.any([this.abort.signal, worker.abort.signal]);
    try {
      for (let orderId = this.guideQueue.shift(); orderId !== undefined; orderId = this.guideQueue.shift()) {
        if (this.guideCache.has(orderId)) continue;
        worker.inFlight = orderId;
        try {
          const publicId = await api.getOrderGuide(orderId, signal);
          if (signal.aborted || generation !== this.generation) return;
          this.guideCache.set(orderId, publicId);
        } catch {
          if (signal.aborted || generation !== this.generation) return;
          this.guideFailures.add(orderId);
        } finally {
          worker.inFlight = null;
        }
        this.publishGuides();
      }
    } finally {
      if (this.guideWorker === worker) this.guideWorker = null;
    }
  }

  private publishGuides(): void {
    const stops = this.getSnapshot().selected?.stops ?? [];
    const guides: Record<string, RouteGuide> = {};
    for (const [index, stop] of stops.entries()) {
      const publicId = this.guideCache.get(stop.order_id);
      guides[stop.order_id] =
        publicId !== undefined
          ? { kind: "ready", publicId }
          : index >= maximumGuideReads || this.guideFailures.has(stop.order_id)
            ? { kind: "unavailable" }
            : { kind: "loading" };
    }
    this.update({ guides });
  }

  private reset(): void {
    this.generation += 1;
    this.realtime.cancel();
    this.changedRoutes = new Set();
    this.abort.abort();
    this.abort = new AbortController();
    this.targetRouteId = null;
    this.guideWorker?.abort.abort();
    this.guideWorker = null;
    this.guideQueue = [];
    this.guideCache.clear();
    this.guideFailures.clear();
  }
}

function info(text: string): RoutesNotice {
  return { tone: "info", text, stepUpHref: null };
}

function warn(text: string): RoutesNotice {
  return { tone: "warn", text, stepUpHref: null };
}

function sessionFailure(error: unknown, returnUrl: string): RoutesNotice | null {
  if (!(error instanceof TenantApiError) || (error.category !== "unauthorized" && error.category !== "forbidden"))
    return null;
  const view = describeFailure(error, returnUrl);
  return { tone: "warn", text: view.message, stepUpHref: view.stepUpHref };
}

function listFailure(error: unknown, returnUrl: string): RoutesNotice {
  return sessionFailure(error, returnUrl) ?? warn(routesLoadFailedMessage);
}

function detailFailure(error: unknown, returnUrl: string): RoutesNotice {
  if (error instanceof TenantApiError && error.category === "not_found") return warn(routeNotFoundMessage);
  return sessionFailure(error, returnUrl) ?? warn(routeLoadFailedMessage);
}

/** The guía lookup (listOrders): session, role and MFA in their words; anything else, the top-bar search failure. */
function lookupFailure(error: unknown, returnUrl: string): RoutesNotice {
  return sessionFailure(error, returnUrl) ?? warn(orderSearchFailedMessage);
}

function codeMessage(messages: Readonly<Record<string, string>>, code: string | null): string | undefined {
  return code !== null && Object.hasOwn(messages, code) ? messages[code] : undefined;
}

/**
 * The es-MX reason of a refused Rutas change, by its AI-05 RoutingConflict code; never the code nor an identifier.
 * Only VERSION_CONFLICT says the route changed (`reload`: read it again). Session, role and MFA come from
 * describeFailure with the step-up back to Rutas. No valid answer says the change cannot be confirmed: every change
 * keeps a fresh key, so a retry would be another request.
 */
export function routeMutationFailure(
  error: unknown,
  mutation: RouteMutation,
  returnUrl: string,
): { readonly text: string; readonly stepUpHref: string | null; readonly reload: boolean } {
  const session = sessionFailure(error, returnUrl);
  if (session !== null) return { text: session.text, stepUpHref: session.stepUpHref, reload: false };
  const unconfirmed = mutation === "create" ? routeCreateUnconfirmedMessage : routeChangeUnconfirmedMessage;
  if (!(error instanceof TenantApiError)) return { text: unconfirmed, stepUpHref: null, reload: false };
  if (error.category === "not_found") {
    const text =
      mutation === "create" ? createRouteNotFoundMessage : mutation === "reorder" ? routeNotFoundMessage : routeStopNotFoundMessage;
    return { text, stepUpHref: null, reload: false };
  }
  if (error.category !== "conflict") return { text: unconfirmed, stepUpHref: null, reload: false };
  const code = error.code;
  if (mutation === "create")
    return { text: codeMessage(routePlannerCreateConflictMessages, code) ?? unconfirmed, stepUpHref: null, reload: false };
  if (code === "VERSION_CONFLICT") return { text: routeChangedMessage, stepUpHref: null, reload: true };
  if (code === "INVALID_REQUEST") return { text: routePlannerInvalidRequestMessage, stepUpHref: null, reload: false };
  if (mutation === "add")
    return {
      text: codeMessage(routeStopConflictMessages, code) ?? routeStopDefaultConflictMessage,
      stepUpHref: null,
      reload: false,
    };
  if (mutation === "remove") return { text: removeStopConflictMessage, stepUpHref: null, reload: false };
  return {
    text: code === "INVALID_STOP_SET" ? reorderStopSetMessage : reorderConflictMessage,
    stepUpHref: null,
    reload: false,
  };
}
