export const realtimeEventTypes = {
  orderStatusChanged: "OrderStatusChanged.v1",
  orderTimelineEventAdded: "OrderTimelineEventAdded.v1",
  assignmentChanged: "AssignmentChanged.v1",
  routeChanged: "RouteChanged.v1",
  incidentCreated: "IncidentCreated.v1",
  externalOfferChanged: "ExternalOfferChanged.v1",
  notificationStatusChanged: "NotificationStatusChanged.v1",
  driverLocationUpdated: "DriverLocationUpdated.v1",
  publicOrderStatusChanged: "PublicOrderStatusChanged.v1",
  publicEtaChanged: "PublicEtaChanged.v1",
} as const;

export type RealtimeEventType =
  (typeof realtimeEventTypes)[keyof typeof realtimeEventTypes];
