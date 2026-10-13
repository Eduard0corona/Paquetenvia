import type { RestSynchronizationSnapshot } from "@/realtime/connection-options";
import { OperationsApiError } from "../api/operations-api";
import {
  inboxQueries,
  inboxViewKey,
  isPriceReviewRow,
  type InboxView,
} from "../contracts/inbox";
import { ContractViolationError } from "../contracts/strict-json";
import type {
  OperationsDashboardFilters,
  OperationsDashboardOrder,
  OperationsDashboardResponse,
} from "../contracts/operations-dashboard";
import type { OperationsQueueCounts } from "../contracts/queue-counts";
import type { OperationsSession } from "../session/operations-session";
import {
  AuthoritativeRefreshCoordinator,
  RefreshScopeChangedError,
  type RefreshRequirement,
} from "./authoritative-refresh-coordinator";
import { ExternalStore } from "./external-store";
import { inboxAggregateVersions, mergeInboxSources } from "./inbox-merge";
import { QueueCountsLoader } from "./queue-counts-loader";
import { ScreenReloads, type ReloadTimers } from "./screen-reloads";
import { isRateLimited } from "./tenant-error-messages";
import {
  rateLimitWaits,
  sleepUntilWaitEnds,
  type RateLimitedOperation,
  type RateLimitWaits,
} from "../../lib/rate-limit-wait";

/**
 * SEC-004: the inbox reads the dashboard and the counts, each with its own budget (owner answer Q3). A wait for
 * either pauses the whole inbox (owner answer Q16, "Toda la pantalla").
 */
export const inboxRateLimit: readonly RateLimitedOperation[] = ["dashboard", "queue_counts"];
const countsRateLimit: readonly RateLimitedOperation[] = ["queue_counts"];

/** The debounce of a realtime notice before its reload, as before SEC-004 (then spaced by 3 s, owner answer Q9). */
export const inboxRealtimeDebounceMilliseconds = 250;

/**
 * UI-PHASE3-INBOX-2026-10-10: state of the dispatcher's work inbox ("Bandeja de trabajo").
 *
 * REST is the only source of truth: every load reads the operations dashboard queries of the
 * selected view and the real server counts again; realtime events, reconnects, polling and
 * visibility only ask for such a load. Everything lives in memory and a session or
 * organization change starts from an empty state.
 */
export interface InboxApi {
  list(filters: OperationsDashboardFilters, signal?: AbortSignal): Promise<OperationsDashboardResponse>;
  queueCounts(signal?: AbortSignal): Promise<OperationsQueueCounts>;
}

export interface InboxDependencies {
  readonly readSession: () => OperationsSession | null;
  readonly createApi: (session: OperationsSession) => InboxApi;
  /** Realtime, polling and visibility loads wait while the page is hidden. */
  readonly isHidden?: () => boolean;
  /** SEC-004: the tab's server waits (tests pass their own). */
  readonly waits?: RateLimitWaits;
  /** The clock and timers of the reload spacing and the wait (tests pass fake ones). */
  readonly timers?: Partial<ReloadTimers>;
}

export type InboxPhase = "loading" | "no_session" | "ready" | "access_unavailable";

export type InboxTrigger =
  | "initial"
  | "view"
  | "manual"
  | "assignment"
  | "realtime"
  | "polling"
  | "visibility"
  | "reconnect";

const backgroundTriggers: ReadonlySet<InboxTrigger> = new Set(["realtime", "polling", "visibility"]);

export interface InboxZoneOption {
  readonly id: string;
  readonly label: string;
}

export interface InboxState {
  readonly phase: InboxPhase;
  readonly view: InboxView;
  readonly rows: readonly OperationsDashboardOrder[];
  readonly hasMore: boolean;
  /** The current view was answered at least once (tells an empty queue from a pending one). */
  readonly loaded: boolean;
  readonly loading: boolean;
  readonly loadingMore: boolean;
  readonly error: string | null;
  readonly lastUpdated: string | null;
  /** UI-PHASE2-QUEUE-COUNTS-2026-10-05: real server counts, never the loaded rows. */
  readonly counts: OperationsQueueCounts | null;
  readonly countsUnavailable: boolean;
  /** Delivery zones seen in this session, for the zone chips. */
  readonly zones: readonly InboxZoneOption[];
  /** Changes with every session or organization change. */
  readonly sessionGeneration: number;
}

