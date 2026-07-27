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
    expect(source).toContain("frame-ancestors 'none'");
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

  it("keeps future actions visibly disabled", () => {
    const source = readFileSync(
      "src/operations/components/operations-order-detail-shell.tsx",
      "utf8",
    );
    for (const action of [
      "Asignar repartidor propio",
      "Publicar oferta externa",
      "Agregar a ruta",
      "Abrir incidencia",
    ])
      expect(source).toContain(action);
    expect(source).toContain("type=\"button\" disabled");
  });
});
