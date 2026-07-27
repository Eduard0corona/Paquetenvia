import type { Uuid } from "./envelope";

export type RealtimeTokenFactory = () => string | Promise<string>;

export interface RestSynchronizationSnapshot {
  readonly aggregate_versions: Readonly<Record<string, number>>;
}

export type RestResynchronizer = () => Promise<RestSynchronizationSnapshot>;

export interface BaseRealtimeConnectionOptions {
  readonly baseUrl: string;
  readonly tokenFactory: RealtimeTokenFactory;
  readonly resynchronizeFromRest: RestResynchronizer;
  readonly reconnectDelaysMilliseconds?: readonly number[];
  readonly onReconnecting?: (error?: Error) => void;
  readonly onReconnected?: (connectionId?: string) => void;
  readonly onResynchronized?: () => void;
  readonly onResynchronizationError?: (error: unknown) => void;
  readonly suppressLogging?: boolean;
}

export interface PrivateRealtimeConnectionOptions
  extends BaseRealtimeConnectionOptions {
  readonly organizationId: Uuid;
}

export interface TrackingRealtimeConnectionOptions
  extends BaseRealtimeConnectionOptions {
  readonly expectedPublicOrderId?: string;
}

export const defaultReconnectDelaysMilliseconds = [0, 2_000, 10_000, 30_000] as const;