export const inboxLoadFailedMessage = "No fue posible actualizar la bandeja.";
export const inboxPaginationFailedMessage = "No fue posible cargar más órdenes. Actualiza la bandeja.";

interface Source {
  readonly filters: OperationsDashboardFilters;
  readonly items: readonly OperationsDashboardOrder[];
  readonly nextCursor: string | null;
  readonly seenCursors: ReadonlySet<string>;
}

type LoadResult =
  | { readonly kind: "replace"; readonly queries: readonly OperationsDashboardFilters[]; readonly pages: readonly OperationsDashboardResponse[] }
  | { readonly kind: "append"; readonly indexes: readonly number[]; readonly pages: readonly OperationsDashboardResponse[] };

class CursorCycleError extends Error {
  public constructor() {
    super("The dashboard cursor repeated.");
    this.name = "CursorCycleError";
  }
}

export class InboxController extends ExternalStore<InboxState> {
  private api: InboxApi | null = null;
  private sources: readonly Source[] = [];
  private scope = 0;
  private readonly zoneNames = new Map<string, string>();
  private coordinator = new AuthoritativeRefreshCoordinator<LoadResult>();
  private readonly counts: QueueCountsLoader;
  /** Owner answers Q9, Q11, Q14 and Q16: realtime spacing, the server's wait and the one reload when it ends. */
  private readonly reloads: ScreenReloads<"realtime">;
  /** Aborted with the scope: a reconnect waiting out the server's wait stops waiting. */
  private scopeAbort = new AbortController();

  public constructor(
    private readonly dependencies: InboxDependencies,
    view: InboxView,
  ) {
    super({
      phase: "loading",
      view,
      rows: [],
      hasMore: false,
      loaded: false,
      loading: false,
      loadingMore: false,
      error: null,
      lastUpdated: null,
      counts: null,
      countsUnavailable: false,
      zones: [],
      sessionGeneration: 0,
    });
    this.counts = new QueueCountsLoader((view) =>
      this.update({ counts: view.counts, countsUnavailable: view.unavailable }),
    );
    this.reloads = new ScreenReloads({
      operations: inboxRateLimit,
      waits: dependencies.waits,
      timers: dependencies.timers,
      reload: () => void this.load("replace", "realtime", "normal"),
    });
  }

  /**
   * A realtime notice (OperationsHub): gathered into at most one reload every 3 seconds, after the existing 250 ms
   * debounce, and none during the server's wait (owner answer Q9).
   */
  public notice(): void {
    if (this.api === null || this.getSnapshot().phase === "access_unavailable") return;
    this.reloads.notice("realtime", inboxRealtimeDebounceMilliseconds);
  }

  /**
   * Starts (again) for the installed session: drops everything of the previous one, then
   * loads the current view. Resolves with the session it started and the resulting phase.
   */
  public async start(): Promise<{ readonly session: OperationsSession | null; readonly phase: InboxPhase }> {
    this.clearSession();
    const session = this.dependencies.readSession();
    if (session === null) {
      this.update({ phase: "no_session" });
      return { session: null, phase: "no_session" };
    }
    this.api = this.dependencies.createApi(session);
    this.update({ phase: "loading" });
    await this.load("replace", "initial", "normal");
    return { session, phase: this.getSnapshot().phase };
  }

  /** Leaves the screen: cancels every request and forgets the session's data. */
  public stop(): void {
    this.clearSession();
    this.update({ phase: "no_session" });
  }

  /** The URL selected another queue or filter: load it from scratch. */
  public setView(view: InboxView): void {
    if (inboxViewKey(view) === inboxViewKey(this.getSnapshot().view)) return;
    this.newScope();
    this.sources = [];
    this.update({ view, rows: [], hasMore: false, loaded: false, error: null, loading: false, loadingMore: false });
    if (this.api !== null && this.getSnapshot().phase !== "access_unavailable") void this.load("replace", "view", "normal");
  }

