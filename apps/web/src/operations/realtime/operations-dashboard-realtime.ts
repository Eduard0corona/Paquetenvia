import { createOperationsConnection } from "@/realtime/operations-connection";
import type { RestSynchronizationSnapshot } from "@/realtime/connection-options";
import type { ManagedRealtimeConnection } from "@/realtime/base-connection";
import type { Uuid } from "@/realtime/envelope";
import type { OperationsSession } from "../session/operations-session";
import { realtimeCredentials } from "@/auth/request-credentials";

export interface OperationsRealtimeCallbacks {
  refreshOperations(): void;
  refreshLocation(): void;
  resynchronize(): Promise<RestSynchronizationSnapshot>;
  reconnecting(): void;
  connected(): void;
  unavailable(): void;
}

export function createOperationsDashboardRealtime(
  apiBaseUrl: string,
  session: OperationsSession,
  callbacks: OperationsRealtimeCallbacks,
): ManagedRealtimeConnection {
  return createOperationsConnection(
    {
      baseUrl: apiBaseUrl,
      organizationId: session.organizationId as Uuid,
      ...realtimeCredentials(session),
      resynchronizeFromRest: callbacks.resynchronize,
      onReconnecting: callbacks.reconnecting,
      onResynchronized: callbacks.connected,
      onResynchronizationError: callbacks.unavailable,
      suppressLogging: true,
    },
    {
      OrderStatusChanged: callbacks.refreshOperations,
      OrderTimelineEventAdded: callbacks.refreshOperations,
      AssignmentChanged: callbacks.refreshOperations,
      DriverLocationUpdated: callbacks.refreshLocation,
      RouteChanged: callbacks.refreshOperations,
      IncidentCreated: callbacks.refreshOperations,
      ExternalOfferChanged: callbacks.refreshOperations,
    },
  );
}
