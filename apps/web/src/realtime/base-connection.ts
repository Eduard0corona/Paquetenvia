import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from "@microsoft/signalr";
import type {
  BaseRealtimeConnectionOptions,
  RestSynchronizationSnapshot,
} from "./connection-options";
import { defaultReconnectDelaysMilliseconds } from "./connection-options";
import { RealtimeEventGuard } from "./deduplication";

export class ManagedRealtimeConnection {
  public constructor(
    private readonly connection: HubConnection,
    private readonly eventGuard: RealtimeEventGuard,
  ) {}

  public get state(): HubConnectionState {
    return this.connection.state;
  }

  public start(): Promise<void> {
    return this.connection.start();
  }

  public async stop(): Promise<void> {
    await this.connection.stop();
    this.eventGuard.clear();
  }
}

export function buildManagedConnection(
  url: string,
  options: BaseRealtimeConnectionOptions,
): {
  readonly connection: HubConnection;
  readonly managed: ManagedRealtimeConnection;
  readonly guard: RealtimeEventGuard;
} {
  assertOptions(options);
  const reconnectDelays =
    options.reconnectDelaysMilliseconds ?? defaultReconnectDelaysMilliseconds;
  const guard = new RealtimeEventGuard();
  const connection = new HubConnectionBuilder()
    .withUrl(url, { accessTokenFactory: options.tokenFactory })
    .withAutomaticReconnect([...reconnectDelays])
    .configureLogging(LogLevel.Warning)
    .build();

  connection.onreconnected(async () => {
    try {
      await resynchronizeAfterReconnect(guard, options.resynchronizeFromRest);
    } catch (error: unknown) {
      options.onResynchronizationError?.(error);
      await connection.stop();
    }
  });

  return {
    connection,
    managed: new ManagedRealtimeConnection(connection, guard),
    guard,
  };
}

export function buildPrivateHubUrl(
  baseUrl: string,
  path: "/hubs/operations" | "/hubs/driver",
  organizationId: string,
): string {
  const url = buildHubUrl(baseUrl, path);
  url.searchParams.set("organization_id", organizationId);
  return url.toString();
}

export function buildHubUrl(
  baseUrl: string,
  path: "/hubs/operations" | "/hubs/driver" | "/hubs/tracking",
): URL {
  const base = new URL(baseUrl);
  if (base.protocol !== "https:" && base.protocol !== "http:") {
    throw new Error("Realtime baseUrl must use HTTP or HTTPS.");
  }

  return new URL(path, base);
}

export async function resynchronizeAfterReconnect(
  guard: RealtimeEventGuard,
  resynchronizeFromRest: () => Promise<RestSynchronizationSnapshot>,
): Promise<void> {
  const snapshot = await resynchronizeFromRest();
  guard.replaceAggregateVersions(snapshot.aggregate_versions);
}

function assertOptions(options: BaseRealtimeConnectionOptions): void {
  if (typeof options.tokenFactory !== "function") {
    throw new Error("A token callback is required.");
  }

  if (typeof options.resynchronizeFromRest !== "function") {
    throw new Error("A REST resynchronization callback is required.");
  }

  const delays =
    options.reconnectDelaysMilliseconds ?? defaultReconnectDelaysMilliseconds;
  if (
    delays.length < 1 ||
    delays.length > 8 ||
    delays[0] !== 0 ||
    delays.some(
      (delay) =>
        !Number.isInteger(delay) || delay < 0 || delay > 60_000,
    )
  ) {
    throw new Error("Reconnect delays must be bounded and start at zero.");
  }
}