  public async refresh(trigger: InboxTrigger): Promise<void> {
    await this.load("replace", trigger, "normal");
  }

  public async loadMore(): Promise<void> {
    const state = this.getSnapshot();
    if (!state.hasMore || state.loadingMore) return;
    await this.load("append", "manual", "normal");
  }

  /**
   * Realtime reconnect: a REST read that always runs (after any read in flight) and whose
   * versions feed the realtime resynchronization; it fails when the scope changed.
   */
  public async resynchronize(): Promise<RestSynchronizationSnapshot> {
    // SEC-004: the mandatory read waits out the server's wait instead of meeting the same 429 and dropping the hub.
    const scope = this.scope;
    await sleepUntilWaitEnds(this.waits(), inboxRateLimit, this.scopeAbort.signal);
    if (scope !== this.scope) throw new RefreshScopeChangedError();
    const result = await this.load("replace", "reconnect", "mandatory-reconnect");
    if (result === null) throw new RefreshScopeChangedError();
    return { aggregate_versions: inboxAggregateVersions(this.sources) };
  }

  private clearSession(): void {
    this.newScope();
    this.api = null;
    this.sources = [];
    this.zoneNames.clear();
    this.counts.reset();
    this.update({
      phase: "loading",
      rows: [],
      hasMore: false,
      loaded: false,
      loading: false,
      loadingMore: false,
      error: null,
      lastUpdated: null,
      counts: null,
      countsUnavailable: false,
      zones: [],
      sessionGeneration: this.getSnapshot().sessionGeneration + 1,
    });
  }

  private newScope(): void {
    this.scope += 1;
    this.reloads.cancel();
    this.scopeAbort.abort();
    this.scopeAbort = new AbortController();
    this.coordinator.cancel();
    this.coordinator = new AuthoritativeRefreshCoordinator<LoadResult>();
  }

  private async load(
    kind: LoadResult["kind"],
    trigger: InboxTrigger,
    requirement: RefreshRequirement,
  ): Promise<LoadResult | null> {
    const api = this.api;
    const scope = this.scope;
    const coordinator = this.coordinator;
    if (api === null || this.getSnapshot().phase === "access_unavailable") {
      if (requirement === "mandatory-reconnect") throw new RefreshScopeChangedError();
      return null;
    }
    if (requirement === "normal" && backgroundTriggers.has(trigger) && (this.dependencies.isHidden?.() ?? false))
      return null;
    // SEC-004 (owner answers Q11, Q14, Q16): during the server's wait nothing is sent, whatever asked (polling,
    // realtime, "Actualizar", a view change); the inbox reads once, by itself, with the view on screen when it ends.
    if (requirement === "normal" && !this.reloads.admit("realtime")) return null;
    const isCurrent = () => this.scope === scope && this.api === api;
    try {
      return await coordinator.request({
        requirement,
        isCurrent,
        execute: async (signal) => {
          this.reloads.reloaded();
          if (kind === "replace") this.update({ loading: true });
          else this.update({ loadingMore: true });
          try {
            if (kind === "replace") {
              const queries = inboxQueries(this.getSnapshot().view);
              const pages = await Promise.all(queries.map((filters) => api.list(filters, signal)));
              return { kind, queries, pages };
            }
            const indexes = this.sources.flatMap((source, index) => (source.nextCursor === null ? [] : [index]));
            const pages = await Promise.all(
              indexes.map((index) =>
                api.list({ ...this.sources[index].filters, cursor: this.sources[index].nextCursor ?? undefined }, signal),
              ),
            );
            return { kind, indexes, pages };
          } finally {
            if (isCurrent()) this.update(kind === "replace" ? { loading: false } : { loadingMore: false });
          }
        },
        apply: (result) => this.apply(result),
      });
    } catch (caught: unknown) {
      if (caught instanceof RefreshScopeChangedError || !isCurrent()) {
        if (requirement === "mandatory-reconnect") throw caught;
        return null;
      }
      const category = caught instanceof OperationsApiError ? caught.category : "contract";
      if (category === "unauthorized" || category === "forbidden") {
        this.newScope();
        this.sources = [];
        this.counts.reset();
        this.update({
          phase: "access_unavailable",
          rows: [],
          hasMore: false,
          loaded: false,
          loading: false,
          loadingMore: false,
          error: null,
          lastUpdated: null,
        });
      } else if (isRateLimited(caught)) {
        // SEC-004: the common message counts down, the rows stay, and the inbox reads once when the wait ends.
        this.update({ phase: "ready", error: null });
        this.reloads.rateLimited("realtime");
      } else {
        this.update({
          phase: "ready",
          error: kind === "append" || caught instanceof CursorCycleError ? inboxPaginationFailedMessage : inboxLoadFailedMessage,
        });
      }
      if (requirement === "mandatory-reconnect") throw caught;
      return null;
    }
  }

