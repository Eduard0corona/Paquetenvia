import { asUuid } from "../../realtime/envelope";

export const MaximumStopsPerResponse = 500;
export const MaximumOrderPublicIdLength = 80;
export const MaximumAddressSummaryLength = 240;
/** AI-05 `DriverStop.contact_short_name` (UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11). */
export const MaximumContactShortNameLength = 64;
/** AI-05 `DriverStop.contact_full_name`, PICKUP stops only ("Completo en recolección"). */
export const MaximumContactFullNameLength = 200;
/** Defensive bound for `DriverStop.zone_name`; AI-05 sets none. */
export const MaximumZoneNameLength = 200;
/** AI-05 `DriverStop.navigation.address_text`. */
export const MaximumAddressTextLength = 1000;
/** AI-05 `DriverStop.navigation.references`. */
export const MaximumReferencesLength = 500;

export const driverStopTypes = ["PICKUP", "DELIVERY", "RETURN"] as const;
export const driverStopStatuses = [
  "ASSIGNED",
  "AT_PICKUP",
  "PICKED_UP",
  "IN_TRANSIT",
  "DELIVERING",
  "FAILED_ATTEMPT",
  "RESCHEDULED",
  "RETURNING",
] as const;

/** The order's own delivery window as UTC instants, normalized to `Z`. */
export interface DriverStopServiceWindow {
  readonly from: string;
  readonly to: string;
}

/**
 * Cash to collect on a DELIVERY stop: the expected amount in MXN integer
 * cents and whether a collection of exactly that amount is already recorded.
 */
export interface DriverStopCod {
  readonly expected: { readonly currency: "MXN"; readonly amount_cents: number };
  readonly recorded: boolean;
}

/** Where the stop is: exact point plus the full address text and references. */
export interface DriverStopNavigation {
  readonly latitude: number;
  readonly longitude: number;
  readonly address_text: string;
  readonly references: string | null;
}

export interface DriverStop {
  readonly order_id: string;
  readonly aggregate_version: number;
  readonly order_public_id: string;
  readonly stop_type: (typeof driverStopTypes)[number];
  readonly status: (typeof driverStopStatuses)[number];
  readonly address_summary: string;
  /** DELIVERY and RETURN stops: "M. López"; always null on a PICKUP stop. */
  readonly contact_short_name: string | null;
  /** PICKUP stops only: the full name ("Farmacia Las Quintas"); null on the others. */
  readonly contact_full_name: string | null;
  readonly zone_name: string | null;
  readonly service_window: DriverStopServiceWindow | null;
  readonly cod: DriverStopCod | null;
  readonly navigation: DriverStopNavigation;
}

const exactProperties = new Set([
  "order_id",
  "aggregate_version",
  "order_public_id",
  "stop_type",
  "status",
  "address_summary",
  "contact_short_name",
  "contact_full_name",
  "zone_name",
  "service_window",
  "cod",
  "navigation",
]);

export class DriverStopsContractError extends Error {
  public constructor() {
    super("La respuesta de paradas no cumple el contrato esperado.");
    this.name = "DriverStopsContractError";
  }
}

/**
 * Parses `GET /driver/me/stops` (AI-05 `DriverStop[]`) fail-closed: exact
 * properties at every level, bounded text, finite in-range coordinates,
 * integer cents, a COD only on DELIVERY stops and a full contact name only on
 * PICKUP stops (a short one only on the others). Nothing is kept from an
 * invalid response.
 */
