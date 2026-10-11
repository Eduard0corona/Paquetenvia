import { vehicleTypes, type VehicleType } from "./assignable-driver";
import {
  array,
  boundedString,
  exactObject,
  fail,
  integer,
  nullable,
  oneOf,
  timestamp,
  uuid,
} from "./strict-json";

/**
 * UI-PHASE3-DRIVERS-2026-10-11 ("Lista de consulta"): AI-05 listOrganizationDrivers (OrganizationDriverPage), the
 * read-only "Repartidores" list of the dispatcher. A driver is shown only by its non-personal `DRV-` reference
 * (AI-06 stores no driver name; personal data waits for GATE-007); the id is a key, never shown, and the latest
 * position comes as its capture time only. Every reader fails closed on the first value outside the contract.
 */
export const organizationDriverStatuses = ["PENDING", "ACTIVE", "SUSPENDED"] as const;
export type OrganizationDriverStatus = (typeof organizationDriverStatuses)[number];

export interface OrganizationDriver {
  readonly driver_id: string;
  readonly driver_reference: string;
  readonly vehicle_type: VehicleType;
  readonly status: OrganizationDriverStatus;
  readonly active_stop_count: number;
  /** Capture time of the latest reported position; null when the driver never reported one. */
  readonly last_position_at: string | null;
}

export interface OrganizationDriverPage {
  readonly items: readonly OrganizationDriver[];
  readonly next_cursor: string | null;
}

/** The server page size is 100; anything larger is not this contract. */
const maximumPageItems = 100;
/** AI-05 bounds every list cursor to 128 characters. */
const maximumCursorLength = 128;
const driverReferencePattern = /^DRV-[0-9a-f]{8}$/;

function cursor(value: unknown): string {
  const text = boundedString(value, 1, maximumCursorLength);
  if (!/^[A-Za-z0-9_-]+$/.test(text)) fail();
  return text;
}

export function parseOrganizationDriver(value: unknown): OrganizationDriver {
  const object = exactObject(value, [
    "driver_id",
    "driver_reference",
    "vehicle_type",
    "status",
    "active_stop_count",
    "last_position_at",
  ]);
  const reference = boundedString(object.driver_reference, 12, 12);
  if (!driverReferencePattern.test(reference)) fail();
  return {
    driver_id: uuid(object.driver_id),
    driver_reference: reference,
    vehicle_type: oneOf(object.vehicle_type, vehicleTypes),
    status: oneOf(object.status, organizationDriverStatuses),
    active_stop_count: integer(object.active_stop_count, 0),
    last_position_at: nullable(object.last_position_at, timestamp),
  };
}

export function parseOrganizationDriverPage(value: unknown): OrganizationDriverPage {
  const object = exactObject(value, ["items", "next_cursor"]);
  const items = array(object.items, maximumPageItems).map(parseOrganizationDriver);
  if (new Set(items.map((item) => item.driver_id)).size !== items.length) fail();
  return { items, next_cursor: nullable(object.next_cursor, cursor) };
}

/** Only `cursor`, and only a value this operation issued. */
export function organizationDriverSearch(cursorValue?: string | null): URLSearchParams {
  const search = new URLSearchParams();
  if (cursorValue !== undefined && cursorValue !== null) search.set("cursor", cursor(cursorValue));
  return search;
}

/** es-MX label and tone of each profile status; the label is always shown next to the dot (never color alone). */
export const organizationDriverStatusLabels: Readonly<Record<OrganizationDriverStatus, string>> = {
  PENDING: "Pendiente de activar",
  ACTIVE: "Activo",
  SUSPENDED: "Suspendido",
};

export const organizationDriverStatusTones: Readonly<Record<OrganizationDriverStatus, "muted" | "ok" | "warn">> = {
  PENDING: "muted",
  ACTIVE: "ok",
  SUSPENDED: "warn",
};

export function activeStopsText(count: number): string {
  if (count === 0) return "Sin paradas";
  return count === 1 ? "1 parada" : `${count} paradas`;
}

/** Drivers by reference, then id: the display order of the list (the API pages by id). */
export function sortByReference(drivers: readonly OrganizationDriver[]): OrganizationDriver[] {
  return [...drivers].sort((left, right) =>
    left.driver_reference < right.driver_reference
      ? -1
      : left.driver_reference > right.driver_reference
        ? 1
        : left.driver_id < right.driver_id
          ? -1
          : left.driver_id > right.driver_id
            ? 1
            : 0,
  );
}
