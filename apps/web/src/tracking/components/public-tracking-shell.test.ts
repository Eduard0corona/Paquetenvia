import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { PublicTrackingProjection } from "../contracts/public-tracking";
import type { PublicTrackingState } from "../state/use-public-tracking";
import { PublicTrackingShell } from "./public-tracking-shell";

const tracking = vi.hoisted(() => ({
  state: null as PublicTrackingState | null,
}));

vi.mock("../state/use-public-tracking", () => ({
  usePublicTracking: () => tracking.state,
}));

const brandVariable = "NEXT_PUBLIC_TRACKING_BRAND_NAME";
const heading = '<p class="trackingHeading">Seguimiento de envío</p>';

const projection: PublicTrackingProjection = {
  public_id: "ORD_AAAAAAAAAAAAAAAAAAAAAA",
  public_status: "OUT_FOR_DELIVERY",
  aggregate_version: 3,
  estimated_window: null,
  timeline: [{ code: "OUT_FOR_DELIVERY", occurred_at: "2026-10-10T18:00:00.000Z" }],
};

function state(
  view: PublicTrackingState["view"],
  withProjection = false,
): PublicTrackingState {
  return {
    view,
    connection: "connected",
    projection: withProjection ? projection : null,
    lastUpdated: null,
    refresh: () => undefined,
  };
}

/** Renders the shell as the browser does once the token was read from the URL. */
function renderInBrowser(current: PublicTrackingState): string {
  tracking.state = current;
  vi.stubGlobal("window", { location: { pathname: `/track/${"A".repeat(43)}` } });
  try {
    return renderToStaticMarkup(createElement(PublicTrackingShell));
  } finally {
    vi.unstubAllGlobals();
  }
}

/** Every view a recipient can reach, the server-rendered loading view included. */
function everyView(): Record<string, string> {
  tracking.state = state("loading");
  return {
    "server loading": renderToStaticMarkup(createElement(PublicTrackingShell)),
    loading: renderInBrowser(state("loading")),
    "not found": renderInBrowser(state("not-found")),
    unavailable: renderInBrowser(state("unavailable")),
    "rate limited": renderInBrowser(state("rate-limited")),
    tracking: renderInBrowser(state("tracking", true)),
    "unavailable with snapshot": renderInBrowser(state("unavailable", true)),
  };
}

const occurrences = (markup: string, text: string) => markup.split(text).length - 1;

let savedBrand: string | undefined;
beforeEach(() => {
  savedBrand = process.env[brandVariable];
  delete process.env[brandVariable];
});
afterEach(() => {
  if (savedBrand === undefined) delete process.env[brandVariable];
  else process.env[brandVariable] = savedBrand;
});

// GATE-001-NEUTRAL-PUBLIC-BRAND-2026-10-10: public tracking stays neutral
// until GATE-001 closes.
describe("public tracking shell branding", () => {
  it("opens every view with the neutral heading and never names the commercial name", () => {
    const views = everyView();
    expect(views.tracking).toContain(projection.public_id);
    expect(views["not found"]).toContain("No pudimos encontrar este seguimiento.");
    for (const [view, markup] of Object.entries(views)) {
      expect(markup, view).toContain(heading);
      expect(occurrences(markup, "Seguimiento de envío"), view).toBe(1);
      expect(markup, view).not.toMatch(/paquetenv/i);
      expect(markup, view).not.toContain("trackingEyebrow");
    }
  });

  it("does not repeat the heading when it is configured as the brand", () => {
    process.env[brandVariable] = "Seguimiento de envío";
    for (const [view, markup] of Object.entries(everyView())) {
      expect(markup, view).toContain(heading);
      expect(occurrences(markup, "Seguimiento de envío"), view).toBe(1);
    }
  });

  it("puts a configured brand above the neutral heading", () => {
    process.env[brandVariable] = "Marca validada";
    for (const [view, markup] of Object.entries(everyView())) {
      expect(markup, view).toContain(
        '<p class="trackingHeading">Marca validada</p><p class="trackingEyebrow">Seguimiento de envío</p>',
      );
    }
  });
});
