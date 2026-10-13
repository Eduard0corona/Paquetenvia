import type { OrderListApi } from "../api/order-list-api";
import { TenantApiError } from "../api/tenant-request";
import { canOpenOrderDetail, canOpenOrderList } from "../contracts/capabilities";
import {
  defaultOrderListView,
  orderListPath,
  orderListRange,
  orderListViewKey,
  type OrderListItem,
  type OrderListQuery,
  type OrderListView,
} from "../contracts/order-list";
import type { OperationsSession } from "../session/operations-session";
import { ExternalStore } from "./external-store";
import { describeFailure, isRateLimited } from "./tenant-error-messages";
import { rateLimitWaits, type RateLimitedOperation, type RateLimitWaits } from "../../lib/rate-limit-wait";

/**
 * UI-PHASE3-ORDERS-LIST-2026-10-12 ("Sí, lista de órdenes"): state of the "Órdenes" screen.
 *
 * REST is the only source of truth: every read replaces the list with the first page listOrders returns for the
 * filters on screen (status and creation period from the URL, the guía typed in the search), and "Cargar más" appends
 * the next page with the cursor of that same query (ORD-LIST-DATE-FILTER-2026-10-12 binds a cursor to its filters).
 * A filter change drops the rows of the previous filters at once. The page becoming visible or focused again re-reads
 * the first page only while no further page was loaded, so "Cargar más" pages are never dropped behind the person's
 * back. Everything lives in memory; a session or organization change starts from an empty list.
 */
export type OrderListTrigger = "initial" | "filters" | "manual" | "visibility" | "focus";

const backgroundTriggers: ReadonlySet<OrderListTrigger> = new Set(["visibility", "focus"]);
export const backgroundRefreshIntervalMilliseconds = 5_000;

export const orderListLoadFailedMessage = "No fue posible actualizar la lista de órdenes.";
export const orderListPaginationFailedMessage = "No fue posible cargar más órdenes. Actualiza la lista.";
export const orderListFiltersRefusedMessage =
  "El servidor no aceptó estos filtros. Revisa las fechas y vuelve a intentar.";

export interface OrderListState {
  readonly phase: "no_session" | "loading" | "ready" | "access_unavailable";
  readonly role: string | null;
  /** The role opens /ops/orders/:id (DISPATCHER, PLATFORM_ADMIN); VIEWER lists without links. */
  readonly canOpenDetail: boolean;
  /** Status and creation period, as in the URL. */
  readonly view: OrderListView;
  /** The exact guía (public_id) being searched, or null. Never kept outside this tab's memory. */
  readonly guide: string | null;
  /** Orders of the current filters, newest first, as the API returned them. */
  readonly rows: readonly OrderListItem[];
  readonly nextCursor: string | null;
  /** The current filters were answered at least once (tells "no orders" from a pending read). */
  readonly loaded: boolean;
  readonly loading: boolean;
  readonly loadingMore: boolean;
  /** The creation period on screen cannot be sent (order or length); nothing is read until it is fixed. */
  readonly rangeError: string | null;
  /** The last read failed; the rows of the same filters stay. */
  readonly error: string | null;
  /** A refusal only a second factor would lift, with its step-up link. */
  readonly message: string | null;
  readonly stepUpHref: string | null;
  /** When the last read was answered (device clock, shown in Mazatlán time). */
  readonly lastUpdated: string | null;
}

export interface OrderListDependencies {
  readonly readSession: () => OperationsSession | null;
  readonly createApi: (session: OperationsSession) => OrderListApi;
  readonly loadRole: (session: OperationsSession, signal: AbortSignal) => Promise<string | null>;
  readonly isHidden?: () => boolean;
  readonly now?: () => number;
  /** SEC-004: the tab's server-imposed waits (tests pass their own). */
  readonly waits?: RateLimitWaits;
}

/** SEC-004: listOrders, 120 per minute per user (the pending COD list spends the same budget). */
export const orderListRateLimit: readonly RateLimitedOperation[] = ["order_list"];

const emptyList = {
  rows: [],
  nextCursor: null,
  loaded: false,
  loading: false,
  loadingMore: false,
  error: null,
  message: null,
  stepUpHref: null,
  lastUpdated: null,
} as const satisfies Partial<OrderListState>;

