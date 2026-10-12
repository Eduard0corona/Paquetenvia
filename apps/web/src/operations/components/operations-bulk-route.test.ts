import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import type { BulkOrder } from "../contracts/bulk-assignment";
import { bulkRouteGoneMessage, draftRouteChoices, routeStopConflictMessages } from "../contracts/bulk-route";
import type { ManualRoute } from "../contracts/manual-route";
import { syntheticUuid } from "../contracts/ui-001-screens.fixtures";
import type { BulkRouteState } from "../state/bulk-route-controller";
import { BulkRouteChoices, BulkRouteFixed, BulkRoutePreview, OperationsBulkRoute } from "./operations-bulk-route";

const driver = { driverId: syntheticUuid(0x601), driverReference: "DRV-0a1b2c3d" };
const uuidText = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/;

function bulkOrder(counter: number, zoneName: string | null = "Centro"): BulkOrder {
  return {
    orderId: syntheticUuid(0x100 + counter),
    publicId: `ORD_${String(counter).padStart(22, "0")}`,
    zoneName,
    totalCents: 123_456,
  };
}

const [first, second, third] = [bulkOrder(1), bulkOrder(2, null), bulkOrder(3)];

function route(counter: number, overrides: Partial<ManualRoute> = {}): ManualRoute {
  return {
    id: syntheticUuid(0x800 + counter),
    status: "DRAFT",
    version: 3,
    driver_id: driver.driverId,
    city_id: syntheticUuid(0x900),
    service_area_id: null,
    scheduled_for: "2026-10-12",
    assignment_cost_cents_total: 9_000,
    stop_count: 2,
    ...overrides,
  };
}

/** Visible text: tags removed, whitespace collapsed (attributes such as radio values are not text). */
const text = (html: string) => html.replace(/<[^>]+>/g, " ").replace(/\s+/g, " ").trim();
const noop = () => undefined;

function choicesHtml(routes: BulkRouteState["routes"], selected: string | null = null): string {
  const choices = routes.kind === "ready" ? draftRouteChoices(routes.routes) : [];
  return renderToStaticMarkup(
    createElement(BulkRouteChoices, {
      driverReference: driver.driverReference,
      routes,
      choices,
      selected,
      disabled: false,
      onSelect: noop,
      onReload: noop,
    }),
  );
}

function previewHtml(
  phase: BulkRouteState["phase"],
  results: BulkRouteState["results"] = {},
  chosen: ManualRoute | null = null,
): string {
  const [choice] = chosen === null ? [] : draftRouteChoices([chosen]);
  return renderToStaticMarkup(
    createElement(BulkRoutePreview, {
      orders: [first, second, third],
      driverReference: driver.driverReference,
      choice: choice ?? null,
      phase,
      results,
    }),
  );
}

