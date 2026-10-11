import type {
  DriverStop,
  DriverStopNavigation,
  DriverStopServiceWindow,
} from "./driver-stop";

/**
 * UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11 presentation helpers for the
 * details a driver sees at the door. Pure functions: no network, no storage.
 */

/** America/Mazatlan is the pilot zone; windows are always shown in it, never in the phone's zone. */
export const DriverStopTimeZone = "America/Mazatlan";

const actionVerbs: Readonly<Record<DriverStop["stop_type"], string>> = {
  PICKUP: "Recoger",
  DELIVERY: "Entregar",
  RETURN: "Devolver",
};

const leadPrefixes: Readonly<Record<DriverStop["stop_type"], string>> = {
  PICKUP: "Recoger con",
  DELIVERY: "Entregar a",
  RETURN: "Devolver a",
};

/** "Entregar", "Recoger" or "Devolver": what the driver does at the stop. */
export function driverStopActionVerb(type: DriverStop["stop_type"]): string {
  return actionVerbs[type];
}

/**
 * The contact name the stop shows (owner answer "Completo en recolección"):
 * the full name on a pickup ("Farmacia Las Quintas"), the short one on a
 * delivery or return ("M. López"); null when the stop has none.
 */
export function driverStopContactName(
  stop: Pick<DriverStop, "contact_short_name" | "contact_full_name">,
): string | null {
  return stop.contact_full_name ?? stop.contact_short_name;
}

/** "Entregar a M. López"; only the verb when the stop has no contact name. */
export function driverStopLead(
  stop: Pick<DriverStop, "stop_type" | "contact_short_name" | "contact_full_name">,
): string {
  const contact = driverStopContactName(stop);
  return contact === null
    ? actionVerbs[stop.stop_type]
    : `${leadPrefixes[stop.stop_type]} ${contact}`;
}

/**
 * "Cómo llegar" (GATE-003, owner answer "Google Maps"): a Google Maps search
 * URL on the exact point. It is a top-level navigation the phone hands to the
 * Maps app (or the Maps web page when the app is missing); the system never
 * requests a route, distance or ETA.
 */
export function googleMapsSearchUrl(
  navigation: Pick<DriverStopNavigation, "latitude" | "longitude">,
): string {
  const url = new URL("https://www.google.com/maps/search/");
  url.searchParams.set("api", "1");
  url.searchParams.set(
    "query",
    `${formatCoordinate(navigation.latitude)},${formatCoordinate(navigation.longitude)}`,
  );
  return url.toString();
}

/** Plain decimal text (never exponent notation), at most seven decimals. */
export function formatCoordinate(value: number): string {
  if (!Number.isFinite(value)) throw new RangeError("coordinate");
  const text = value.toFixed(7).replace(/\.?0+$/, "");
  return text === "-0" ? "0" : text;
}

const timeFormatter = new Intl.DateTimeFormat("es-MX", {
  hour: "2-digit",
  minute: "2-digit",
  hourCycle: "h23",
  timeZone: DriverStopTimeZone,
});

const dayFormatter = new Intl.DateTimeFormat("es-MX", {
  day: "numeric",
  month: "short",
  timeZone: DriverStopTimeZone,
});

const dayKeyFormatter = new Intl.DateTimeFormat("en-CA", {
  year: "numeric",
  month: "2-digit",
  day: "2-digit",
  timeZone: DriverStopTimeZone,
});

/**
 * "13:00–15:00" in Mazatlán time when the window is today; with the day
 * ("12 oct, 13:00–15:00") otherwise, and with both days when it spans midnight.
 */
export function formatDriverServiceWindow(
  window: DriverStopServiceWindow,
  now: Date = new Date(),
): string {
  const from = new Date(window.from);
  const to = new Date(window.to);
  const fromDay = dayKeyFormatter.format(from);
  const toDay = dayKeyFormatter.format(to);
  const fromTime = timeFormatter.format(from);
  const toTime = timeFormatter.format(to);
  if (fromDay !== toDay) {
    return `${dayFormatter.format(from)}, ${fromTime} – ${dayFormatter.format(to)}, ${toTime}`;
  }
  return fromDay === dayKeyFormatter.format(now)
    ? `${fromTime}–${toTime}`
    : `${dayFormatter.format(from)}, ${fromTime}–${toTime}`;
}
