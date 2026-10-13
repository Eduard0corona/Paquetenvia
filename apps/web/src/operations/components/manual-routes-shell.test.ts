import { readFileSync } from "node:fs";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import type { ManualRoute, ManualRouteDetail } from "../contracts/manual-route";
import { syntheticUuid } from "../contracts/ui-001-screens.fixtures";
import type { CreateRouteFormState } from "../state/create-route-form-controller";
import type { RouteGuide } from "../state/manual-routes-controller";
import { RouteDetailView, RouteList, RoutesNoticeView } from "./manual-routes-shell";

/** UI-PHASE3-ROUTES-NO-IDS-2026-10-12: the route planner shows dates, DRV- references and guías, never ids. */
const text = (html: string) => html.replace(/<[^>]+>/g, " ").replace(/\s+/g, " ").trim();
const noop = () => undefined;

const routeId = syntheticUuid(0x801);
const otherRouteId = syntheticUuid(0x802);
const thirdRouteId = syntheticUuid(0x803);
const driverId = syntheticUuid(0x601);
const otherDriverId = syntheticUuid(0x602);
/** A driver listOrganizationDrivers did not return (for example an inactive one). */
const unlistedDriverId = syntheticUuid(0x6ff);
const orders = [syntheticUuid(0x101), syntheticUuid(0x102), syntheticUuid(0x103)];
const stopIds = [syntheticUuid(0xa01), syntheticUuid(0xa02), syntheticUuid(0xa03)];
const guide = "ORD_AbCdEfGhIjKlMnOpQrStUv";
/** Every identifier of the fixtures, whole or as the 8-character fragment the screen used to show. */
const identifiers = [
  routeId,
  otherRouteId,
  thirdRouteId,
  driverId,
  otherDriverId,
  unlistedDriverId,
  syntheticUuid(0x900),
  ...orders,
  ...stopIds,
];

type RouteListDrivers = Pick<CreateRouteFormState, "drivers" | "driverReferences">;

/** The listOrganizationDrivers read of the screen: DRV- references by driver id, any status. */
const driversRead: RouteListDrivers = {
  drivers: { kind: "ready", items: [] },
  driverReferences: new Map([
    [driverId, "DRV-0a1b2c3d"],
    [otherDriverId, "DRV-9f8e7d6c"],
  ]),
};
const driversLoading: RouteListDrivers = { drivers: { kind: "loading" }, driverReferences: new Map() };
const driversFailed: RouteListDrivers = {
  drivers: { kind: "failed", message: "No fue posible cargar los repartidores.", stepUpHref: null },
  driverReferences: new Map(),
};

function expectNoIdentifier(html: string): void {
  // Not as text and not in any attribute: ids are React keys and option values only.
  for (const id of identifiers) {
    expect(html, id).not.toContain(id);
    expect(html, id).not.toContain(id.slice(0, 8));
  }
  expect(html).not.toMatch(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/);
}

function summary(id: string, overrides: Partial<ManualRoute> = {}): ManualRoute {
  return {
    id,
    status: "DRAFT",
    version: 7,
    driver_id: driverId,
    city_id: syntheticUuid(0x900),
    service_area_id: null,
    scheduled_for: "2026-10-12",
    assignment_cost_cents_total: 12_550,
    stop_count: 3,
    ...overrides,
  };
}

const detail: ManualRouteDetail = {
  ...summary(routeId),
  stops: orders.map((order, index) => ({
    id: stopIds[index]!,
    order_id: order,
    sequence: index + 1,
    stop_type: "DELIVERY",
    status: "PENDING",
  })),
};

const guides: Record<string, RouteGuide> = {
  [orders[0]!]: { kind: "ready", publicId: guide },
  [orders[1]!]: { kind: "unavailable" },
  [orders[2]!]: { kind: "loading" },
};

function listHtml(routes: readonly ManualRoute[], drivers: RouteListDrivers = driversRead, selectedId: string | null = null): string {
  return renderToStaticMarkup(createElement(RouteList, { routes, drivers, truncated: false, selectedId, onSelect: noop }));
}

