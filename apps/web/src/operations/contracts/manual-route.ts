export type RouteStatus = "DRAFT" | "PLANNED" | "ACTIVE" | "COMPLETED" | "CANCELLED";

export interface ManualRoute {
  readonly id: string;
  readonly status: RouteStatus;
  readonly version: number;
  readonly driver_id: string;
  readonly city_id: string;
  readonly service_area_id: string | null;
  readonly scheduled_for: string | null;
  readonly assignment_cost_cents_total: number;
  readonly stop_count: number;
}

export interface ManualRouteStop {
  readonly id: string;
  readonly order_id: string;
  readonly sequence: number;
  readonly stop_type: "PICKUP" | "DELIVERY" | "RETURN";
  readonly status: "PENDING" | "ARRIVED" | "COMPLETED" | "FAILED" | "SKIPPED";
}

export interface ManualRouteDetail extends ManualRoute {
  readonly stops: readonly ManualRouteStop[];
}

export interface ManualRoutePage {
  readonly items: readonly ManualRoute[];
  readonly next_cursor: string | null;
}

export const routeStatusLabels: Readonly<Record<RouteStatus, string>> = {
  DRAFT: "Borrador",
  PLANNED: "Planeada",
  ACTIVE: "En curso",
  COMPLETED: "Completada",
  CANCELLED: "Cancelada",
};

export const routeStopTypeLabels: Readonly<Record<ManualRouteStop["stop_type"], string>> = {
  PICKUP: "Recolección",
  DELIVERY: "Entrega",
  RETURN: "Devolución",
};

export const routeStopStatusLabels: Readonly<Record<ManualRouteStop["status"], string>> = {
  PENDING: "Pendiente",
  ARRIVED: "En el punto",
  COMPLETED: "Completada",
  FAILED: "Fallida",
  SKIPPED: "Omitida",
};

const months = [
  "enero",
  "febrero",
  "marzo",
  "abril",
  "mayo",
  "junio",
  "julio",
  "agosto",
  "septiembre",
  "octubre",
  "noviembre",
  "diciembre",
] as const;

/** A route's calendar date ("2026-10-12") in words ("12 de octubre de 2026"); null when it is not a real date. */
export function routeDateText(value: string): string | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (match === null) return null;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const date = new Date(Date.UTC(year, month - 1, day));
  if (year < 1000 || date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day)
    return null;
  return `${day} de ${months[month - 1]} de ${year}`;
}

/**
 * UI-PHASE3-ROUTES-NO-IDS-2026-10-12: a route as people read it, by its scheduled date - "Ruta del 12 de octubre de
 * 2026" or "Ruta sin fecha" - never by an identifier nor a `YYYY-MM-DD` value.
 */
export function routeName(route: Pick<ManualRoute, "scheduled_for">): string {
  const date = route.scheduled_for === null ? null : routeDateText(route.scheduled_for);
  return date === null ? "Ruta sin fecha" : `Ruta del ${date}`;
}

/** "Sin paradas", "1 parada", "3 paradas". */
export function routeStopsText(count: number): string {
  if (count === 0) return "Sin paradas";
  return count === 1 ? "1 parada" : `${count} paradas`;
}

/**
 * How the actions of a stop name it (UI-PHASE3-ROUTES-NO-IDS-2026-10-12): "orden ORD_…" by its guía, or "parada 2"
 * by its position when the guía is not available; never an identifier.
 */
export function routeStopSubject(stop: Pick<ManualRouteStop, "sequence">, guide: string | null): string {
  return guide === null ? `parada ${stop.sequence}` : `orden ${guide}`;
}

/** Text of the confirmation shown before a stop leaves a route: the stop, its type and, when known, its guía. */
export function removeRouteStopConfirmation(
  stop: ManualRouteStop,
  guide: string | null,
): {
  readonly title: string;
  readonly description: string;
  readonly confirmLabel: string;
} {
  const type = routeStopTypeLabels[stop.stop_type].toLowerCase();
  return {
    title: "¿Retirar parada?",
    description:
      `Se retirará de la ruta la parada ${stop.sequence} (${guide === null ? type : `${type} de la orden ${guide}`}). ` +
      "La orden quedará fuera de esta ruta.",
    confirmLabel: "Retirar",
  };
}

