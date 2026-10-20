import {
  DriverStopsApiError,
  type DriverStopsApi,
} from "../api/driver-stops-api";
import {
  createDriverCachePartition,
  DriverStopsCacheError,
  DriverStopsSchemaVersion,
  type DriverCachePartition,
  type DriverStopsCache,
  type DriverStopsSnapshot,
} from "../cache/driver-stops-cache";
import type { DriverStop, DriverStopList } from "../contracts/driver-stop";
import type {
  DriverStopsRealtimeFactory,
} from "../realtime/driver-stops-realtime";
import type { DriverSession } from "../session/driver-session";
import {
  driverStopCountBucket,
  type DriverStopsTelemetry,
} from "../telemetry/driver-stops-telemetry";

export type DriverStopsPhase =
  | "session-unavailable"
  | "loading"
  | "ready"
  | "empty"
  | "offline"
  | "offline-empty"
  /**
   * DRV-SESSION-EXPIRY-OFFLINE-2026-10-19 (owner answer E2): the session
   * ended (401). Everything saved for the partition stays; the screen asks
   * the driver to sign in again.
   */
  | "session-ended"
  | "unauthorized"
  | "forbidden"
  | "recoverable-error"
  | "invalid-contract";

export interface DriverStopsViewState {
  readonly phase: DriverStopsPhase;
  readonly stops: readonly DriverStop[];
  readonly synchronizedAt: string | null;
  readonly realtime: "updated" | "reconnecting" | "offline";
  readonly refreshing: boolean;
  /**
   * UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11 ("Hoy · N paradas · K hechas"):
   * the driver's stops completed on the Mazatlán day of `synchronizedAt`, as
   * the server counted them; null when unknown.
   */
  readonly completedToday: number | null;
}

/**
 * UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: a read that brings a stop without
 * its details (a confirmation read with `details=false`, or a full read while
 * the server cannot read them) never wipes the details the phone already
 * holds for it. A stop keeps them while it is still the same stop (same order
 * and stop type); a stop the server no longer returns leaves the list, details
 * included; a new stop, or one that now heads to another place, has none until
 * a full read brings them. `missingDetails` tells whether any stop is left
 * without details.
 */
export function mergeConfirmedStops(
  previous: readonly DriverStop[],
  confirmed: readonly DriverStop[],
): { readonly stops: readonly DriverStop[]; readonly missingDetails: boolean } {
  const known = new Map(previous.map((stop) => [stop.order_id, stop]));
  let missingDetails = false;
  const stops = confirmed.map((stop): DriverStop => {
    const earlier = known.get(stop.order_id);
    if (
      earlier &&
      earlier.stop_type === stop.stop_type &&
      earlier.details_available
    ) {
      return Object.freeze({
        ...stop,
        details_available: true,
        contact_short_name: earlier.contact_short_name,
        contact_full_name: earlier.contact_full_name,
        navigation: earlier.navigation,
      });
    }
    if (!stop.details_available) missingDetails = true;
    return stop;
  });
  return { stops: Object.freeze(stops), missingDetails };
}

/**
 * DRV-STALE-STOP-READ-2026-10-14 (owner answers Q6 "Sí, en un PR aparte
 * (Recommended)" and Q6b "Se aplica, como hoy (Recommended)"): a read that was
 * already on its way when the app accepted a newer version of a stop is stale
 * for that stop. `newerWhileInFlight` holds, per order, the highest version the
 * app accepted from other reads while this one was on its way. A stale stop is
 * not taken: the stop the app holds stays as it is, and a stop that already
 * left the list is not brought back. Q6c "Solo paradas ya vistas
 * (Recommended)": when another read removed a stop the app held, it counts as
 * accepted at the held version + 1 (see acceptRead), so this read does not
 * bring it back at the held version or lower. An equal version of a held stop,
 * a stop the read does not bring (it leaves the list, as DRV-001 says) and any
 * read sent after the newer version was accepted (also after a version reset)
 * behave as before.
 */
