export const orderStatuses = [
  "DRAFT",
  "CONFIRMED",
  "READY_FOR_PICKUP",
  "ASSIGNED",
  "AT_PICKUP",
  "PICKED_UP",
  "IN_TRANSIT",
  "DELIVERING",
  "FAILED_ATTEMPT",
  "RESCHEDULED",
  "RETURNING",
  "RETURNED",
  "DELIVERED",
  "CLOSED",
  "CLAIM_OPEN",
  "CLAIM_RESOLVED",
  "CANCELLED",
] as const;

export type OrderStatus = (typeof orderStatuses)[number];
export type OperationsServiceType =
  | "SAME_DAY"
  | "URGENT"
  | "SCHEDULED_ROUTE";
export type OperationsCostWarning =
  | "AUTHORIZED_OVERRIDE"
  | "BELOW_MINIMUM_SNAPSHOT";

export interface OperationsOrganizationSummary {
  readonly organization_id: string;
  readonly display_name: string;
}

export interface OperationsClientSummary {
  readonly client_account_id: string;
  readonly display_name: string;
}

export interface OperationsZoneSummary {
  readonly operating_zone_id: string;
  readonly name: string;
  readonly zone_type: "CORE" | "STANDARD" | "EXTENDED" | "EXCLUDED";
}

export interface OperationsAssignmentSummary {
  readonly assignment_id: string;
  readonly assignment_type: "OWN" | "EXTERNAL" | "ALLY_CAPACITY";
  readonly status: string;
  readonly driver_id: string;
  readonly driver_reference: string;
}

export interface OperationsDriverLocation {
  readonly lat: number;
  readonly lng: number;
  readonly accuracy_m: number;
  readonly captured_at: string;
}

export interface OperationsTimeWindow {
  readonly from: string;
  readonly to: string;
}

export interface OperationsDashboardOrder {
  readonly order_id: string;
  readonly aggregate_version: number;
  readonly public_id: string;
  readonly owner: OperationsOrganizationSummary;
  readonly operator: OperationsOrganizationSummary | null;
  readonly client: OperationsClientSummary | null;
  readonly status: OrderStatus;
  readonly created_at: string;
  readonly updated_at: string;
  readonly service_type: OperationsServiceType;
  readonly pickup_window: OperationsTimeWindow | null;
  readonly delivery_window: OperationsTimeWindow | null;
  readonly delivery_zone: OperationsZoneSummary | null;
  readonly assignment: OperationsAssignmentSummary | null;
  readonly latest_driver_location: OperationsDriverLocation | null;
  readonly cost_warning: OperationsCostWarning | null;
  readonly unassigned_alert: boolean;
}

export interface OperationsDashboardResponse {
  readonly generated_at: string;
  readonly items: readonly OperationsDashboardOrder[];
  readonly next_cursor: string | null;
}

export interface OperationsDashboardFilters {
  readonly orderId?: string;
  readonly status?: OrderStatus;
  readonly deliveryZoneId?: string;
  readonly clientAccountId?: string;
  readonly ownerOrganizationId?: string;
  readonly operatorOrganizationId?: string;
  readonly serviceType?: OperationsServiceType;
  readonly createdFrom?: string;
  readonly createdTo?: string;
  readonly unassigned?: boolean;
  readonly cursor?: string;
}

export interface OperationsOrderTimelineItem {
  readonly event_type: string;
  readonly occurred_at: string;
}

export interface OperationsMoney {
  readonly currency: string;
  readonly amount_cents: number;
}

export interface OperationsOrderDetail {
  readonly id: string;
  readonly public_id: string;
  readonly owner_org_id: string;
  readonly operator_org_id: string | null;
  readonly status: OrderStatus;
  readonly price_net: OperationsMoney;
  readonly version: number;
  readonly origin_location_id: string;
  readonly destination_location_id: string;
  readonly service_type: OperationsServiceType;
  readonly quote_id: string;
  readonly city_id: string;
  readonly service_area_id: string | null;
  readonly pricing_tier: string;
  readonly total: OperationsMoney;
  readonly claim_window_ends_at: string | null;
  readonly finalized_at: string | null;
  readonly timeline: readonly OperationsOrderTimelineItem[];
}

export interface OperationsOrganizationContext {
  readonly organization_id: string;
  readonly display_name: string;
  readonly role: string;
  readonly is_default: boolean;
}
