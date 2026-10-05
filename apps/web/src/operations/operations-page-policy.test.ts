import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

describe("operations page privacy policy", () => {
  it("keeps operations routes and authenticated requests network-only", () => {
    const source = readFileSync("public/sw.js", "utf8");
    expect(source).toContain('CACHE_NAME = "paquetenvia-driver-shell-v5"');
    expect(source).toContain('url.pathname === "/ops"');
    expect(source).toContain('url.pathname.startsWith("/ops/")');
    expect(source).toContain('request.headers.has("Authorization")');
    expect(source).toContain('url.searchParams.has("access_token")');
  });

  it("sets private no-store security headers without geolocation", () => {
    const source = readFileSync("next.config.ts", "utf8");
    expect(source).toContain('source: "/ops/:path*"');
    expect(source).toContain('value: "no-store, private"');
    expect(source).toContain("geolocation=()");
    expect(
      readFileSync("src/security/security-headers.ts", "utf8"),
    ).toContain("frame-ancestors 'none'");
  });

  it("does not use browser persistence or an external map provider", () => {
    const sources = [
      "src/operations/state/use-operations-dashboard.ts",
      "src/operations/state/use-operations-order-detail.ts",
      "src/operations/components/operations-positions.tsx",
    ]
      .map((path) => readFileSync(path, "utf8"))
      .join("\n");
    expect(sources).not.toMatch(
      /localStorage|sessionStorage|indexedDB|caches\.|google\.maps|mapbox|leaflet|openstreetmap/i,
    );
    expect(sources).toContain("Vista de posiciones sin cartografía.");
  });

  it("links order actions to the screens where they work instead of disabled placeholders", () => {
    const source = readFileSync(
      "src/operations/components/operations-order-detail-shell.tsx",
      "utf8",
    );
    for (const action of ["Publicar oferta externa", "Abrir incidencia"])
      expect(source).toContain(action);
    expect(source).toContain('href="/ops/dashboard"');
    expect(source).toContain('href="/ops/incidents"');
    expect(source).not.toContain("Acciones futuras");
    expect(source).not.toContain("type=\"button\" disabled");
  });

  it("controls the creation-date inputs from the filters so clearing them empties the inputs", () => {
    const source = readFileSync("src/operations/components/operations-filters.tsx", "utf8");
    expect(source).toContain("value={utcToDateTimeLocal(filters.createdFrom)}");
    expect(source).toContain("value={utcToDateTimeLocal(filters.createdTo)}");
    expect(source).toContain("onChange({});");
    expect(source).not.toContain("defaultValue");
  });

  it("labels the assignment the same way on the dashboard card and the order detail", () => {
    for (const path of [
      "src/operations/components/operations-order-card.tsx",
      "src/operations/components/operations-order-detail-shell.tsx",
    ]) {
      const source = readFileSync(path, "utf8");
      expect(source, path).toContain("assignmentTypeLabel(");
      expect(source, path).not.toContain("Flota propia");
    }
  });

  it("links the dashboard to order creation and finance screens to each other", () => {
    const dashboard = readFileSync("src/operations/components/operations-dashboard-shell.tsx", "utf8");
    expect(dashboard).toContain('href="/ops/orders/new"');
    expect(dashboard).toContain('href="/finance/cod"');
    const cod = readFileSync("src/finance/components/cod-shell.tsx", "utf8");
    expect(cod).toContain('href="/finance/settlements"');
    expect(cod).toContain('href="/ops/dashboard"');
    const settlements = readFileSync("src/finance/components/settlements-shell.tsx", "utf8");
    expect(settlements).toContain('href="/finance/cod"');
    expect(settlements).toContain('href="/ops/dashboard"');
  });
});
