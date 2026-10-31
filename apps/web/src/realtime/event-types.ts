export const realtimeEventTypes = {
  orderStatusChanged: "OrderStatusChanged.v1",
  orderTimelineEventAdded: "OrderTimelineEventAdded.v1",
  assignmentChanged: "AssignmentChanged.v1",
  routeChanged: "RouteChanged.v1",
  incidentCreated: "IncidentCreated.v1",
  externalOfferChanged: "ExternalOfferChanged.v1",
  notificationStatusChanged: "NotificationStatusChanged.v1",
  // NTF-RECIPIENT-NOTICES-2026-10-20 (AI-12 decided amendment): a recipient notice carries order_id and notice.
  notificationStatusChangedV2: "NotificationStatusChanged.v2",
  driverLocationUpdated: "DriverLocationUpdated.v1",
  publicOrderStatusChanged: "PublicOrderStatusChanged.v1",
  publicEtaChanged: "PublicEtaChanged.v1",
} as const;

export type RealtimeEventType =
  (typeof realtimeEventTypes)[keyof typeof realtimeEventTypes];