/** listRoutes cursors are opaque base64 text the server bounds to 256 characters. */
const routeCursorPattern = /^[A-Za-z0-9+/]{1,254}={0,2}$/;

export function isRouteCursor(value: string): boolean {
  return value.length <= 256 && routeCursorPattern.test(value);
}

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const statuses = new Set<RouteStatus>(["DRAFT", "PLANNED", "ACTIVE", "COMPLETED", "CANCELLED"]);
const stopTypes = new Set(["PICKUP", "DELIVERY", "RETURN"]);
const stopStatuses = new Set(["PENDING", "ARRIVED", "COMPLETED", "FAILED", "SKIPPED"]);

export function parseManualRoute(value: unknown): ManualRoute {
  const row = object(value);
  exact(row, ["id", "status", "version", "driver_id", "city_id", "service_area_id", "scheduled_for", "assignment_cost_cents_total", "stop_count"]);
  if (!isUuid(row.id) || !statuses.has(row.status as RouteStatus) ||
      !integer(row.version, 1) || !isUuid(row.driver_id) || !isUuid(row.city_id) ||
      !nullableUuid(row.service_area_id) || !nullableDate(row.scheduled_for) ||
      !integer(row.assignment_cost_cents_total, 0) || !Number.isSafeInteger(row.assignment_cost_cents_total) ||
      !integer(row.stop_count, 0)) throw new Error("Invalid Route response.");
  return row as unknown as ManualRoute;
}

export function parseManualRouteDetail(value: unknown): ManualRouteDetail {
  const row = object(value);
  const { stops, ...route } = row;
  if (!Array.isArray(stops)) throw new Error("Invalid RouteDetail response.");
  const parsedRoute = parseManualRoute(route);
  const parsedStops = stops.map(parseStop);
  if (parsedStops.some((stop, index) => stop.sequence !== index + 1) ||
      parsedRoute.stop_count !== parsedStops.length) throw new Error("Invalid RouteDetail sequence.");
  return { ...parsedRoute, stops: parsedStops };
}

export function parseManualRoutePage(value: unknown): ManualRoutePage {
  const row = object(value);
  exact(row, ["items", "next_cursor"]);
  if (!Array.isArray(row.items) || (row.next_cursor !== null && typeof row.next_cursor !== "string"))
    throw new Error("Invalid RoutePage response.");
  return { items: row.items.map(parseManualRoute), next_cursor: row.next_cursor as string | null };
}

function parseStop(value: unknown): ManualRouteStop {
  const row = object(value);
  exact(row, ["id", "order_id", "sequence", "stop_type", "status"]);
  if (!isUuid(row.id) || !isUuid(row.order_id) || !integer(row.sequence, 1) ||
      !stopTypes.has(row.stop_type as string) || !stopStatuses.has(row.status as string))
    throw new Error("Invalid RouteStop response.");
  return row as unknown as ManualRouteStop;
}

function object(value: unknown): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) throw new Error("Invalid response.");
  return value as Record<string, unknown>;
}

function exact(value: Record<string, unknown>, names: readonly string[]): void {
  const keys = Object.keys(value);
  if (keys.length !== names.length || names.some((name) => !keys.includes(name))) throw new Error("Unexpected response shape.");
}

function isUuid(value: unknown): value is string {
  return typeof value === "string" && uuid.test(value) && value !== "00000000-0000-0000-0000-000000000000";
}

function nullableUuid(value: unknown): boolean { return value === null || isUuid(value); }
/** UI-PHASE3-ROUTES-NO-IDS-2026-10-12: a scheduled date is a real calendar date or none; anything else fails closed. */
function nullableDate(value: unknown): boolean {
  return value === null || (typeof value === "string" && routeDateText(value) !== null);
}
function integer(value: unknown, minimum: number): value is number {
  return typeof value === "number" && Number.isInteger(value) && value >= minimum;
}