/** The visible text of each row of the list, in the order rendered. */
function rowTexts(html: string): string[] {
  return [...html.matchAll(/<button[^>]*>([\s\S]*?)<\/button>/g)].map((match) => text(match[1] ?? ""));
}

/** The name of each row (its `<strong>`), in the order rendered. */
function rowNames(html: string): string[] {
  return [...html.matchAll(/<strong>([\s\S]*?)<\/strong>/g)].map((match) => text(match[1] ?? ""));
}

function detailHtml(overrides: Partial<Parameters<typeof RouteDetailView>[0]> = {}): string {
  return renderToStaticMarkup(
    createElement(RouteDetailView, {
      route: detail,
      driver: "Repartidor DRV-0a1b2c3d",
      guides,
      guide: "",
      mutating: false,
      onGuideChange: noop,
      onAdd: noop,
      onReorder: noop,
      onRemove: noop,
      ...overrides,
    }),
  );
}

describe("route planner without internal ids (UI-PHASE3-ROUTES-NO-IDS-2026-10-12)", () => {
  it("lists every route by its date name, status, stops and cost, never by an id or a YYYY-MM-DD date", () => {
    const html = listHtml(
      [summary(routeId), summary(otherRouteId, { scheduled_for: null, stop_count: 1, status: "PLANNED" })],
      driversRead,
      routeId,
    );
    const visible = text(html);
    expect(visible).toContain("Ruta del 12 de octubre de 2026 · DRV-0a1b2c3d Borrador 3 paradas · $125.50");
    expect(visible).toContain("Ruta sin fecha · DRV-0a1b2c3d Planeada 1 parada · $125.50");
    expect(visible).not.toMatch(/\d{4}-\d{2}-\d{2}/);
    expect(html).toContain('aria-current="true"');
    expect(visible).not.toContain("algunas no aparecen");
    expectNoIdentifier(html);
    const truncated = renderToStaticMarkup(
      createElement(RouteList, { routes: [summary(routeId)], drivers: driversRead, truncated: true, selectedId: null, onSelect: noop }),
    );
    expect(text(truncated)).toContain("Hay más rutas de las que esta lista muestra; algunas no aparecen.");
  });

  describe("the driver of each row (UI-PHASE3-ROUTES-ROW-DRIVER-2026-10-12)", () => {
    // Several routes of the same day, in the server's order (not by date nor by driver).
    const sameDay = [
      summary(routeId, { driver_id: otherDriverId }),
      summary(otherRouteId, { scheduled_for: "2026-10-11", stop_count: 1 }),
      summary(thirdRouteId, { driver_id: driverId, stop_count: 0 }),
    ];

    it("names a known driver by its DRV- reference next to the date name", () => {
      const html = listHtml(sameDay);
      expect(rowNames(html)).toEqual([
        "Ruta del 12 de octubre de 2026 · DRV-9f8e7d6c",
        "Ruta del 11 de octubre de 2026 · DRV-0a1b2c3d",
        "Ruta del 12 de octubre de 2026 · DRV-0a1b2c3d",
      ]);
      expect(rowTexts(html)).toEqual([
        "Ruta del 12 de octubre de 2026 · DRV-9f8e7d6c Borrador 3 paradas · $125.50",
        "Ruta del 11 de octubre de 2026 · DRV-0a1b2c3d Borrador 1 parada · $125.50",
        "Ruta del 12 de octubre de 2026 · DRV-0a1b2c3d Borrador Sin paradas · $125.50",
      ]);
      expect(text(listHtml([summary(routeId, { scheduled_for: null })]))).toContain("Ruta sin fecha · DRV-0a1b2c3d Borrador");
      expectNoIdentifier(html);
    });

    it("says Repartidor no disponible for a driver the read did not return, or when the read failed", () => {
      const unlisted = listHtml([summary(routeId, { driver_id: unlistedDriverId }), sameDay[0]!]);
      expect(rowNames(unlisted)).toEqual([
        "Ruta del 12 de octubre de 2026 · Repartidor no disponible",
        "Ruta del 12 de octubre de 2026 · DRV-9f8e7d6c",
      ]);
      const failed = listHtml(sameDay, driversFailed);
      expect(rowNames(failed)).toEqual([
        "Ruta del 12 de octubre de 2026 · Repartidor no disponible",
        "Ruta del 11 de octubre de 2026 · Repartidor no disponible",
        "Ruta del 12 de octubre de 2026 · Repartidor no disponible",
      ]);
      expect(text(failed)).not.toContain("No fue posible cargar los repartidores.");
      expectNoIdentifier(unlisted);
      expectNoIdentifier(failed);
    });

    it("shows nothing for the driver while the drivers are read", () => {
      const html = listHtml(sameDay, driversLoading);
      expect(rowNames(html)).toEqual([
        "Ruta del 12 de octubre de 2026",
        "Ruta del 11 de octubre de 2026",
        "Ruta del 12 de octubre de 2026",
      ]);
      expect(rowTexts(html)[0]).toBe("Ruta del 12 de octubre de 2026 Borrador 3 paradas · $125.50");
      expect(text(html)).not.toMatch(/DRV-|Repartidor|Cargando/);
      expectNoIdentifier(html);
    });

    it("never renders an id, and each row's accessible name stays its text", () => {
      const cases: Array<[RouteListDrivers, ManualRoute[]]> = [
        [driversRead, sameDay],
        [driversRead, [summary(routeId, { driver_id: unlistedDriverId })]],
        [driversLoading, sameDay],
        [driversFailed, sameDay],
        // A read that returned no driver at all.
        [{ drivers: { kind: "ready", items: [] }, driverReferences: new Map() }, sameDay],
      ];
      for (const [drivers, routes] of cases) {
        const html = listHtml(routes, drivers, routes[0]!.id);
        expectNoIdentifier(html);
        expect(text(html)).not.toMatch(/undefined|null|\[object/);
        // No aria-label nor aria-hidden: each button is named by what it shows (date name, driver, status, stops, cost).
        expect(html).not.toMatch(/aria-label|aria-hidden|aria-labelledby/);
        expect(html.match(/<button type="button"/g)).toHaveLength(routes.length);
        expect(html.match(/aria-current="true"/g)).toHaveLength(1);
      }
    });
  });

  it("heads the detail with the date name, the DRV- driver, the status and the stops", () => {
    const html = detailHtml();
    expect(html).toContain("<h2>Ruta del 12 de octubre de 2026</h2>");
    expect(html).toContain("<p>Repartidor DRV-0a1b2c3d · Borrador · 3 paradas</p>");
    expect(text(detailHtml({ route: { ...detail, scheduled_for: null } }))).toContain("Ruta sin fecha");
    // A driver outside listOrganizationDrivers is never named by its id.
    expect(detailHtml({ driver: "Repartidor no disponible" })).toContain("<p>Repartidor no disponible · Borrador · 3 paradas</p>");
    expect(text(html)).not.toMatch(/versión|Ruta [0-9a-f]{8}/);
    expectNoIdentifier(html);
  });

  it("asks for the guía (ORD_…) to add a delivery, never for the order id", () => {
    const html = detailHtml({ guide });
    expect(text(html)).toContain("Guía (ORD_…)");
    expect(html).toContain('placeholder="ORD_…"');
    expect(html).toContain(`value="${guide}"`);
    expect(html).toContain('name="guide"');
    expect(html).not.toMatch(/ID de la orden|order_id|name="order"/);
    expect(html).toMatch(/<button class="btn btnPrimary" type="submit">Agregar entrega<\/button>/);
    expect(detailHtml({ mutating: true })).toMatch(/type="submit" disabled="">Agregar entrega/);
    expect(detailHtml({ route: { ...detail, status: "PLANNED" } })).toMatch(/type="submit" disabled="">Agregar entrega/);
  });

  it("holds 'Agregar entrega' with the common countdown during the wait of listOrders, keeping the guía (SEC-004 Q13)", () => {
    const html = detailHtml({ guide, rateLimit: { secondsLeft: 1 } });
    expect(html).toMatch(/type="submit" disabled="">Agregar entrega/);
    expect(html).toContain(`value="${guide}"`);
    expect(html).toContain('<p class="notice noticeWarn" role="timer">Demasiadas solicitudes. Espera 1 segundo e intenta de nuevo.</p>');
  });

  it("names each stop by its guía, Guía no disponible or Cargando guía…, and its actions by guía or position", () => {
    const html = detailHtml();
    const titles = [...html.matchAll(/<strong>([\s\S]*?)<\/strong><small>/g)].map((match) => text(match[1] ?? ""));
    expect(titles).toEqual([`1. ${guide}`, "2. Guía no disponible", "3. Cargando guía…"]);
    const labels = [...html.matchAll(/aria-label="([^"]+)"/g)].map((match) => match[1]);
    expect(labels).toEqual([
      `Mover orden ${guide} arriba`,
      `Mover orden ${guide} abajo`,
      `Retirar orden ${guide} de la ruta`,
      "Mover parada 2 arriba",
      "Mover parada 2 abajo",
      "Retirar parada 2 de la ruta",
      "Mover parada 3 arriba",
      "Mover parada 3 abajo",
      "Retirar parada 3 de la ruta",
    ]);
    // A stop whose guía was never asked for reads as loading, not as an id.
    expect(text(detailHtml({ guides: {} }))).toContain("1. Cargando guía…");
    expectNoIdentifier(html);
    expectNoIdentifier(detailHtml({ guides: {} }));
  });

  it("shows the reason of a refused change and Verificar identidad only when MFA is missing", () => {
    const mfa = renderToStaticMarkup(
      createElement(RoutesNoticeView, {
        notice: {
          tone: "warn",
          text: "Esta acción requiere verificar tu identidad (MFA).",
          stepUpHref: `/login?mfa=required&return_url=${encodeURIComponent(`/ops/routes?routeId=${routeId}`)}`,
        },
      }),
    );
    expect(mfa).toContain('class="notice noticeWarn" role="status"');
    expect(text(mfa)).toBe("Esta acción requiere verificar tu identidad (MFA). Verificar identidad");
    expect(mfa).toContain('href="/login?mfa=required&amp;return_url=%2Fops%2Froutes%3FrouteId%3D');
    const saved = renderToStaticMarkup(
      createElement(RoutesNoticeView, { notice: { tone: "info", text: "Cambio guardado.", stepUpHref: null } }),
    );
    expect(saved).toBe('<p class="notice noticeInfo" role="status">Cambio guardado. </p>');
  });

  it("keeps the screen's sources free of short ids, ids as text and browser persistence", () => {
    const shell = readFileSync("src/operations/components/manual-routes-shell.tsx", "utf8");
    expect(shell).not.toMatch(/shortId|short-id/);
    // No id rendered as a JSX child ("{route.id}", "{stop.order_id}"…).
    expect(shell).not.toMatch(/[>}]\s*\{\s*[\w?.!]+\.(id|order_id|driver_id|city_id)\s*\}/);
    expect(shell).not.toMatch(/ID de la orden/);
    for (const path of [
      "src/operations/components/manual-routes-shell.tsx",
      "src/operations/state/manual-routes-controller.ts",
      "src/operations/state/use-manual-routes.ts",
      "src/operations/api/routes-api.ts",
      "src/operations/contracts/manual-route.ts",
    ]) {
      const source = readFileSync(path, "utf8");
      expect(source, path).not.toMatch(/localStorage|sessionStorage|indexedDB|caches\.|console\.|sendBeacon/);
      expect(source, path).not.toMatch(/shortId/);
    }
  });
});
