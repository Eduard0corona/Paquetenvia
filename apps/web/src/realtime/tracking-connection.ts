import type { RealtimeEventHandlers, TrackingEvents } from "./contracts";
import {
  buildHubUrl,
  buildManagedConnection,
  type ManagedRealtimeConnection,
} from "./base-connection";
import type { TrackingRealtimeConnectionOptions } from "./connection-options";

export function createTrackingConnection(
  options: TrackingRealtimeConnectionOptions,
  handlers: RealtimeEventHandlers<TrackingEvents>,
): ManagedRealtimeConnection {
  const { connection, managed, guard } = buildManagedConnection(
    buildHubUrl(options.baseUrl, "/hubs/tracking").toString(),
    options,
  );

  connection.on(
    "PublicOrderStatusChanged",
    (event: TrackingEvents["PublicOrderStatusChanged"]) => {
      if (
        options.expectedPublicOrderId !== undefined &&
        event.payload.public_order_id !== options.expectedPublicOrderId
      ) {
        return;
      }

      if (guard.shouldApply(event)) handlers.PublicOrderStatusChanged?.(event);
    },
  );
  connection.on("PublicEtaChanged", (event: TrackingEvents["PublicEtaChanged"]) => {
    if (
      options.expectedPublicOrderId !== undefined &&
      event.payload.public_order_id !== options.expectedPublicOrderId
    ) {
      return;
    }

    if (guard.shouldApply(event)) handlers.PublicEtaChanged?.(event);
  });
  return managed;
}
