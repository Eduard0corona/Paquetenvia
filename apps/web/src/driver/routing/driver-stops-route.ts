import { asUuid, type Uuid } from "../../realtime/envelope";

export type DriverStopsRoute =
  | Readonly<{ kind: "list" }>
  | Readonly<{ kind: "detail"; orderId: Uuid }>
  | Readonly<{ kind: "not-found" }>;

const detailPathPattern =
  /^\/driver\/stops\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\/?$/;

export function parseDriverStopsPathname(pathname: string): DriverStopsRoute {
  if (pathname === "/driver/stops" || pathname === "/driver/stops/") {
    return Object.freeze({ kind: "list" });
  }

  const match = detailPathPattern.exec(pathname);
  if (!match) {
    return Object.freeze({ kind: "not-found" });
  }

  try {
    return Object.freeze({ kind: "detail", orderId: asUuid(match[1]) });
  } catch {
    return Object.freeze({ kind: "not-found" });
  }
}

/**
 * DRV-PWA-INAPP-NAVIGATION: dispatched on the window after an in-app move
 * between the stops list and a stop detail, so the stops screen reads the new
 * pathname (`popstate` covers the browser's back and forward).
 */
export const driverStopsRouteChangedEvent =
  "paquetenvia:driver-stops-route-changed";

/** The parts of a click that decide whether the app may take it over. */
export interface DriverStopsLinkClick {
  readonly button: number;
  readonly defaultPrevented: boolean;
  readonly metaKey: boolean;
  readonly ctrlKey: boolean;
  readonly shiftKey: boolean;
  readonly altKey: boolean;
}

/**
 * A plain primary click (or Enter on the link) moves inside the app; a
 * modified or secondary click keeps the browser's own behaviour (for example
 * opening the stop in a new tab).
 */
export function isInAppNavigationClick(event: DriverStopsLinkClick): boolean {
  return (
    !event.defaultPrevented &&
    event.button === 0 &&
    !event.metaKey &&
    !event.ctrlKey &&
    !event.shiftKey &&
    !event.altKey
  );
}

export interface DriverStopsNavigationHost {
  readonly history: Pick<History, "pushState">;
  dispatchEvent(event: Event): boolean;
  scrollTo(x: number, y: number): void;
}

/**
 * DRV-PWA-INAPP-NAVIGATION: moves between the stops list and a stop detail
 * without loading a page. The History API entry is the one the Next.js App
 * Router keeps in sync (usePathname, back and forward); nothing is fetched,
 * so the move works the same offline and the session, the controllers and the
 * open IndexedDB partition stay as they are. Only the canonical list and
 * detail paths are taken; anything else returns false and the caller lets the
 * browser load it. Like a page load, the new screen starts at the top.
 */
export function navigateWithinDriverStops(
  href: string,
  host: DriverStopsNavigationHost = window,
): boolean {
  if (parseDriverStopsPathname(href).kind === "not-found") {
    return false;
  }
  host.history.pushState(null, "", href);
  host.dispatchEvent(new Event(driverStopsRouteChangedEvent));
  host.scrollTo(0, 0);
  return true;
}

export function findDriverStopForRoute<
  TStop extends Readonly<{ order_id: string }>,
>(route: DriverStopsRoute | null, stops: readonly TStop[]): TStop | undefined {
  if (route?.kind !== "detail") {
    return undefined;
  }

  return stops.find((stop) => stop.order_id === route.orderId);
}