function initialState(view: OrderListView): OrderListState {
  return {
    phase: "no_session",
    role: null,
    canOpenDetail: false,
    view,
    guide: null,
    rangeError: null,
    ...emptyList,
  };
}

export class OrderListController extends ExternalStore<OrderListState> {
  private api: OrderListApi | null = null;
  private generation = 0;
  private sessionController: AbortController | null = null;
  private readController: AbortController | null = null;
  private readSequence = 0;
  /** The query whose first page is on screen; "Cargar más" sends exactly it with the cursor. */
  private pageQuery: OrderListQuery | null = null;
  private pagesLoaded = 0;
  private seenCursors = new Set<string>();
  private lastReadAt = Number.NEGATIVE_INFINITY;

  public constructor(
    private readonly dependencies: OrderListDependencies,
    view: OrderListView = defaultOrderListView,
  ) {
    super(initialState(view));
  }

  /** Starts (again) for the installed session: drops everything of the previous one, then reads the list. */
  public async start(): Promise<void> {
    const view = this.getSnapshot().view;
    this.reset();
    const generation = this.generation;
    const session = this.dependencies.readSession();
    this.api = session === null ? null : this.dependencies.createApi(session);
    this.update({ ...initialState(view), phase: session === null ? "no_session" : "loading" });
    if (session === null) return;
    let role: string | null;
    try {
      role = await this.dependencies.loadRole(session, this.sessionController!.signal);
    } catch {
      role = null;
    }
    if (generation !== this.generation) return;
    if (!canOpenOrderList(role)) {
      this.update({ phase: "access_unavailable", role });
      return;
    }
    this.update({ phase: "ready", role, canOpenDetail: canOpenOrderDetail(role) });
    await this.refresh("initial");
  }

  /** Leaves the screen: cancels every request and forgets the session's data. */
  public stop(): void {
    const view = this.getSnapshot().view;
    this.reset();
    this.api = null;
    this.update(initialState(view));
  }

  /** The URL filters changed: the previous rows go and the new filters are read. */
  public async setView(view: OrderListView): Promise<void> {
    if (orderListViewKey(view) === orderListViewKey(this.getSnapshot().view)) return;
    this.update({ view });
    await this.refresh("filters");
  }

  /** Searches one exact guía (already validated) or, with null, stops searching. */
  public async setGuide(publicId: string | null): Promise<void> {
    if (publicId === this.getSnapshot().guide) return;
    this.update({ guide: publicId });
    await this.refresh("filters");
  }

  /** Reads the first page of the current filters and replaces the list with it. */
  public async refresh(trigger: OrderListTrigger = "manual"): Promise<void> {
    const api = this.api;
    const state = this.getSnapshot();
    if (api === null || state.phase !== "ready") return;
    if (backgroundTriggers.has(trigger)) {
      if (this.dependencies.isHidden?.() ?? false) return;
      if (state.loading || state.loadingMore || this.pagesLoaded > 1) return;
      if (this.now() - this.lastReadAt < backgroundRefreshIntervalMilliseconds) return;
    }

    const generation = this.generation;
    const sequence = ++this.readSequence;
    this.readController?.abort();
    const controller = new AbortController();
    this.readController = controller;
    const filtersChanged = trigger === "filters";
    if (filtersChanged) {
      this.pageQuery = null;
      this.pagesLoaded = 0;
      this.seenCursors = new Set();
      this.update({ ...emptyList });
    }

    const range = orderListRange(state.view);
    if (!range.ok) {
      // Nothing is sent: the rows of other filters are gone and the screen says what to fix.
      this.pageQuery = null;
      this.pagesLoaded = 0;
      this.update({ ...emptyList, rangeError: range.message });
      return;
    }
    const query: OrderListQuery = {
      status: state.view.status,
      publicId: state.guide,
      createdFrom: range.createdFrom,
      createdTo: range.createdTo,
    };
    // SEC-004 (owner answer Q6): while the server's wait lasts nothing is sent (another request would only meet the
    // same 429); "Actualizar" re-enables itself when it ends.
    if (this.waiting()) {
      this.update({ rangeError: null });
      return;
    }

    this.update({ loading: true, loadingMore: false, rangeError: null });
    try {
      const page = await api.list(query, null, controller.signal);
      if (!this.isCurrent(generation, sequence)) return;
      this.pageQuery = query;
      this.pagesLoaded = 1;
      this.seenCursors = new Set(page.next_cursor === null ? [] : [page.next_cursor]);
      this.update({
        rows: page.items,
        nextCursor: page.next_cursor,
        loaded: true,
        error: null,
        message: null,
        stepUpHref: null,
        lastUpdated: new Date(this.now()).toISOString(),
      });
    } catch (error: unknown) {
      if (!this.isCurrent(generation, sequence)) return;
      this.fail(error, "read");
    } finally {
      if (this.isCurrent(generation, sequence)) {
        this.lastReadAt = this.now();
        this.update({ loading: false });
      }
    }
  }

