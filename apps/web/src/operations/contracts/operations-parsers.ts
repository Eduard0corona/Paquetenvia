import {
  orderStatuses,
  type OperationsAssignmentSummary,
  type OperationsClientSummary,
  type OperationsDashboardOrder,
  type OperationsDashboardResponse,
  type OperationsCostWarning,
  type OperationsDriverLocation,
  type OperationsMoney,
  type OperationsOrderDetail,
  type OperationsOrderTimelineItem,
  type OperationsOrganizationContext,
  type OperationsOrganizationSummary,
  type OperationsTimeWindow,
  type OperationsZoneSummary,
  type OrderStatus,
} from "./operations-dashboard";

const uuidPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const driverReferencePattern = /^DRV-[0-9a-f]{8}$/;
const statusSet = new Set<string>(orderStatuses);
const serviceTypes = new Set(["SAME_DAY", "URGENT", "SCHEDULED_ROUTE"]);
const costWarnings = new Set([
  "AUTHORIZED_OVERRIDE",
  "BELOW_MINIMUM_SNAPSHOT",
]);
const zoneTypes = new Set(["CORE", "STANDARD", "EXTENDED", "EXCLUDED"]);
const assignmentTypes = new Set(["OWN", "EXTERNAL", "ALLY_CAPACITY"]);

export class OperationsContractError extends Error {
  public constructor() {
    super("La respuesta de operaciones no cumple el contrato.");
    this.name = "OperationsContractError";
  }
}

export function parseOperationsDashboard(
  value: unknown,
): OperationsDashboardResponse {
  const object = exactObject(value, ["generated_at", "items", "next_cursor"]);
  const items = array(object.items);
  if (items.length > 100) fail();
  return {
    generated_at: utc(object.generated_at),
    items: items.map(parseDashboardOrder),
    next_cursor:
      object.next_cursor === null
        ? null
        : boundedString(object.next_cursor, 1, 512),
  };
}

export function parseOperationsOrderDetail(
  value: unknown,
): OperationsOrderDetail {
  const object = exactObject(value, [
    "id",
    "public_id",
    "owner_org_id",
    "operator_org_id",
    "status",
    "price_net",
    "version",
    "origin_location_id",
    "destination_location_id",
    "service_type",
    "quote_id",
    "city_id",
    "service_area_id",
    "pricing_tier",
    "total",
    "claim_window_ends_at",
    "finalized_at",
    "service_window",
    "timeline",
  ]);
  const timeline = array(object.timeline);
  if (timeline.length > 1000) fail();
  return {
    id: uuid(object.id),
    public_id: boundedString(object.public_id, 1, 128),
    owner_org_id: uuid(object.owner_org_id),
    operator_org_id:
      object.operator_org_id === null ? null : uuid(object.operator_org_id),
    status: status(object.status),
    price_net: money(object.price_net),
    version: positiveInteger(object.version),
    origin_location_id: uuid(object.origin_location_id),
    destination_location_id: uuid(object.destination_location_id),
    service_type: serviceType(object.service_type),
    quote_id: uuid(object.quote_id),
    city_id: uuid(object.city_id),
    service_area_id:
      object.service_area_id === null ? null : uuid(object.service_area_id),
    pricing_tier: boundedString(object.pricing_tier, 1, 64),
    total: money(object.total),
    claim_window_ends_at:
      object.claim_window_ends_at === null
        ? null
        : utc(object.claim_window_ends_at),
    finalized_at:
      object.finalized_at === null ? null : utc(object.finalized_at),
    service_window:
      object.service_window === null ? null : serviceWindow(object.service_window),
    timeline: timeline.map(parseTimeline),
  };
}

export function parseOrganizationContexts(
  value: unknown,
): readonly OperationsOrganizationContext[] {
  const contexts = array(value);
  if (contexts.length > 100) fail();
  return contexts.map((item) => {
    const object = exactObject(item, [
      "organization_id",
      "display_name",
      "role",
      "is_default",
    ]);
    return {
      organization_id: uuid(object.organization_id),
      display_name: boundedString(object.display_name, 1, 200),
      role: boundedString(object.role, 1, 64),
      is_default: boolean(object.is_default),
    };
  });
}

function parseDashboardOrder(value: unknown): OperationsDashboardOrder {
  const object = exactObject(value, [
    "order_id",
    "aggregate_version",
    "public_id",
    "owner",
    "operator",
    "client",
    "status",
    "created_at",
    "updated_at",
    "service_type",
    "pickup_window",
    "delivery_window",
    "delivery_zone",
    "assignment",
    "latest_driver_location",
    "cost_warning",
    "unassigned_alert",
  ]);
  return {
    order_id: uuid(object.order_id),
    aggregate_version: positiveInteger(object.aggregate_version),
    public_id: boundedString(object.public_id, 1, 128),
    owner: organization(object.owner),
    operator: object.operator === null ? null : organization(object.operator),
    client: object.client === null ? null : client(object.client),
    status: status(object.status),
    created_at: utc(object.created_at),
    updated_at: utc(object.updated_at),
    service_type: serviceType(object.service_type),
    pickup_window:
      object.pickup_window === null ? null : timeWindow(object.pickup_window),
    delivery_window:
      object.delivery_window === null
        ? null
        : timeWindow(object.delivery_window),
    delivery_zone:
      object.delivery_zone === null ? null : zone(object.delivery_zone),
    assignment:
      object.assignment === null ? null : assignment(object.assignment),
    latest_driver_location:
      object.latest_driver_location === null
        ? null
        : location(object.latest_driver_location),
    cost_warning:
      object.cost_warning === null
        ? null
        : oneOf<OperationsCostWarning>(object.cost_warning, costWarnings),
    unassigned_alert: boolean(object.unassigned_alert),
  };
}

