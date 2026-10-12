import { asUuid } from "../../realtime/envelope";

export const MaximumStopsPerResponse = 500;
export const MaximumOrderPublicIdLength = 80;
export const MaximumAddressSummaryLength = 240;
/** AI-05 `DriverStop.contact_short_name` (UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11). */
export const MaximumContactShortNameLength = 64;
/** AI-05 `DriverStop.contact_full_name`, PICKUP and RETURN stops only. */
export const MaximumContactFullNameLength = 200;
/** AI-05 `DriverStop.zone_name`; the server cuts a longer name to it. */
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
  /**
   * false when the protected details were not requested or could not be read
   * (shown as "Datos de entrega no disponibles por ahora"): the contact names
   * and the navigation target are then null.
   */
  readonly details_available: boolean;
  /** DELIVERY stops only: "M. López"; null on PICKUP and RETURN stops. */
  readonly contact_short_name: string | null;
  /** PICKUP and RETURN stops only: the full name ("Farmacia Las Quintas"). */
  readonly contact_full_name: string | null;
  readonly zone_name: string | null;
  readonly service_window: DriverStopServiceWindow | null;
  readonly cod: DriverStopCod | null;
  readonly navigation: DriverStopNavigation | null;
}

/**
 * `GET /driver/me/stops` (AI-05 `DriverStopList`): the stops the app could
 * accept, how many of the driver's stops were completed today (Mazatlán) and
 * how many stops it had to leave out or show without details because they
 * broke the contract. The counts are telemetry only; nothing else is kept.
 */
export interface DriverStopList {
  readonly stops: readonly DriverStop[];
  readonly completedToday: number;
  readonly droppedCount: number;
  readonly degradedCount: number;
}

const exactProperties = new Set([
  "order_id",
  "aggregate_version",
  "order_public_id",
  "stop_type",
  "status",
  "address_summary",
  "details_available",
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
 * Parses the listMyStops answer. The envelope itself is exact (`stops` and
 * `completed_today` only); each stop is then checked on its own, so one
 * invalid stop never rejects the list (UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11):
 * a stop whose identity, status, summary, window, zone or cash breaks the
 * contract is left out, and a stop whose protected details break it is kept
 * without them. A repeated order keeps its first stop.
 */
export function parseDriverStopList(value: unknown): DriverStopList {
  const record = readExactRecord(value, new Set(["stops", "completed_today"]));
  const completedToday = record.completed_today;
  if (
    !Number.isSafeInteger(completedToday) ||
    (completedToday as number) < 0 ||
    !Array.isArray(record.stops) ||
    record.stops.length > MaximumStopsPerResponse
  ) {
    throw new DriverStopsContractError();
  }

  const orderIds = new Set<string>();
  const stops: DriverStop[] = [];
  let droppedCount = 0;
  let degradedCount = 0;
  for (const candidate of record.stops as unknown[]) {
    let parsed: { stop: DriverStop; degraded: boolean };
    try {
      parsed = parseStop(candidate, true);
    } catch (error) {
      if (!(error instanceof DriverStopsContractError)) throw error;
      droppedCount += 1;
      continue;
    }
    if (orderIds.has(parsed.stop.order_id)) {
      droppedCount += 1;
      continue;
    }
    orderIds.add(parsed.stop.order_id);
    if (parsed.degraded) degradedCount += 1;
    stops.push(parsed.stop);
  }

  return Object.freeze({
    stops: Object.freeze(stops),
    completedToday: completedToday as number,
    droppedCount,
    degradedCount,
  });
}

/**
 * Validates stops this app already accepted (the saved snapshot) strictly:
 * any invalid stop, duplicate or a list over the defensive maximum rejects
 * the whole value, so a tampered snapshot is never shown.
 */
export function parseDriverStops(value: unknown): readonly DriverStop[] {
  if (!Array.isArray(value) || value.length > MaximumStopsPerResponse) {
    throw new DriverStopsContractError();
  }

  const orderIds = new Set<string>();
  return value.map((candidate) => {
    const { stop } = parseStop(candidate, false);
    if (orderIds.has(stop.order_id)) {
      throw new DriverStopsContractError();
    }
    orderIds.add(stop.order_id);
    return stop;
  });
}

/**
 * One AI-05 `DriverStop`: exact properties at every level, bounded text,
 * finite in-range coordinates, integer cents and a COD only on DELIVERY stops.
 * The contact name follows the owner's answers: the full name only on PICKUP
 * and RETURN stops, the short one only on DELIVERY stops, and neither without
 * details. With `degradeDetails`, details that break those rules are dropped
 * (the stop is kept without them) instead of rejecting the stop.
 */
function parseStop(
  candidate: unknown,
  degradeDetails: boolean,
): { stop: DriverStop; degraded: boolean } {
  const record = readExactRecord(candidate, exactProperties);
  const orderId = readUuid(record.order_id);
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

  const core = {
    order_id: orderId,
    aggregate_version: aggregateVersion as number,
    order_public_id: publicId,
    stop_type: stopType,
    status: record.status as DriverStop["status"],
    address_summary: address,
    zone_name: readNullableBoundedText(record.zone_name, MaximumZoneNameLength),
    service_window: readServiceWindow(record.service_window),
    cod,
  };

  let details: Pick<
    DriverStop,
    | "details_available"
    | "contact_short_name"
    | "contact_full_name"
    | "navigation"
  >;
  let degraded = false;
  try {
    details = readDetails(record, stopType);
  } catch (error) {
    if (!degradeDetails || !(error instanceof DriverStopsContractError)) {
      throw error;
    }
    degraded = true;
    details = {
      details_available: false,
      contact_short_name: null,
      contact_full_name: null,
      navigation: null,
    };
  }

  return {
    stop: Object.freeze({ ...core, ...details }) as DriverStop,
    degraded,
  };
}

function readDetails(
  record: Record<string, unknown>,
  stopType: DriverStop["stop_type"],
): Pick<
  DriverStop,
  "details_available" | "contact_short_name" | "contact_full_name" | "navigation"
> {
  if (typeof record.details_available !== "boolean") {
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
  const navigation =
    record.navigation === null ? null : readNavigation(record.navigation);
  // UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: a delivery recipient's full name
  // is never accepted; pickups and returns carry only the full name; without
  // details there is no name and no navigation target.
  if (
    (fullName !== null && stopType === "DELIVERY") ||
    (shortName !== null && stopType !== "DELIVERY") ||
    (record.details_available &&
      navigation === null) ||
    (!record.details_available &&
      (navigation !== null || shortName !== null || fullName !== null))
  ) {
    throw new DriverStopsContractError();
  }
  return {
    details_available: record.details_available,
    contact_short_name: shortName,
    contact_full_name: fullName,
    navigation,
  };
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
