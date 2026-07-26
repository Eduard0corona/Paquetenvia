import { describe, expect, it } from "vitest";
import {
  findDriverStopForRoute,
  parseDriverStopsPathname,
} from "./driver-stops-route";

const orderId = "22222222-2222-2222-2222-222222222222";

describe("parseDriverStopsPathname", () => {
  it.each(["/driver/stops", "/driver/stops/"])(
    "accepts the list pathname %s",
    (pathname) => {
      expect(parseDriverStopsPathname(pathname)).toEqual({ kind: "list" });
    },
  );

  it.each([`/driver/stops/${orderId}`, `/driver/stops/${orderId}/`])(
    "accepts the canonical detail pathname %s",
    (pathname) => {
      expect(parseDriverStopsPathname(pathname)).toEqual({
        kind: "detail",
        orderId,
      });
    },
  );

  it.each([
    "/driver/stop",
    "/driver/stops//",
    "/driver/stops/extra/path",
    `/driver/stops/${orderId}/extra`,
    `/driver/stops/${orderId}-extra`,
    `/driver/stops/${orderId}/../${orderId}`,
    "/driver/stops/ABCDEF12-3456-7890-ABCD-EF1234567890",
    "/driver/stops/00000000-0000-0000-0000-000000000000",
    "/driver/stops/not-a-uuid",
    `/driver/stops/${orderId}%2Fextra`,
    `/driver/stops/${orderId}?organization=other`,
    `/driver/stops/${orderId}#other`,
  ])("rejects the non-canonical pathname %s", (pathname) => {
    expect(parseDriverStopsPathname(pathname)).toEqual({ kind: "not-found" });
  });
});

describe("findDriverStopForRoute", () => {
  const currentPartitionStops = [
    { order_id: orderId, address_summary: "Organización actual" },
  ] as const;

  it("selects the requested stop from the current partition", () => {
    const route = parseDriverStopsPathname(`/driver/stops/${orderId}`);
    expect(findDriverStopForRoute(route, currentPartitionStops)).toEqual(
      currentPartitionStops[0],
    );
  });

  it("returns not found for an unknown or invalid pathname", () => {
    const unknown = parseDriverStopsPathname(
      "/driver/stops/33333333-3333-3333-3333-333333333333",
    );
    const invalid = parseDriverStopsPathname("/driver/stops/not-a-uuid");
    expect(findDriverStopForRoute(unknown, currentPartitionStops)).toBeUndefined();
    expect(findDriverStopForRoute(invalid, currentPartitionStops)).toBeUndefined();
  });

  it("cannot select a stop that is absent from the current partition", () => {
    const otherPartitionRoute = parseDriverStopsPathname(
      "/driver/stops/44444444-4444-4444-4444-444444444444",
    );
    expect(
      findDriverStopForRoute(otherPartitionRoute, currentPartitionStops),
    ).toBeUndefined();
  });
});