describe("bulk add to route step (UI-PHASE3-INBOX-BULK-ADD-TO-ROUTE-2026-10-11)", () => {
  it("opens with the batch's orders and driver, the routes loading and nothing sent", () => {
    const html = renderToStaticMarkup(
      createElement(OperationsBulkRoute, {
        orders: [first, third],
        driver,
        returnUrl: "/ops/inbox?queue=unassigned",
        onChanged: noop,
        onCancel: noop,
        onRunningChange: noop,
      }),
    );
    const visible = text(html);
    expect(visible).toContain("Agregar 2 órdenes de DRV-0a1b2c3d a una ruta");
    expect(visible).toContain("Solo van las órdenes que este lote asignó a DRV-0a1b2c3d");
    expect(visible).toContain("Nada se agrega hasta que revises la vista previa y confirmes.");
    expect(visible).toContain("Buscando las rutas en borrador de DRV-0a1b2c3d…");
    expect(visible).toContain("Vista previa: 2 órdenes de DRV-0a1b2c3d. Se agregan al final de la ruta");
    expect(visible).toContain("Cancelar");
    // The review waits for a route; no order outside the batch is listed.
    expect(html).toMatch(/aria-disabled="true"[^>]*>Revisar y agregar 2 órdenes/);
    expect(visible).not.toContain(second.publicId);
    expect(visible).not.toMatch(uuidText);
    expect(visible).not.toMatch(/\$|MXN/);
  });

  it("lists only the driver's DRAFT routes by date and stops, newest first, and says where routes are created", () => {
    const routes = [route(1, { scheduled_for: null, stop_count: 0 }), route(2, { stop_count: 1 })];
    const html = choicesHtml({ kind: "ready", routes }, routes[1]?.id ?? null);
    const visible = text(html);
    expect(visible).toContain("Ruta en borrador de DRV-0a1b2c3d");
    expect(visible).toContain("Ruta sin fecha · sin paradas");
    expect(visible).toContain("Ruta del 12 de octubre de 2026 · 1 parada");
    expect(visible).toContain("De la más reciente a la más antigua.");
    expect(html.match(/type="radio"/g)).toHaveLength(2);
    expect(html).toContain(`checked="" value="${routes[1]?.id}"`);
    expect(html.match(/checked=""/g)).toHaveLength(1);
    // Routes are created in Rutas, opened in another tab so the batch stays on screen.
    expect(html).toContain('<a target="_blank" rel="noopener noreferrer" href="/ops/routes">Abrir Rutas en otra pestaña</a>');
    expect(visible).toContain("Buscar rutas de nuevo");
    // No identifier and no amount (the route's assignment cost total stays out of the inbox).
    expect(visible).not.toMatch(uuidText);
    expect(visible).not.toMatch(/\$|MXN|90\.00/);
  });

  it("explains how to get a route when the driver has none, and shows loading and failures", () => {
    const empty = text(choicesHtml({ kind: "ready", routes: [] }));
    expect(empty).toContain("DRV-0a1b2c3d no tiene rutas en borrador. Crea una para este repartidor en Rutas y vuelve a buscar aquí.");
    expect(empty).toContain("Abrir Rutas en otra pestaña");
    expect(empty).not.toContain("De la más reciente");
    expect(text(choicesHtml({ kind: "loading" }))).toContain("Buscando las rutas en borrador de DRV-0a1b2c3d…");
    const failed = choicesHtml({
      kind: "failed",
      message: "Esta acción requiere verificar tu identidad (MFA).",
      stepUpHref: "/login?mfa=required&return_url=%2Fops%2Finbox",
    });
    expect(text(failed)).toContain("Esta acción requiere verificar tu identidad (MFA). Verificar identidad");
    expect(failed).toContain('href="/login?mfa=required&amp;return_url=%2Fops%2Finbox"');
  });

  it("previews the orders in the order they are added, Guía and Destino only", () => {
    const html = previewHtml("choosing", {}, route(1));
    const caption = text(html.slice(html.indexOf("<caption"), html.indexOf("</caption>")));
    expect(caption).toBe(
      "Vista previa: 3 órdenes de DRV-0a1b2c3d a «Ruta del 12 de octubre de 2026». Se agregan al final de la ruta, una por una y en el orden de esta tabla.",
    );
    const head = html.slice(html.indexOf("<thead>"), html.indexOf("</thead>"));
    expect([...head.matchAll(/<th\b[^>]*>([\s\S]*?)<\/th>/g)].map((match) => text(match[1] ?? ""))).toEqual([
      "Orden de envío",
      "Guía",
      "Destino (zona)",
      "Resultado",
    ]);
    const rows = [...html.matchAll(/<tr>([\s\S]*?)<\/tr>/g)].slice(1).map((match) => text(match[1] ?? ""));
    expect(rows).toEqual([
      `1 ${first.publicId} Centro Por agregar`,
      `2 ${second.publicId} Sin zona asignada Por agregar`,
      `3 ${third.publicId} Centro Por agregar`,
    ]);
    // No amount at all: neither the order total nor the route's assignment costs.
    expect(text(html)).not.toMatch(/\$|MXN|1,234\.56/);
  });

  it("shows each order's own result and never hides a failure", () => {
    const html = previewHtml("choosing", {
      [first.orderId]: { kind: "added" },
      [second.orderId]: { kind: "route_changed" },
      [third.orderId]: { kind: "not_sent" },
    });
    const rows = [...html.matchAll(/<tr>([\s\S]*?)<\/tr>/g)].slice(1).map((match) => text(match[1] ?? ""));
    expect(rows[0]).toContain("Agregada a la ruta");
    expect(rows[1]).toContain(routeStopConflictMessages.VERSION_CONFLICT);
    expect(rows[2]).toContain("Sin enviar");
    const running = previewHtml("running", {
      [first.orderId]: { kind: "failed", message: routeStopConflictMessages.DRIVER_INELIGIBLE ?? "", retryable: false },
      [second.orderId]: { kind: "sending" },
      [third.orderId]: { kind: "pending" },
    });
    const runningRows = [...running.matchAll(/<tr>([\s\S]*?)<\/tr>/g)].slice(1).map((match) => text(match[1] ?? ""));
    expect(runningRows[0]).toContain(routeStopConflictMessages.DRIVER_INELIGIBLE);
    expect(runningRows[1]).toContain("Enviando…");
    expect(runningRows[2]).toContain("En espera");
  });

  it("names the fixed route with its current stops, or says it can no longer be used", () => {
    const fixed = route(1, { stop_count: 4 });
    const [choice] = draftRouteChoices([route(1)]);
    const ready = renderToStaticMarkup(
      createElement(BulkRouteFixed, {
        route: fixed,
        choice: choice ?? null,
        routes: { kind: "ready", routes: [route(1)] },
        canReload: false,
        onReload: noop,
      }),
    );
    expect(text(ready)).toBe("Ruta: Ruta del 12 de octubre de 2026 · 4 paradas");
    const gone = renderToStaticMarkup(
      createElement(BulkRouteFixed, { route: fixed, choice: null, routes: { kind: "ready", routes: [] }, canReload: true, onReload: noop }),
    );
    expect(text(gone)).toBe(`${bulkRouteGoneMessage} Leer la ruta de nuevo`);
    const loading = renderToStaticMarkup(
      createElement(BulkRouteFixed, { route: fixed, choice: null, routes: { kind: "loading" }, canReload: true, onReload: noop }),
    );
    expect(text(loading)).toBe("Leyendo de nuevo la ruta…");
  });
});