export function parseDriverStops(value: unknown): readonly DriverStop[] {
  if (!Array.isArray(value) || value.length > MaximumStopsPerResponse) {
    throw new DriverStopsContractError();
  }

  const orderIds = new Set<string>();
  return value.map((candidate) => {
    const record = readExactRecord(candidate, exactProperties);

    const orderId = readUuid(record.order_id);
    if (orderIds.has(orderId)) {
      throw new DriverStopsContractError();
    }
    orderIds.add(orderId);

    const aggregateVersion = record.aggregate_version;
    if (
      !Number.isSafeInteger(aggregateVersion) ||
      (aggregateVersion as number) < 1
    ) {
      throw new DriverStopsContractError();
    }

    const publicId = readBoundedText(
      record.order_public_id,
      MaximumOrderPublicIdLength,
    );
    const address = readBoundedText(
      record.address_summary,
      MaximumAddressSummaryLength,
    );
    if (!driverStopTypes.includes(record.stop_type as DriverStop["stop_type"])) {
      throw new DriverStopsContractError();
    }
    if (!driverStopStatuses.includes(record.status as DriverStop["status"])) {
      throw new DriverStopsContractError();
    }
    const stopType = record.stop_type as DriverStop["stop_type"];
    const cod = readCod(record.cod);
    if (cod !== null && stopType !== "DELIVERY") {
      throw new DriverStopsContractError();
    }
    const shortName = readNullableBoundedText(
      record.contact_short_name,
      MaximumContactShortNameLength,
    );
    const fullName = readNullableBoundedText(
      record.contact_full_name,
      MaximumContactFullNameLength,
    );
    // UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: a delivery or return contact's
    // full name is never accepted, and a pickup carries only the full name.
    if (
      (fullName !== null && stopType !== "PICKUP") ||
      (shortName !== null && stopType === "PICKUP")
    ) {
      throw new DriverStopsContractError();
    }

    return Object.freeze({
      order_id: orderId,
      aggregate_version: aggregateVersion as number,
      order_public_id: publicId,
      stop_type: stopType,
      status: record.status as DriverStop["status"],
      address_summary: address,
      contact_short_name: shortName,
      contact_full_name: fullName,
      zone_name: readNullableBoundedText(record.zone_name, MaximumZoneNameLength),
      service_window: readServiceWindow(record.service_window),
      cod,
      navigation: readNavigation(record.navigation),
    });
  });
}

function readServiceWindow(value: unknown): DriverStopServiceWindow | null {
  if (value === null) return null;
  const record = readExactRecord(value, new Set(["from", "to"]));
  const from = readUtcTimestamp(record.from);
  const to = readUtcTimestamp(record.to);
  if (Date.parse(from) >= Date.parse(to)) {
    throw new DriverStopsContractError();
  }
  return Object.freeze({ from, to });
}

function readCod(value: unknown): DriverStopCod | null {
  if (value === null) return null;
  const record = readExactRecord(value, new Set(["expected", "recorded"]));
  const expected = readExactRecord(
    record.expected,
    new Set(["currency", "amount_cents"]),
  );
  if (
    expected.currency !== "MXN" ||
    !Number.isSafeInteger(expected.amount_cents) ||
    (expected.amount_cents as number) < 1 ||
    typeof record.recorded !== "boolean"
  ) {
    throw new DriverStopsContractError();
  }
  return Object.freeze({
    expected: Object.freeze({
      currency: "MXN" as const,
      amount_cents: expected.amount_cents as number,
    }),
    recorded: record.recorded,
  });
}

function readNavigation(value: unknown): DriverStopNavigation {
  const record = readExactRecord(
    value,
    new Set(["latitude", "longitude", "address_text", "references"]),
  );
  return Object.freeze({
    latitude: readCoordinate(record.latitude, 90),
    longitude: readCoordinate(record.longitude, 180),
    address_text: readBoundedText(record.address_text, MaximumAddressTextLength),
    references: readNullableBoundedText(
      record.references,
      MaximumReferencesLength,
    ),
  });
}

function readCoordinate(value: unknown, bound: number): number {
  if (
    typeof value !== "number" ||
    !Number.isFinite(value) ||
    value < -bound ||
    value > bound
  ) {
    throw new DriverStopsContractError();
  }
  return value;
}

function readExactRecord(
  value: unknown,
  properties: ReadonlySet<string>,
): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw new DriverStopsContractError();
  }
  const record = value as Record<string, unknown>;
  const keys = Object.keys(record);
  if (
    keys.length !== properties.size ||
    keys.some((key) => !properties.has(key))
  ) {
    throw new DriverStopsContractError();
  }
  return record;
}

function readUuid(value: unknown): string {
  if (typeof value !== "string") {
    throw new DriverStopsContractError();
  }

  try {
    return asUuid(value);
  } catch {
    throw new DriverStopsContractError();
  }
}

function readBoundedText(value: unknown, maximumLength: number): string {
  if (
    typeof value !== "string" ||
    value.length < 1 ||
    value.length > maximumLength ||
    value.trim().length < 1
  ) {
    throw new DriverStopsContractError();
  }

  return value;
}

function readNullableBoundedText(
  value: unknown,
  maximumLength: number,
): string | null {
  return value === null ? null : readBoundedText(value, maximumLength);
}

/** UTC instants only (`Z` or `+00:00`), returned normalized to `Z`. */
function readUtcTimestamp(value: unknown): string {
  if (
    typeof value !== "string" ||
    value.length > 40 ||
    !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(
      value,
    ) ||
    !Number.isFinite(Date.parse(value))
  ) {
    throw new DriverStopsContractError();
  }
  return new Date(value).toISOString();
}
