import { externalOfferVehicleLabels, type ExternalOfferVehicleType } from "./external-offer-confirmation";
import { formatMxnCentsWithCurrency } from "./money";
import {
  array,
  boolean,
  boundedString,
  exactObject,
  fail,
  integer,
  isCanonicalUuid,
  nullable,
  oneOf,
  uuid,
} from "./strict-json";

/**
 * UI-PHASE2-DRIVER-PICKER-2026-10-05: AI-05 listAssignableDrivers (AssignableDriverPage)
 * and the assignDriver request and Assignment response. Every reader fails closed on the
 * first value outside the contract. A driver is shown only by its non-personal
 * `driver_reference` (AI-06 stores no driver name); the id is sent, never shown.
 */
export const vehicleTypes = ["MOTORCYCLE", "CAR", "VAN", "BICYCLE", "WALKER"] as const;
export type VehicleType = (typeof vehicleTypes)[number];

export const ineligibilityReasons = [
  "DRIVER_UNAVAILABLE",
  "DRIVER_STATUS_NOT_ACTIVE",
  "USER_NOT_ACTIVE",
  "DRIVER_MEMBERSHIP_NOT_ACTIVE",
  "HOME_CITY_MISMATCH",
  "SERVICE_AREA_REQUIRED",
  "SERVICE_AREA_NOT_ELIGIBLE",
  "DOCUMENT_POLICY_UNAVAILABLE",
  "REQUIRED_DOCUMENT_MISSING",
  "DOCUMENT_STATUS_NOT_VALID",
  "DOCUMENT_EXPIRED",
  "DOCUMENT_EXPIRY_MISSING",
  "DOCUMENT_HASH_INVALID",
  "VEHICLE_CAPACITY_POLICY_UNAVAILABLE",
  "PACKAGE_REQUIREMENT_INVALID",
  "PACKAGE_COUNT_EXCEEDED",
  "TOTAL_WEIGHT_EXCEEDED",
  "SINGLE_PACKAGE_WEIGHT_EXCEEDED",
  "PACKAGE_LENGTH_EXCEEDED",
  "PACKAGE_WIDTH_EXCEEDED",
  "PACKAGE_HEIGHT_EXCEEDED",
] as const;
export type IneligibilityReason = (typeof ineligibilityReasons)[number];

/** Plain es-MX explanation of each policy code; several codes may share one sentence. */
export const ineligibilityReasonLabels: Readonly<Record<IneligibilityReason, string>> = {
  DRIVER_UNAVAILABLE: "No está disponible",
  DRIVER_STATUS_NOT_ACTIVE: "Su perfil no está activo",
  USER_NOT_ACTIVE: "Su cuenta no está activa",
  DRIVER_MEMBERSHIP_NOT_ACTIVE: "No tiene acceso activo como repartidor",
  HOME_CITY_MISMATCH: "Trabaja en otra ciudad",
  SERVICE_AREA_REQUIRED: "No cubre la zona de la orden",
  SERVICE_AREA_NOT_ELIGIBLE: "No cubre la zona de la orden",
  DOCUMENT_POLICY_UNAVAILABLE: "Faltan las reglas de documentos para su vehículo",
  REQUIRED_DOCUMENT_MISSING: "Le falta un documento obligatorio",
  DOCUMENT_STATUS_NOT_VALID: "Tiene un documento no aprobado",
  DOCUMENT_EXPIRED: "Tiene un documento vencido",
  DOCUMENT_EXPIRY_MISSING: "Un documento no tiene fecha de vencimiento",
  DOCUMENT_HASH_INVALID: "Un documento no se pudo verificar",
  VEHICLE_CAPACITY_POLICY_UNAVAILABLE: "Faltan las reglas de capacidad para su vehículo",
  PACKAGE_REQUIREMENT_INVALID: "Los paquetes de la orden no tienen peso o medidas válidos",
  PACKAGE_COUNT_EXCEEDED: "Son demasiados paquetes para su vehículo",
  TOTAL_WEIGHT_EXCEEDED: "La carga pesa más de lo que admite su vehículo",
  SINGLE_PACKAGE_WEIGHT_EXCEEDED: "Un paquete pesa más de lo que admite su vehículo",
  PACKAGE_LENGTH_EXCEEDED: "Un paquete no cabe en su vehículo",
  PACKAGE_WIDTH_EXCEEDED: "Un paquete no cabe en su vehículo",
  PACKAGE_HEIGHT_EXCEEDED: "Un paquete no cabe en su vehículo",
};

export interface AssignableDriver {
  readonly driver_id: string;
  readonly driver_reference: string;
  readonly vehicle_type: VehicleType;
  readonly eligible: boolean;
  readonly ineligibility_reasons: readonly IneligibilityReason[];
  readonly active_assignment_count: number;
}

export interface AssignableDriverPage {
  readonly items: readonly AssignableDriver[];
  readonly next_cursor: string | null;
}

/** AI-05 bounds every list cursor to 128 characters. */
const maximumCursorLength = 128;
/** The server page size is 100; anything larger is not this contract. */
const maximumPageItems = 100;
const driverReferencePattern = /^DRV-[0-9a-f]{8}$/;

