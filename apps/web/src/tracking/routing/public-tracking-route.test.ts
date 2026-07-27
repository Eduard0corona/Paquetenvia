import { describe, expect, it } from "vitest";
import { parsePublicTrackingPathname } from "./public-tracking-route";

const token = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA";

describe("public tracking route", () => {
  it.each([`/track/${token}`, `/track/${token}/`])(
    "accepts only a canonical token in %s",
    (pathname) => {
      expect(parsePublicTrackingPathname(pathname)).toEqual({
        kind: "tracking",
        token,
      });
    },
  );

  it.each([
    "/track",
    "/track/",
    `/track/${token}=`,
    `/track/${token}/extra`,
    `/track/%2F${token}`,
    `/track/${token}?query=value`,
    `/track/${token}#fragment`,
    "/track/../secret",
    "/tracking/token",
    `/track/${token.slice(1)}`,
    `/track/${token}a`,
    "/track/á",
  ])("rejects non-canonical pathname %s", (pathname) => {
    expect(parsePublicTrackingPathname(pathname)).toEqual({
      kind: "not-found",
    });
  });
});
