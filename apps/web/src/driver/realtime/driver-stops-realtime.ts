import {
  createDriverConnection,
  mapDriverAggregateVersions,
  type DriverEvents,
  type DriverStopCursor,
} from "../../realtime";
import type { DriverSession } from "../session/driver-session";

export interface DriverStopsRealtimeCallbacks {
  readonly refreshFromSignal: () => void;
  readonly resynchronizeFromRest: () => Promise<readonly DriverStopCursor[]>;
  readonly stateChanged: (state: "updated" | "reconnecting" | "offline") => void;
}

export interface DriverStopsRealtimeFactory {
  create(
    baseUrl: string,
    session: DriverSession,
    callbacks: DriverStopsRealtimeCallbacks,
  ): DriverStopsRealtimeConnection;
}

export interface DriverStopsRealtimeConnection {
  start(): Promise<void>;
  stop(): Promise<void>;
}

export const defaultDriverStopsRealtimeFactory: DriverStopsRealtimeFactory = {
  create(baseUrl, session, callbacks) {
    const relevantHandlers = {
      AssignmentChanged: callbacks.refreshFromSignal,
      OrderStatusChanged: callbacks.refreshFromSignal,
    } satisfies Pick<
      { [K in keyof DriverEvents]?: () => void },
      "AssignmentChanged" | "OrderStatusChanged"
    >;

    return createDriverConnection(
      {
        baseUrl,
        organizationId: session.organizationId as never,
        tokenFactory: session.getAccessToken,
        resynchronizeFromRest: async () => ({
          aggregate_versions: mapDriverAggregateVersions(
            await callbacks.resynchronizeFromRest(),
          ),
        }),
        onReconnecting: () => callbacks.stateChanged("reconnecting"),
        onReconnected: () => callbacks.stateChanged("reconnecting"),
        onResynchronizationError: () => callbacks.stateChanged("offline"),
      },
      relevantHandlers,
    );
  },
};