function cursor(value: unknown): string {
  const text = boundedString(value, 1, maximumCursorLength);
  if (!/^[A-Za-z0-9_-]+$/.test(text)) fail();
  return text;
}

export function parseAssignableDriver(value: unknown): AssignableDriver {
  const object = exactObject(value, [
    "driver_id",
    "driver_reference",
    "vehicle_type",
    "eligible",
    "ineligibility_reasons",
    "active_assignment_count",
  ]);
  const reference = boundedString(object.driver_reference, 12, 12);
  if (!driverReferencePattern.test(reference)) fail();
  const eligible = boolean(object.eligible);
  const reasons = array(object.ineligibility_reasons, ineligibilityReasons.length).map((reason) =>
    oneOf(reason, ineligibilityReasons),
  );
  if (new Set(reasons).size !== reasons.length || eligible !== (reasons.length === 0)) fail();
  return {
    driver_id: uuid(object.driver_id),
    driver_reference: reference,
    vehicle_type: oneOf(object.vehicle_type, vehicleTypes),
    eligible,
    ineligibility_reasons: reasons,
    active_assignment_count: integer(object.active_assignment_count, 0),
  };
}

export function parseAssignableDriverPage(value: unknown): AssignableDriverPage {
  const object = exactObject(value, ["items", "next_cursor"]);
  const items = array(object.items, maximumPageItems).map(parseAssignableDriver);
  if (new Set(items.map((item) => item.driver_id)).size !== items.length) fail();
  return { items, next_cursor: nullable(object.next_cursor, cursor) };
}

/** Only `cursor`, and only a value this operation issued. */
export function assignableDriverSearch(cursorValue?: string | null): URLSearchParams {
  const search = new URLSearchParams();
  if (cursorValue !== undefined && cursorValue !== null) search.set("cursor", cursor(cursorValue));
  return search;
}

/** AI-05 CreateAssignmentRequest as the order detail sends it: OWN, integer cents, no route. */
export interface AssignDriverBody {
  readonly driver_id: string;
  readonly assignment_type: "OWN";
  readonly cost_cents: number;
  readonly route_id: null;
}

export function assignDriverBody(driverId: string, costCents: number): AssignDriverBody {
  if (!isCanonicalUuid(driverId) || !Number.isSafeInteger(costCents) || costCents < 0) fail();
  return { driver_id: driverId, assignment_type: "OWN", cost_cents: costCents, route_id: null };
}

export interface Assignment {
  readonly id: string;
  readonly order_id: string;
  readonly driver_id: string;
  readonly route_id: string | null;
  readonly status: "OFFERED" | "ACCEPTED" | "ACTIVE" | "COMPLETED" | "CANCELLED";
  readonly cost: { readonly currency: "MXN"; readonly amount_cents: number };
}

export function parseAssignment(value: unknown): Assignment {
  const object = exactObject(value, ["id", "order_id", "driver_id", "route_id", "status", "cost"]);
  const cost = exactObject(object.cost, ["currency", "amount_cents"]);
  return {
    id: uuid(object.id),
    order_id: uuid(object.order_id),
    driver_id: uuid(object.driver_id),
    route_id: nullable(object.route_id, uuid),
    status: oneOf(object.status, ["OFFERED", "ACCEPTED", "ACTIVE", "COMPLETED", "CANCELLED"] as const),
    cost: { currency: oneOf(cost.currency, ["MXN"] as const), amount_cents: integer(cost.amount_cents, 0) },
  };
}

export function vehicleLabel(vehicle: VehicleType): string {
  return externalOfferVehicleLabels[vehicle as ExternalOfferVehicleType];
}

/** The distinct es-MX explanations of an ineligible driver, in the policy's order. */
export function ineligibilityText(reasons: readonly IneligibilityReason[]): string {
  return [...new Set(reasons.map((reason) => ineligibilityReasonLabels[reason]))].join("; ");
}

export function activeAssignmentsText(count: number): string {
  if (count === 0) return "sin entregas en curso";
  return count === 1 ? "1 entrega en curso" : `${count} entregas en curso`;
}

/** Eligible drivers first, then fewer deliveries in progress, then the reference; display order only. */
export function sortForPicker(drivers: readonly AssignableDriver[]): AssignableDriver[] {
  return [...drivers].sort(
    (left, right) =>
      Number(right.eligible) - Number(left.eligible) ||
      left.active_assignment_count - right.active_assignment_count ||
      (left.driver_reference < right.driver_reference ? -1 : left.driver_reference > right.driver_reference ? 1 : 0),
  );
}

/** Text of the confirmation shown before the assignment is sent; cost in integer cents. */
export function driverAssignmentConfirmation(
  publicId: string,
  driver: AssignableDriver,
  costCents: number,
): { readonly title: string; readonly description: string; readonly confirmLabel: string } {
  return {
    title: "¿Asignar repartidor?",
    description:
      `La orden ${publicId} se asignará al repartidor ${driver.driver_reference} ` +
      `(${vehicleLabel(driver.vehicle_type).toLowerCase()}) con un costo de ${formatMxnCentsWithCurrency(costCents)}.`,
    confirmLabel: "Asignar",
  };
}