export function withoutStaleStops(
  held: readonly DriverStop[],
  incoming: readonly DriverStop[],
  newerWhileInFlight: ReadonlyMap<string, number>,
): { readonly stops: readonly DriverStop[]; readonly taken: readonly DriverStop[] } {
  if (newerWhileInFlight.size === 0) return { stops: incoming, taken: incoming };
  const holding = new Map(held.map((stop) => [stop.order_id, stop]));
  const stops: DriverStop[] = [];
  const taken: DriverStop[] = [];
  for (const stop of incoming) {
    const newer = newerWhileInFlight.get(stop.order_id);
    if (newer === undefined || newer <= stop.aggregate_version) {
      stops.push(stop);
      taken.push(stop);
      continue;
    }
    const current = holding.get(stop.order_id);
    if (current) stops.push(current);
  }
  return { stops, taken };
}

/** One stops read on its way; see withoutStaleStops. */
interface StopsRead {
  /** When it was sent among this controller's stops reads (Q6d). */
  readonly sentOrder: number;
  readonly newerWhileInFlight: Map<string, number>;
}

/** Today's completed count and the send order of the read that brought it. */
interface CompletedTodayRead {
  readonly sentOrder: number;
  readonly completedToday: number;
}

/** Keeps, per order, the highest version recorded for a read on its way. */
function recordNewer(read: StopsRead, orderId: string, version: number): void {
  const newer = read.newerWhileInFlight.get(orderId);
  if (newer === undefined || newer < version) {
    read.newerWhileInFlight.set(orderId, version);
  }
}

export interface DriverStopsControllerOptions {
  readonly baseUrl: string;
  readonly session: DriverSession;
  readonly api: DriverStopsApi;
  readonly cache: DriverStopsCache;
  readonly realtimeFactory: DriverStopsRealtimeFactory;
  readonly telemetry: DriverStopsTelemetry;
  readonly refreshExternalOffersFromSignal?: () => void;
  readonly resynchronizeExternalOffersFromRest?: () => Promise<void>;
  readonly now?: () => Date;
  readonly debounceMilliseconds?: number;
}

const initialState: DriverStopsViewState = Object.freeze({
  phase: "loading",
  stops: [],
  synchronizedAt: null,
  realtime: "updated",
  refreshing: false,
  completedToday: null,
});

export class DriverStopsController {
  private readonly listeners = new Set<(state: DriverStopsViewState) => void>();
  private readonly now: () => Date;
  private readonly debounceMilliseconds: number;
  private state: DriverStopsViewState = initialState;
  private partition: DriverCachePartition | null = null;
  private abortController: AbortController | null = null;
  private realtimeConnection: ReturnType<
    DriverStopsRealtimeFactory["create"]
  > | null = null;
  private refreshTimer: ReturnType<typeof setTimeout> | null = null;
  private disposed = false;
  /** Every stops read on its way (full and confirmation reads). */
  private readonly readsInFlight = new Set<StopsRead>();
  /** Stops reads sent so far, full and confirmation reads alike. */
  private readsSent = 0;
  /**
   * DRV-STALE-STOP-READ-2026-10-14, Q6d "Corregir en este PR (Recommended)":
   * the read whose `completedToday` is shown and saved. A read sent before it
   * leaves that count as it is; null until a read is taken (the saved
   * snapshot shown on opening is not a read).
   */
  private shownCompletedToday: CompletedTodayRead | null = null;
  /**
   * Answers are taken one at a time, in the order they arrive: each one is
   * compared with, and merged into, what the previous one left, and the
   * snapshot and the state are written in that same order.
   */
  private acceptance: Promise<void> = Promise.resolve();

  public constructor(private readonly options: DriverStopsControllerOptions) {
    this.now = options.now ?? (() => new Date());
    this.debounceMilliseconds = options.debounceMilliseconds ?? 250;
  }

  public get current(): DriverStopsViewState {
    return this.state;
  }

  public subscribe(listener: (state: DriverStopsViewState) => void): () => void {
    this.listeners.add(listener);
    listener(this.state);
    return () => this.listeners.delete(listener);
  }

  public async start(): Promise<void> {
    try {
      this.partition = await createDriverCachePartition(this.options.session);
    } catch {
      this.setState({ ...initialState, phase: "recoverable-error" });
      return;
    }
    await this.refresh("initial");
  }

