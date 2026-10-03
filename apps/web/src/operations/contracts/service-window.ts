/**
 * ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02: optional delivery window on createOrder.
 *
 * The dispatcher types wall-clock times of the pilot zone (America/Mazatlan) in
 * `datetime-local` inputs. They are converted to instants with that zone explicitly,
 * never with the browser's zone, and sent as UTC RFC 3339 strings. The bounds below
 * mirror the server rules (AI-05 CreateOrderRequest.service_window); the server stays
 * the authority and rejects anything else with 409.
 */

export const serviceWindowTimeZone = "America/Mazatlan";
/** Longest window the server accepts (to - from). */
export const maximumServiceWindowHours = 12;
/** How far ahead the window may start. */
export const maximumServiceWindowLeadDays = 30;
/** Repository clock tolerance: the start may lie up to 5 minutes before server time. */
export const serviceWindowClockToleranceMinutes = 5;

export interface ServiceWindowBody {
  readonly from: string;
  readonly to: string;
}

export type ServiceWindowResult =
  | { readonly ok: true; readonly window: ServiceWindowBody | null }
  | { readonly ok: false; readonly error: string };

export const serviceWindowMessages = {
  incomplete: "Captura inicio y fin de la ventana de entrega, o deja ambos vacíos.",
  invalid: "La ventana de entrega requiere fecha y hora válidas (hora de Mazatlán).",
  order: "El fin de la ventana de entrega debe ser posterior al inicio.",
  span: `La ventana de entrega admite como máximo ${maximumServiceWindowHours} horas.`,
  ended: "La ventana de entrega ya terminó; captura una vigente.",
  past: "La ventana de entrega no puede iniciar en el pasado.",
  lead: `La ventana de entrega no puede iniciar a más de ${maximumServiceWindowLeadDays} días.`,
} as const;

const wallTimePattern = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?$/;

const zoneFormatter = new Intl.DateTimeFormat("en-CA", {
  timeZone: serviceWindowTimeZone,
  year: "numeric",
  month: "2-digit",
  day: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
  hourCycle: "h23",
});

/** Wall-clock time of `instant` in America/Mazatlan, encoded as if it were UTC milliseconds. */
function zoneWallClockMs(instant: number): number {
  const parts: Record<string, number> = {};
  for (const part of zoneFormatter.formatToParts(new Date(instant))) {
    if (part.type !== "literal") parts[part.type] = Number(part.value);
  }
  return Date.UTC(
    parts.year!,
    parts.month! - 1,
    parts.day!,
    parts.hour! % 24,
    parts.minute!,
    parts.second!,
  );
}

/**
 * Converts a `datetime-local` value (`YYYY-MM-DDTHH:mm[:ss]`) read as America/Mazatlan
 * wall-clock time to its instant. Returns `null` for malformed text, impossible dates
 * and wall-clock times that do not exist in the zone.
 */
export function mazatlanWallTimeToInstant(text: string): Date | null {
  const match = wallTimePattern.exec(text.trim());
  if (match === null) return null;
  const [year, month, day, hour, minute] = match.slice(1, 6).map(Number) as [number, number, number, number, number];
  const second = match[6] === undefined ? 0 : Number(match[6]);
  const wall = Date.UTC(year, month - 1, day, hour, minute, second);
  const check = new Date(wall);
  if (
    check.getUTCFullYear() !== year ||
    check.getUTCMonth() !== month - 1 ||
    check.getUTCDate() !== day ||
    check.getUTCHours() !== hour ||
    check.getUTCMinutes() !== minute ||
    check.getUTCSeconds() !== second
  )
    return null;
  // Two passes settle the zone offset around a transition; the final check rejects a
  // wall-clock time skipped by the zone.
  let instant = wall - (zoneWallClockMs(wall) - wall);
  instant = wall - (zoneWallClockMs(instant) - instant);
  return zoneWallClockMs(instant) === wall ? new Date(instant) : null;
}

/** UTC RFC 3339 with whole seconds, the form the server stores and hashes. */
function utcText(instant: Date): string {
  return instant.toISOString().replace(/\.\d{3}Z$/, "Z");
}

/**
 * Builds the optional `service_window` from the two typed values. Both empty is "no
 * window" (the zone's schedule applies); one alone, an invalid time or a window the
 * server would refuse at `now` is an error.
 */
export function buildServiceWindow(
  fromText: string | undefined,
  toText: string | undefined,
  now: Date,
): ServiceWindowResult {
  const fromTrimmed = (fromText ?? "").trim();
  const toTrimmed = (toText ?? "").trim();
  if (fromTrimmed === "" && toTrimmed === "") return { ok: true, window: null };
  if (fromTrimmed === "" || toTrimmed === "") return { ok: false, error: serviceWindowMessages.incomplete };
  const from = mazatlanWallTimeToInstant(fromTrimmed);
  const to = mazatlanWallTimeToInstant(toTrimmed);
  if (from === null || to === null) return { ok: false, error: serviceWindowMessages.invalid };
  const fromMs = from.getTime();
  const toMs = to.getTime();
  const nowMs = now.getTime();
  if (Number.isNaN(nowMs)) return { ok: false, error: serviceWindowMessages.invalid };
  if (fromMs >= toMs) return { ok: false, error: serviceWindowMessages.order };
  if (toMs - fromMs > maximumServiceWindowHours * 3_600_000) return { ok: false, error: serviceWindowMessages.span };
  if (toMs <= nowMs) return { ok: false, error: serviceWindowMessages.ended };
  if (fromMs < nowMs - serviceWindowClockToleranceMinutes * 60_000)
    return { ok: false, error: serviceWindowMessages.past };
  if (fromMs > nowMs + maximumServiceWindowLeadDays * 86_400_000)
    return { ok: false, error: serviceWindowMessages.lead };
  return { ok: true, window: { from: utcText(from), to: utcText(to) } };
}

const windowFormatter = new Intl.DateTimeFormat("es-MX", {
  timeZone: serviceWindowTimeZone,
  day: "2-digit",
  month: "short",
  hour: "2-digit",
  minute: "2-digit",
  hourCycle: "h23",
});

/** The order's delivery window in Mazatlan time; null means the zone's schedule applies. */
export function formatServiceWindow(window: ServiceWindowBody | null): string {
  if (window === null) return "Horario de la zona";
  return `${windowFormatter.format(new Date(window.from))} a ${windowFormatter.format(new Date(window.to))}`;
}
