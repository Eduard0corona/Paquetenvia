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
 * UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: a confirmation read
 * (`details=false`) never wipes the details the phone already holds. A stop
 * keeps them while it is still the same stop (same order and stop type); a
 * stop the server no longer returns leaves the list, details included; a new
 * stop, or one that now heads to another place, has none until the next full
 * read, which the caller schedules.
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
   * without wiping the details it holds. A disposed controller (logout) or an
   * aborted pass never answers with an empty list: it throws "cancelled", so
   * no queued action is ever taken for missing.
   */
  public async refreshForSync(
    signal?: AbortSignal,
  ): Promise<readonly DriverStop[]> {
    if (this.disposed || !this.partition || signal?.aborted) {
      throw new DriverStopsApiError("cancelled");
    }
    const list = await this.options.api.listStops(signal, { details: false });
    if (this.disposed || signal?.aborted) {
      throw new DriverStopsApiError("cancelled");
    }
    this.reportRejected(list);
    const merged = mergeConfirmedStops(this.state.stops, list.stops);
    await this.acceptAuthoritativeStops(merged.stops, list.completedToday);
    if (merged.missingDetails) this.scheduleRefresh();
    return list.stops;
  }

  public async revokeAccess(
    category: "unauthorized" | "forbidden",
  ): Promise<void> {
    await this.stopRealtime();
    this.abortController?.abort();
    if (this.partition) {
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

    try {
      const list = await this.options.api.listStops(request.signal);
      if (this.disposed || request.signal.aborted) {
        return [];
      }
      this.reportRejected(list);
      await this.acceptAuthoritativeStops(list.stops, list.completedToday);
      await this.ensureRealtime();
      return list.stops;
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
      if (this.abortController === request) {
        this.abortController = null;
      }
    }
  }

  private async handleApiError(error: DriverStopsApiError): Promise<void> {
    this.options.telemetry.loadFailed(error.category);
    if (error.category === "cancelled") {
      return;
    }
    if (error.category === "unauthorized" || error.category === "forbidden") {
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
    completedToday: number,
  ): Promise<void> {
    if (!this.partition || this.disposed) return;
    const synchronizedAt = this.now().toISOString();
    const snapshot: DriverStopsSnapshot = Object.freeze({
      schemaVersion: DriverStopsSchemaVersion,
      synchronizedAt,
      stops,
      completedToday,
    });
    await this.options.cache.replaceSnapshot(this.partition, snapshot);
    this.options.telemetry.loadCompleted(
      "rest",
      driverStopCountBucket(stops.length),
    );
    this.setState({
      phase: stops.length === 0 ? "empty" : "ready",
      stops,
      synchronizedAt,
      realtime: "updated",
      refreshing: false,
      completedToday,
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