  public async retry(): Promise<void> {
    await this.refresh("manual");
  }

  /**
   * The read the sync scheduler confirms queued actions with
   * (UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11): without protected details, so
   * it never depends on the key service, and merged into the saved snapshot
   * without wiping the details it holds; a stop left without details gets a
   * full read. Like a full read, it never takes a stop back to a version
   * older than one accepted while it was on its way (withoutStaleStops); the
   * scheduler still gets the answer as REST gave it. A disposed controller
   * (logout) or an aborted pass never answers with an empty list: it throws
   * "cancelled", so no queued action is ever taken for missing. Before the
   * controller started it throws "recoverable", so the action is retried.
   */
  public async refreshForSync(signal?: AbortSignal): Promise<DriverStopList> {
    if (this.disposed || signal?.aborted) {
      throw new DriverStopsApiError("cancelled");
    }
    if (!this.partition) {
      throw new DriverStopsApiError("recoverable");
    }
    const read = this.beginRead();
    try {
      const list = await this.options.api.listStops(signal, { details: false });
      if (this.disposed || signal?.aborted) {
        throw new DriverStopsApiError("cancelled");
      }
      this.reportRejected(list);
      const merged = await this.acceptRead(
        read,
        list,
        () => this.disposed || signal?.aborted === true,
      );
      if (merged === null) {
        throw new DriverStopsApiError("cancelled");
      }
      if (merged.missingDetails) this.scheduleRefresh();
      // The scheduler confirms with what REST answered (DRV-002), also when
      // the screen kept a newer version of a stop.
      return list;
    } finally {
      this.readsInFlight.delete(read);
    }
  }

  /**
   * Stops reading and realtime for this session. A 403 (`forbidden`) or a
   * session without a usable credential (`unauthorized`) also wipes the
   * partition's snapshot, roles and dispatch contact; a 401
   * (`session-ended`, DRV-SESSION-EXPIRY-OFFLINE-2026-10-19) keeps them, so
   * the same driver finds them again after signing in.
   */
  public async revokeAccess(
    category: "session-ended" | "unauthorized" | "forbidden",
  ): Promise<void> {
    await this.stopRealtime();
    this.abortController?.abort();
    if (this.partition && category !== "session-ended") {
      await this.options.cache.clearPartition(this.partition).catch(() => undefined);
    }
    this.setState({
      phase: category,
      stops: [],
      synchronizedAt: null,
      realtime: "offline",
      refreshing: false,
      completedToday: null,
    });
  }

  public scheduleRefresh(): void {
    if (this.disposed || this.refreshTimer !== null) {
      return;
    }
    this.refreshTimer = setTimeout(() => {
      this.refreshTimer = null;
      void this.refresh("signal");
    }, this.debounceMilliseconds);
  }

  public async dispose(): Promise<void> {
    this.disposed = true;
    this.abortController?.abort();
    if (this.refreshTimer !== null) {
      clearTimeout(this.refreshTimer);
      this.refreshTimer = null;
    }
    if (this.realtimeConnection) {
      await this.realtimeConnection.stop().catch(() => undefined);
      this.realtimeConnection = null;
    }
    this.listeners.clear();
  }

  private async refresh(
    cause: "initial" | "manual" | "signal" | "reconnect",
  ): Promise<readonly DriverStop[]> {
    if (this.disposed || !this.partition) {
      return [];
    }

    this.abortController?.abort();
    const request = new AbortController();
    this.abortController = request;
    this.setState({
      ...this.state,
      phase:
        cause === "initial" || this.state.stops.length === 0
          ? "loading"
          : this.state.phase,
      refreshing: cause !== "initial",
    });

    const read = this.beginRead();
    try {
      const list = await this.options.api.listStops(request.signal);
      if (this.disposed || request.signal.aborted) {
        return [];
      }
      this.reportRejected(list);
      // A full read never schedules another one, even when some details are
      // still unavailable; the next confirmation or signal tries again.
      const merged = await this.acceptRead(
        read,
        list,
        () => this.disposed || request.signal.aborted,
      );
      if (merged === null) {
        return [];
      }
      await this.ensureRealtime();
      return merged.stops;
    } catch (error) {
      if (this.disposed || request.signal.aborted) {
        return [];
      }
      if (error instanceof DriverStopsApiError) {
        await this.handleApiError(error);
      } else {
        await this.showRecoverableFallback();
      }
      return [];
    } finally {
      this.readsInFlight.delete(read);
      if (this.abortController === request) {
        this.abortController = null;
      }
    }
  }

