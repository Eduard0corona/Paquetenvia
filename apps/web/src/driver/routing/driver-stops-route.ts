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

export function findDriverStopForRoute<
  TStop extends Readonly<{ order_id: string }>,
>(route: DriverStopsRoute | null, stops: readonly TStop[]): TStop | undefined {
  if (route?.kind !== "detail") {
    return undefined;
  }

  return stops.find((stop) => stop.order_id === route.orderId);
}
