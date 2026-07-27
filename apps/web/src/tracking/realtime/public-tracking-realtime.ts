import { createTrackingConnection } from "../../realtime/tracking-connection";
import type { ManagedRealtimeConnection } from "../../realtime/base-connection";
import type { PublicTrackingProjection } from "../contracts/public-tracking";

export interface PublicTrackingRealtimeOptions {
  readonly baseUrl: string;
  readonly token: string;
  readonly projection: PublicTrackingProjection;
  readonly refresh: () => Promise<PublicTrackingProjection | null>;
  readonly onSignal: () => void;
  readonly onReconnecting: () => void;
  readonly onReconnected: () => void;
  readonly onDisconnected: () => void;
}

export function createPublicTrackingRealtime(
  options: PublicTrackingRealtimeOptions,
): ManagedRealtimeConnection {
  return createTrackingConnection(
    {
      baseUrl: options.baseUrl,
      tokenFactory: () => options.token,
      expectedPublicOrderId: options.projection.public_id,
      suppressLogging: true,
      onReconnecting: options.onReconnecting,
      onReconnected: options.onReconnected,
      onResynchronizationError: options.onDisconnected,
      resynchronizeFromRest: async () => {
        const projection = await options.refresh();
        return {
          aggregate_versions:
            projection === null
              ? {}
              : { [projection.public_id]: projection.aggregate_version },
        };
      },
    },
    {
      PublicOrderStatusChanged: options.onSignal,
      PublicEtaChanged: options.onSignal,
    },
  );
}