  private beginRead(): StopsRead {
    this.readsSent += 1;
    const read: StopsRead = {
      sentOrder: this.readsSent,
      newerWhileInFlight: new Map(),
    };
    this.readsInFlight.add(read);
    return read;
  }

  /**
   * Takes one answer after the ones that arrived before it (see
   * `acceptance`): drops what is stale in it (withoutStaleStops), keeps the
   * details the phone holds (mergeConfirmedStops), saves the snapshot, shows
   * the stops and then tells every other read still on its way which versions
   * were accepted and which held stops were removed. Today's count is taken
   * unless the read was sent before the one whose count is shown (Q6d). Resolves
   * null, without taking anything, when the read was cancelled meanwhile.
   */
  private acceptRead(
    read: StopsRead,
    list: DriverStopList,
    cancelled: () => boolean,
  ): Promise<ReturnType<typeof mergeConfirmedStops> | null> {
    const accepted = this.acceptance.then(async () => {
      if (cancelled()) return null;
      const held = this.state.stops;
      const current = withoutStaleStops(
        held,
        list.stops,
        read.newerWhileInFlight,
      );
      const merged = mergeConfirmedStops(
        await this.knownStops(current.stops),
        current.stops,
      );
      if (cancelled()) return null;
      // Q6d: a read sent before the one whose count is shown keeps that count,
      // lower or higher; a read sent after it always brings its own.
      const shown = this.shownCompletedToday;
      const completedToday =
        shown !== null && read.sentOrder < shown.sentOrder
          ? shown
          : { sentOrder: read.sentOrder, completedToday: list.completedToday };
      await this.acceptAuthoritativeStops(merged.stops, completedToday);
      // Q6c: a held stop this read no longer brings counts as removed only when
      // this read left after the app accepted it (no entry of its own for it).
      // A removal can keep the order's version (D8 closes the assignment after
      // a reschedule) and a new assignment bumps it, so the other reads on
      // their way record it at the held version + 1.
      const kept = new Set(current.stops.map((stop) => stop.order_id));
      const removed = held.filter(
        (stop) =>
          !kept.has(stop.order_id) &&
          !read.newerWhileInFlight.has(stop.order_id),
      );
      for (const other of this.readsInFlight) {
        if (other === read) continue;
        for (const stop of current.taken) {
          recordNewer(other, stop.order_id, stop.aggregate_version);
        }
        for (const stop of removed) {
          recordNewer(other, stop.order_id, stop.aggregate_version + 1);
        }
      }
      return merged;
    });
    this.acceptance = accepted.then(
      () => undefined,
      () => undefined,
    );
    return accepted;
  }

  private async handleApiError(error: DriverStopsApiError): Promise<void> {
    this.options.telemetry.loadFailed(error.category);
    if (error.category === "cancelled") {
      return;
    }
    if (
      error.category === "session-ended" ||
      error.category === "unauthorized" ||
      error.category === "forbidden"
    ) {
      await this.revokeAccess(error.category);
      return;
    }
    if (error.category === "invalid-contract") {
      this.setState({
        phase: "invalid-contract",
        stops: [],
        synchronizedAt: null,
        realtime: "offline",
        refreshing: false,
        completedToday: null,
      });
      return;
    }
    await this.showRecoverableFallback();
  }