  private apply(result: LoadResult): void {
    // UI-PHASE3-INBOX-PRICE-REVIEW-BULK-2026-10-11: a price review query lists only orders with a
    // cost warning that have not finished; any other row means the filter was not applied, so the
    // read fails closed instead of showing it.
    result.pages.forEach((page, pageIndex) => {
      const filters =
        result.kind === "replace" ? result.queries[pageIndex] : this.sources[result.indexes[pageIndex]].filters;
      if (filters.priceReview === true && !page.items.every(isPriceReviewRow)) throw new ContractViolationError();
    });
    let sources: Source[];
    if (result.kind === "replace") {
      sources = result.queries.map((filters, index) => ({
        filters,
        items: [...result.pages[index].items],
        nextCursor: result.pages[index].next_cursor,
        seenCursors: new Set(result.pages[index].next_cursor === null ? [] : [result.pages[index].next_cursor]),
      }));
    } else {
      sources = [...this.sources];
      // Validate every page before changing anything: a repeated cursor would page forever.
      result.indexes.forEach((sourceIndex, pageIndex) => {
        const cursor = result.pages[pageIndex].next_cursor;
        if (cursor !== null && sources[sourceIndex].seenCursors.has(cursor)) throw new CursorCycleError();
      });
      result.indexes.forEach((sourceIndex, pageIndex) => {
        const source = sources[sourceIndex];
        const page = result.pages[pageIndex];
        sources[sourceIndex] = {
          filters: source.filters,
          items: [...source.items, ...page.items],
          nextCursor: page.next_cursor,
          seenCursors: page.next_cursor === null ? source.seenCursors : new Set([...source.seenCursors, page.next_cursor]),
        };
      });
    }
    this.sources = sources;
    for (const page of result.pages) {
      for (const item of page.items) {
        if (item.delivery_zone !== null) this.zoneNames.set(item.delivery_zone.operating_zone_id, item.delivery_zone.name);
      }
    }
    const merged = mergeInboxSources(sources);
    const generatedAt = result.pages
      .map((page) => page.generated_at)
      .reduce<string | null>((latest, value) => (latest === null || Date.parse(value) > Date.parse(latest) ? value : latest), null);
    this.update({
      phase: "ready",
      rows: merged.rows,
      hasMore: merged.hasMore,
      loaded: true,
      error: null,
      lastUpdated: generatedAt ?? this.getSnapshot().lastUpdated,
      zones: [...this.zoneNames]
        .map(([id, label]) => ({ id, label }))
        .sort((left, right) => left.label.localeCompare(right.label, "es-MX") || (left.id < right.id ? -1 : 1)),
    });
    // The counts are read again with every list read, exactly like the dashboard.
    if (result.kind === "replace") void this.loadCounts();
  }

  /** SEC-004: a 429 on the counts pauses the whole inbox, which reads everything once when it ends (Q16). */
  private async loadCounts(): Promise<void> {
    const api = this.api;
    if (api === null) return;
    if (this.waits().isWaiting(countsRateLimit)) {
      this.reloads.rateLimited("realtime");
      return;
    }
    const outcome = await this.counts.load((signal) => api.queueCounts(signal));
    if (outcome === "rate_limited" && this.api === api) this.reloads.rateLimited("realtime");
  }

  private waits(): RateLimitWaits {
    return this.dependencies.waits ?? rateLimitWaits;
  }
}
