import type { DriverEvents, RealtimeEventHandlers } from "./contracts";
import {
  buildManagedConnection,
  buildPrivateHubUrl,
  type ManagedRealtimeConnection,
} from "./base-connection";
import type { PrivateRealtimeConnectionOptions } from "./connection-options";

export function createDriverConnection(
  options: PrivateRealtimeConnectionOptions,
  handlers: RealtimeEventHandlers<DriverEvents>,
): ManagedRealtimeConnection {
  const { connection, managed, guard } = buildManagedConnection(
    buildPrivateHubUrl(options.baseUrl, "/hubs/driver", options.organizationId),
    options,
  );

  connection.on("AssignmentChanged", (event: DriverEvents["AssignmentChanged"]) => {
    if (guard.shouldApply(event)) handlers.AssignmentChanged?.(event);
  });
  connection.on("RouteChanged", (event: DriverEvents["RouteChanged"]) => {
    if (guard.shouldApply(event)) handlers.RouteChanged?.(event);
  });
  connection.on("OrderStatusChanged", (event: DriverEvents["OrderStatusChanged"]) => {
    if (guard.shouldApply(event)) handlers.OrderStatusChanged?.(event);
  });
  connection.on(
    "ExternalOfferChanged",
    (event: DriverEvents["ExternalOfferChanged"]) => {
      if (guard.shouldApply(event)) handlers.ExternalOfferChanged?.(event);
    },
  );
  return managed;
}