  /** Appends the next page of the same query; a cursor the list already followed stops the paging with a message. */
  public async loadMore(): Promise<void> {
    const api = this.api;
    const state = this.getSnapshot();
    const query = this.pageQuery;
    const cursor = state.nextCursor;
    if (api === null || state.phase !== "ready" || query === null || cursor === null) return;
    if (state.loading || state.loadingMore || this.waiting()) return;
    const generation = this.generation;
    const sequence = this.readSequence;
    this.update({ loadingMore: true });
    try {
      const page = await api.list(query, cursor, this.readController?.signal);
      if (!this.isCurrent(generation, sequence)) return;
      if (page.next_cursor !== null && this.seenCursors.has(page.next_cursor)) {
        this.update({ nextCursor: null, error: orderListPaginationFailedMessage });
        return;
      }
      if (page.next_cursor !== null) this.seenCursors.add(page.next_cursor);
      const known = new Set(this.getSnapshot().rows.map((row) => row.id));
      this.pagesLoaded += 1;
      this.update({
        rows: [...this.getSnapshot().rows, ...page.items.filter((row) => !known.has(row.id))],
        nextCursor: page.next_cursor,
        error: null,
      });
    } catch (error: unknown) {
      if (this.isCurrent(generation, sequence)) this.fail(error, "page");
    } finally {
      if (this.isCurrent(generation, sequence)) this.update({ loadingMore: false });
    }
  }

  private fail(error: unknown, kind: "read" | "page"): void {
    // SEC-004 (owner answer Q6): the screen shows the common message with its countdown; nothing of its own.
    if (isRateLimited(error)) {
      this.update({ error: null });
      return;
    }
    if (error instanceof TenantApiError && (error.category === "unauthorized" || error.category === "forbidden")) {
      if (error.mfaRequired) {
        // Only the second factor is missing: nothing is shown until it is verified.
        const view = describeFailure(error, orderListPath);
        this.pageQuery = null;
        this.pagesLoaded = 0;
        this.update({ rows: [], nextCursor: null, error: null, message: view.message, stepUpHref: view.stepUpHref });
        return;
      }
      // Lost access clears everything, as on the other operations lists.
      const { role, view } = this.getSnapshot();
      this.reset();
      this.api = null;
      this.update({ ...initialState(view), phase: "access_unavailable", role });
      return;
    }
    if (kind === "page") {
      this.update({ error: orderListPaginationFailedMessage });
      return;
    }
    const refused = error instanceof TenantApiError && error.category === "conflict" && error.code === "INVALID_REQUEST";
    this.update({ error: refused ? orderListFiltersRefusedMessage : orderListLoadFailedMessage });
  }

  private isCurrent(generation: number, sequence: number): boolean {
    return generation === this.generation && sequence === this.readSequence;
  }

  private reset(): void {
    this.generation += 1;
    this.sessionController?.abort();
    this.sessionController = new AbortController();
    this.readController?.abort();
    this.readController = null;
    this.readSequence += 1;
    this.pageQuery = null;
    this.pagesLoaded = 0;
    this.seenCursors = new Set();
    this.lastReadAt = Number.NEGATIVE_INFINITY;
  }

  private now(): number {
    return this.dependencies.now?.() ?? Date.now();
  }

  private waiting(): boolean {
    return (this.dependencies.waits ?? rateLimitWaits).isWaiting(orderListRateLimit);
  }
}