function organization(value: unknown): OperationsOrganizationSummary {
  const object = exactObject(value, ["organization_id", "display_name"]);
  return {
    organization_id: uuid(object.organization_id),
    display_name: boundedString(object.display_name, 1, 200),
  };
}

function client(value: unknown): OperationsClientSummary {
  const object = exactObject(value, ["client_account_id", "display_name"]);
  return {
    client_account_id: uuid(object.client_account_id),
    display_name: boundedString(object.display_name, 1, 200),
  };
}

function zone(value: unknown): OperationsZoneSummary {
  const object = exactObject(value, [
    "operating_zone_id",
    "name",
    "zone_type",
  ]);
  return {
    operating_zone_id: uuid(object.operating_zone_id),
    name: boundedString(object.name, 1, 200),
    zone_type: oneOf(object.zone_type, zoneTypes),
  };
}

function assignment(value: unknown): OperationsAssignmentSummary {
  const object = exactObject(value, [
    "assignment_id",
    "assignment_type",
    "status",
    "driver_id",
    "driver_reference",
  ]);
  const reference = boundedString(object.driver_reference, 12, 12);
  if (!driverReferencePattern.test(reference)) fail();
  return {
    assignment_id: uuid(object.assignment_id),
    assignment_type: oneOf(object.assignment_type, assignmentTypes),
    status: boundedString(object.status, 1, 32),
    driver_id: uuid(object.driver_id),
    driver_reference: reference,
  };
}

function location(value: unknown): OperationsDriverLocation {
  const object = exactObject(value, [
    "lat",
    "lng",
    "accuracy_m",
    "captured_at",
  ]);
  const lat = finite(object.lat);
  const lng = finite(object.lng);
  const accuracy = finite(object.accuracy_m);
  if (lat < -90 || lat > 90 || lng < -180 || lng > 180 || accuracy < 0) fail();
  return {
    lat,
    lng,
    accuracy_m: accuracy,
    captured_at: utc(object.captured_at),
  };
}

function timeWindow(value: unknown): OperationsTimeWindow {
  const object = exactObject(value, ["from", "to"]);
  const from = utc(object.from);
  const to = utc(object.to);
  if (Date.parse(from) > Date.parse(to)) fail();
  return { from, to };
}

/** ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02: an order's own window has from strictly before to. */
function serviceWindow(value: unknown): OperationsTimeWindow {
  const window = timeWindow(value);
  if (Date.parse(window.from) >= Date.parse(window.to)) fail();
  return window;
}

function parseTimeline(value: unknown): OperationsOrderTimelineItem {
  const object = exactObject(value, ["event_type", "occurred_at"]);
  return {
    event_type: boundedString(object.event_type, 1, 100),
    occurred_at: utc(object.occurred_at),
  };
}

function money(value: unknown): OperationsMoney {
  const object = exactObject(value, ["currency", "amount_cents"]);
  if (object.currency !== "MXN") fail();
  const amount = nonNegativeInteger(object.amount_cents);
  return { currency: "MXN", amount_cents: amount };
}

function exactObject(
  value: unknown,
  keys: readonly string[],
): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) fail();
  const object = value as Record<string, unknown>;
  const actual = Object.keys(object).sort();
  const expected = [...keys].sort();
  if (
    actual.length !== expected.length ||
    actual.some((key, index) => key !== expected[index])
  )
    fail();
  return object;
}

function array(value: unknown): readonly unknown[] {
  if (!Array.isArray(value)) fail();
  return value;
}

function boundedString(value: unknown, minimum: number, maximum: number): string {
  if (
    typeof value !== "string" ||
    value.length < minimum ||
    value.length > maximum
  )
    fail();
  return value;
}

function uuid(value: unknown): string {
  const text = boundedString(value, 36, 36);
  if (!uuidPattern.test(text) || text === "00000000-0000-0000-0000-000000000000")
    fail();
  return text;
}

function utc(value: unknown): string {
  const text = boundedString(value, 20, 40);
  if (
    !(text.endsWith("Z") || text.endsWith("+00:00")) ||
    Number.isNaN(Date.parse(text))
  )
    fail();
  return text;
}

function status(value: unknown): OrderStatus {
  return oneOf(value, statusSet) as OrderStatus;
}

function serviceType(value: unknown) {
  return oneOf(value, serviceTypes) as
    | "SAME_DAY"
    | "URGENT"
    | "SCHEDULED_ROUTE";
}

function oneOf<T extends string>(value: unknown, values: Set<string>): T {
  if (typeof value !== "string" || !values.has(value)) fail();
  return value as T;
}

function positiveInteger(value: unknown): number {
  if (!Number.isSafeInteger(value) || (value as number) < 1) fail();
  return value as number;
}

function nonNegativeInteger(value: unknown): number {
  if (!Number.isSafeInteger(value) || (value as number) < 0) fail();
  return value as number;
}

function finite(value: unknown): number {
  if (typeof value !== "number" || !Number.isFinite(value)) fail();
  return value;
}

function boolean(value: unknown): boolean {
  if (typeof value !== "boolean") fail();
  return value;
}

function fail(): never {
  throw new OperationsContractError();
}