  private async showRecoverableFallback(): Promise<void> {
    let snapshot: DriverStopsSnapshot | null = null;
    try {
      snapshot = this.partition
        ? await this.options.cache.readSnapshot(this.partition)
        : null;
    } catch (error) {
      if (!(error instanceof DriverStopsCacheError)) {
        throw error;
      }
    }

    if (snapshot) {
      this.options.telemetry.loadCompleted(
        "offline",
        driverStopCountBucket(snapshot.stops.length),
      );
      this.setState({
        phase: snapshot.stops.length === 0 ? "offline-empty" : "offline",
        stops: snapshot.stops,
        synchronizedAt: snapshot.synchronizedAt,
        realtime: "offline",
        refreshing: false,
        completedToday: snapshot.completedToday,
      });
    } else {
      this.setState({
        phase: "offline-empty",
        stops: [],
        synchronizedAt: null,
        realtime: "offline",
        refreshing: false,
        completedToday: null,
      });
    }
  }

  /**
   * The stops whose details a read without them may keep: the ones shown,
   * or, before anything is shown (the app just opened), the ones saved on the
   * phone for this session, so a first read while the details cannot be read
   * never wipes the saved ones. The snapshot is read only when needed.
   */
  private async knownStops(
    incoming: readonly DriverStop[],
  ): Promise<readonly DriverStop[]> {
    if (
      this.state.stops.length > 0 ||
      !this.partition ||
      incoming.every((stop) => stop.details_available)
    ) {
      return this.state.stops;
    }
    try {
      return (await this.options.cache.readSnapshot(this.partition))?.stops ?? [];
    } catch {
      return [];
    }
  }

  private reportRejected(list: DriverStopList): void {
    if (list.droppedCount > 0) {
      this.options.telemetry.stopsRejected?.(
        "dropped",
        driverStopCountBucket(list.droppedCount),
      );
    }
    if (list.degradedCount > 0) {
      this.options.telemetry.stopsRejected?.(
        "degraded",
        driverStopCountBucket(list.degradedCount),
      );
    }
  }

  private async acceptAuthoritativeStops(
    stops: readonly DriverStop[],
    count: CompletedTodayRead,
  ): Promise<void> {
    if (!this.partition || this.disposed) return;
    const synchronizedAt = this.now().toISOString();
    const snapshot: DriverStopsSnapshot = Object.freeze({
      schemaVersion: DriverStopsSchemaVersion,
      synchronizedAt,
      stops,
      completedToday: count.completedToday,
    });
    await this.options.cache.replaceSnapshot(this.partition, snapshot);
    this.options.telemetry.loadCompleted(
      "rest",
      driverStopCountBucket(stops.length),
    );
    this.shownCompletedToday = count;
    this.setState({
      phase: stops.length === 0 ? "empty" : "ready",
      stops,
      synchronizedAt,
      realtime: "updated",
      refreshing: false,
      completedToday: count.completedToday,
    });
  }

  private async ensureRealtime(): Promise<void> {
    if (this.realtimeConnection || this.disposed) {
      return;
    }
    this.realtimeConnection = this.options.realtimeFactory.create(
      this.options.baseUrl,
      this.options.session,
      {
        refreshFromSignal: () => this.scheduleRefresh(),
        resynchronizeFromRest: async () => {
          const stops = await this.refresh("reconnect");
          return stops;
        },
        refreshExternalOffersFromSignal:
          this.options.refreshExternalOffersFromSignal,
        resynchronizeExternalOffersFromRest:
          this.options.resynchronizeExternalOffersFromRest,
        stateChanged: (realtime) => {
          this.options.telemetry.realtimeStateChanged(realtime);
          this.setState({ ...this.state, realtime });
        },
      },
    );
    try {
      await this.realtimeConnection.start();
      this.setState({ ...this.state, realtime: "updated" });
    } catch {
      this.options.telemetry.realtimeStateChanged("offline");
      this.setState({ ...this.state, realtime: "offline" });
      await this.stopRealtime();
    }
  }

  private async stopRealtime(): Promise<void> {
    const connection = this.realtimeConnection;
    this.realtimeConnection = null;
    if (connection) {
      await connection.stop().catch(() => undefined);
    }
  }

  private setState(state: DriverStopsViewState): void {
    if (this.disposed) {
      return;
    }
    this.state = Object.freeze(state);
    for (const listener of this.listeners) {
      listener(this.state);
    }
  }
}
