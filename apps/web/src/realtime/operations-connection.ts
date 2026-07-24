import type {
  OperationsEvents,
  RealtimeEventHandlers,
} from "./contracts";
import {
  buildManagedConnection,
  buildPrivateHubUrl,
  type ManagedRealtimeConnection,
} from "./base-connection";
import type { PrivateRealtimeConnectionOptions } from "./connection-options";

export function createOperationsConnection(
  options: PrivateRealtimeConnectionOptions,
  handlers: RealtimeEventHandlers<OperationsEvents>,
): ManagedRealtimeConnection {
  const { connection, managed, guard } = buildManagedConnection(
    buildPrivateHubUrl(options.baseUrl, "/hubs/operations", options.organizationId),
    options,
  );

  connection.on("OrderStatusChanged", (event: OperationsEvents["OrderStatusChanged"]) => {
    if (guard.shouldApply(event)) handlers.OrderStatusChanged?.(event);
  });
  connection.on(
    "OrderTimelineEventAdded",
    (event: OperationsEvents["OrderTimelineEventAdded"]) => {
      if (guard.shouldApply(event)) handlers.OrderTimelineEventAdded?.(event);
    },
  );
  connection.on("AssignmentChanged", (event: OperationsEvents["AssignmentChanged"]) => {
    if (guard.shouldApply(event)) handlers.AssignmentChanged?.(event);
  });
  connection.on("RouteChanged", (event: OperationsEvents["RouteChanged"]) => {
    if (guard.shouldApply(event)) handlers.RouteChanged?.(event);
  });
  connection.on("IncidentCreated", (event: OperationsEvents["IncidentCreated"]) => {
    if (guard.shouldApply(event)) handlers.IncidentCreated?.(event);
  });
  connection.on(
    "ExternalOfferChanged",
    (event: OperationsEvents["ExternalOfferChanged"]) => {
      if (guard.shouldApply(event)) handlers.ExternalOfferChanged?.(event);
    },
  );
  connection.on(
    "NotificationStatusChanged",
    (event: OperationsEvents["NotificationStatusChanged"]) => {
      if (guard.shouldApply(event)) handlers.NotificationStatusChanged?.(event);
    },
  );
  connection.on(
    "DriverLocationUpdated",
    (event: OperationsEvents["DriverLocationUpdated"]) => {
      if (guard.shouldApply(event)) handlers.DriverLocationUpdated?.(event);
    },
  );
  return managed;
}
