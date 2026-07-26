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
import type { DriverStop } from "../contracts/driver-stop";
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
}

export interface DriverStopsControllerOptions {
  readonly baseUrl: string;
  readonly session: DriverSession;
  readonly api: DriverStopsApi;
  readonly cache: DriverStopsCache;
  readonly realtimeFactory: DriverStopsRealtimeFactory;
  readonly telemetry: DriverStopsTelemetry;
  readonly now?: () => Date;
  readonly debounceMilliseconds?: number;
}

const initialState: DriverStopsViewState = Object.freeze({
  phase: "loading",
  stops: [],
  synchronizedAt: null,
  realtime: "updated",
  refreshing: false,
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
      const stops = await this.options.api.listStops(request.signal);
      if (this.disposed || request.signal.aborted) {
        return [];
      }
      const synchronizedAt = this.now().toISOString();
      const snapshot: DriverStopsSnapshot = Object.freeze({
        schemaVersion: DriverStopsSchemaVersion,
        synchronizedAt,
        stops,
      });
      await this.options.cache.replaceSnapshot(this.partition, snapshot);
      this.options.telemetry.loadCompleted("rest", driverStopCountBucket(stops.length));
      this.setState({
        phase: stops.length === 0 ? "empty" : "ready",
        stops,
        synchronizedAt,
        realtime: "updated",
        refreshing: false,
      });
      await this.ensureRealtime();
      return stops;
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
      await this.stopRealtime();
      if (this.partition) {
        await this.options.cache.clearPartition(this.partition).catch(() => undefined);
      }
      this.setState({
        phase: error.category,
        stops: [],
        synchronizedAt: null,
        realtime: "offline",
        refreshing: false,
      });
      return;
    }
    if (error.category === "invalid-contract") {
      this.setState({
        phase: "invalid-contract",
        stops: [],
        synchronizedAt: null,
        realtime: "offline",
        refreshing: false,
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
      });
    } else {
      this.setState({
        phase: "offline-empty",
        stops: [],
        synchronizedAt: null,
        realtime: "offline",
        refreshing: false,
      });
    }
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
