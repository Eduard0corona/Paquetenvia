import type { PublicOrderId, RealtimeEnvelope, Uuid } from "./envelope";

export interface OrderStatusChangedPayload {
  readonly order_id: Uuid;
  readonly previous_status: string;
  readonly new_status: string;
  readonly occurred_at: string;
}

export interface PublicOrderStatusChangedPayload {
  readonly public_order_id: PublicOrderId;
  readonly public_status:
    | "CREATED"
    | "SCHEDULED"
    | "IN_TRANSIT"
    | "OUT_FOR_DELIVERY"
    | "DELIVERY_EXCEPTION"
    | "DELIVERED"
    | "RETURNING"
    | "RETURNED"
    | "CANCELLED";
  readonly occurred_at: string;
}

export interface OrderTimelineEventAddedPayload {
  readonly order_id: Uuid;
  readonly timeline_event_id: Uuid;
  readonly category: string;
  readonly summary: string;
  readonly occurred_at: string;
}

export interface AssignmentChangedPayload {
  readonly order_id: Uuid;
  readonly assignment_id: Uuid;
  readonly driver_id: Uuid;
  readonly assignment_status: string;
  readonly occurred_at: string;
}

export interface RouteChangedPayload {
  readonly route_id: Uuid;
  readonly route_version: number;
  readonly changed_stop_ids: readonly Uuid[];
  readonly occurred_at: string;
}

export interface DriverLocationUpdatedPayload {
  readonly driver_id: Uuid;
  readonly lat: number;
  readonly lng: number;
  readonly accuracy_m: number;
  readonly captured_at: string;
}

export interface IncidentCreatedPayload {
  readonly incident_id: Uuid;
  readonly order_id: Uuid;
  readonly type: string;
  readonly severity: string;
  readonly status: string;
  readonly occurred_at: string;
}

export interface ExternalOfferChangedPayload {
  readonly offer_id: Uuid;
  readonly status: string;
  readonly commission_cents: number;
  readonly expires_at: string;
}

export interface NotificationStatusChangedPayload {
  readonly notification_id: Uuid;
  readonly channel: string;
  readonly status: string;
  readonly attempts: number;
  readonly occurred_at: string;
}

export interface PublicEtaChangedPayload {
  readonly public_order_id: PublicOrderId;
  readonly eta_from: string;
  readonly eta_to: string;
  readonly updated_at: string;
}

export interface OperationsEvents {
  readonly OrderStatusChanged: RealtimeEnvelope<Uuid, OrderStatusChangedPayload>;
  readonly OrderTimelineEventAdded: RealtimeEnvelope<Uuid, OrderTimelineEventAddedPayload>;
  readonly AssignmentChanged: RealtimeEnvelope<Uuid, AssignmentChangedPayload>;
  readonly RouteChanged: RealtimeEnvelope<Uuid, RouteChangedPayload>;
  readonly IncidentCreated: RealtimeEnvelope<Uuid, IncidentCreatedPayload>;
  readonly ExternalOfferChanged: RealtimeEnvelope<Uuid, ExternalOfferChangedPayload>;
  readonly NotificationStatusChanged: RealtimeEnvelope<Uuid, NotificationStatusChangedPayload>;
  readonly DriverLocationUpdated: RealtimeEnvelope<Uuid, DriverLocationUpdatedPayload>;
}

export interface DriverEvents {
  readonly AssignmentChanged: RealtimeEnvelope<Uuid, AssignmentChangedPayload>;
  readonly RouteChanged: RealtimeEnvelope<Uuid, RouteChangedPayload>;
  readonly OrderStatusChanged: RealtimeEnvelope<Uuid, OrderStatusChangedPayload>;
  readonly ExternalOfferChanged: RealtimeEnvelope<Uuid, ExternalOfferChangedPayload>;
}

export interface TrackingEvents {
  readonly PublicOrderStatusChanged: RealtimeEnvelope<
    PublicOrderId,
    PublicOrderStatusChangedPayload
  >;
  readonly PublicEtaChanged: RealtimeEnvelope<PublicOrderId, PublicEtaChangedPayload>;
}

export type RealtimeEventHandlers<TEvents> = {
  readonly [TEventName in keyof TEvents]?: (event: TEvents[TEventName]) => void;
};
