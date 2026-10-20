import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

const component = readFileSync(
  resolve(process.cwd(), "src/driver/components/driver-stops-experience.tsx"),
  "utf8",
);
const listPage = readFileSync(
  resolve(process.cwd(), "src/app/driver/stops/page.tsx"),
  "utf8",
);
const detailPage = readFileSync(
  resolve(process.cwd(), "src/app/driver/stops/[id]/page.tsx"),
  "utf8",
);

describe("driver stops generic shell", () => {
  it("derives the route from the browser pathname", () => {
    expect(component).toContain(
      "parseDriverStopsPathname(window.location.pathname)",
    );
    expect(component).not.toContain("detailOrderId");
  });

  it("moves between list and detail inside the app, without Next prefetch or RSC", () => {
    // DRV-PWA-INAPP-NAVIGATION: real anchors whose plain click uses the
    // History API (navigateWithinDriverStops), so no page loads and nothing
    // is fetched, online or offline.
    expect(component).not.toContain('from "next/link"');
    expect(component).not.toContain("<Link");
    expect(component).not.toContain("useRouter");
    expect(component).toContain("<a");
    expect(component).toContain(
      "isInAppNavigationClick(event) && navigateWithinDriverStops(href)",
    );
    expect(component).toContain("event.preventDefault()");
    expect(component).toContain(
      'href={`/driver/stops/${stop.order_id}`}',
    );
    expect(component.match(/<DriverStopsLink className=\{styles\.secondaryLink\} href="\/driver\/stops">/g)).toHaveLength(2);
    expect(component).toContain(
      "window.addEventListener(driverStopsRouteChangedEvent, refreshRoute)",
    );
    expect(component).toContain('window.addEventListener("popstate", refreshRoute)');
    // As a page load did, each move reads the stops again (offline: the snapshot).
    const refreshRoute = component.slice(
      component.indexOf("const refreshRoute = () => {"),
      component.indexOf("readRoute();\n    window.addEventListener"),
    );
    expect(refreshRoute).toContain("void controllerRef.current?.retry();");
  });

  it('offers "Iniciar sesión" on the session panels with a validated return url', () => {
    // DRV-PWA-INAPP-NAVIGATION: the installed app has no address bar.
    const unavailable = component.indexOf('title="Sesión no disponible"');
    const unauthorized = component.indexOf(
      '{state.phase === "unauthorized" ? <SignInAction route={route} /> : null}',
    );
    expect(unavailable).toBeGreaterThan(0);
    expect(component.indexOf("<SignInAction route={route} />", unavailable)).toBeGreaterThan(unavailable);
    expect(unauthorized).toBeGreaterThan(0);
    expect(component).toContain("href={buildSignInPromptHref(returnPath)}");
    expect(component).toContain("Conéctate a internet para iniciar sesión.");
  });

  it("renders the same prop-free experience from list and detail pages", () => {
    expect(listPage).toContain("<DriverStopsExperience />");
    expect(detailPage).toContain("<DriverStopsExperience />");
    expect(detailPage).not.toContain("params");
  });

  it("does not render the internal route UUID as detail text", () => {
    expect(component).not.toContain(">{orderId}<");
  });

  it("shows commission and expiration before the external offer accept action", () => {
    const commission = component.indexOf("formatCommission(offer.commission.amount_cents)");
    const expiration = component.indexOf("formatTimestamp(offer.expires_at)");
    const accept = component.indexOf("Aceptar oferta");
    expect(commission).toBeGreaterThan(0);
    expect(expiration).toBeGreaterThan(commission);
    expect(accept).toBeGreaterThan(expiration);
    expect(component).toContain("No me interesa");
    expect(component).not.toContain("REJECTED");
  });
});
