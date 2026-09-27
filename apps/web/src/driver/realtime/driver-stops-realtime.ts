import {
  createDriverConnection,
  mapDriverAggregateVersions,
  type DriverEvents,
  type DriverStopCursor,
} from "../../realtime";
import { realtimeCredentials } from "@/auth/request-credentials";
import type { DriverSession } from "../session/driver-session";

export interface DriverStopsRealtimeCallbacks {
  readonly refreshFromSignal: () => void;
  readonly resynchronizeFromRest: () => Promise<readonly DriverStopCursor[]>;
  readonly refreshExternalOffersFromSignal?: () => void;
  readonly resynchronizeExternalOffersFromRest?: () => Promise<void>;
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
      ExternalOfferChanged: callbacks.refreshExternalOffersFromSignal,
    } satisfies Pick<
      { [K in keyof DriverEvents]?: () => void },
      "AssignmentChanged" | "OrderStatusChanged" | "ExternalOfferChanged"
    >;

    return createDriverConnection(
      {
        baseUrl,
        organizationId: session.organizationId as never,
        ...realtimeCredentials(session),
        resynchronizeFromRest: async () => {
          const [stops] = await Promise.all([
            callbacks.resynchronizeFromRest(),
            callbacks.resynchronizeExternalOffersFromRest?.(),
          ]);
          return { aggregate_versions: mapDriverAggregateVersions(stops) };
        },
        onReconnecting: () => callbacks.stateChanged("reconnecting"),
        onReconnected: () => callbacks.stateChanged("reconnecting"),
        onResynchronizationError: () => callbacks.stateChanged("offline"),
      },
      relevantHandlers,
    );
  },
};
