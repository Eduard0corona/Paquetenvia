import { readFileSync } from "node:fs";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import type { BulkOrder } from "../contracts/bulk-assignment";
import {
  bulkRouteGoneMessage,
  bulkRouteMixedCitiesMessage,
  createRouteConflictMessages,
  draftRouteChoices,
  routeStopConflictMessages,
  unconfirmedRouteText,
} from "../contracts/bulk-route";
import type { ManualRoute } from "../contracts/manual-route";
import { syntheticUuid } from "../contracts/ui-001-screens.fixtures";
import type { BulkRouteCreation, BulkRouteNotice, BulkRouteState } from "../state/bulk-route-controller";
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

function choicesHtml(
  routes: BulkRouteState["routes"],
  selected: string | null = null,
  creation: BulkRouteCreation = { kind: "none" },
  notice: BulkRouteNotice | null = null,
): string {
  const choices = routes.kind === "ready" ? draftRouteChoices(routes.routes, routes.today) : [];
  return renderToStaticMarkup(
    createElement(BulkRouteChoices, {
      driverReference: driver.driverReference,
      routes,
      choices,
      selected,
      disabled: false,
      creation,
      notice,
      onSelect: noop,
      onReload: noop,
      onCreate: noop,
    }),
  );
}

const cityId = syntheticUuid(0x900);
const offered: BulkRouteCreation = { kind: "offered", cityId, cityName: "Culiacán, SIN" };
/** A `<button>` by its visible label; a missing button fails the test instead of passing a negative check. */
function buttonTag(html: string, label: string): string {
  const match = new RegExp(`<button[^>]*>${label}</button>`).exec(html);
  if (match === null) throw new Error(`No button "${label}".`);
  return match[0];
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
    // Read on a later day: none of them is today's, so the server's order stays.
    const html = choicesHtml({ kind: "ready", routes, today: "2026-10-20" }, routes[1]?.id ?? null);
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
    const empty = text(choicesHtml({ kind: "ready", routes: [], today: "2026-10-12" }));
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
        routes: { kind: "ready", routes: [route(1)], today: "2026-10-12" },
        canReload: false,
        onReload: noop,
      }),
    );
    expect(text(ready)).toBe("Ruta: Ruta del 12 de octubre de 2026 · 4 paradas");
    const gone = renderToStaticMarkup(
      createElement(BulkRouteFixed, { route: fixed, choice: null, routes: { kind: "ready", routes: [], today: "2026-10-12" }, canReload: true, onReload: noop }),
    );
    expect(text(gone)).toBe(`${bulkRouteGoneMessage} Leer la ruta de nuevo`);
    const loading = renderToStaticMarkup(
      createElement(BulkRouteFixed, { route: fixed, choice: null, routes: { kind: "loading" }, canReload: true, onReload: noop }),
    );
    expect(text(loading)).toBe("Leyendo de nuevo la ruta…");
  });
});

describe("Crear ruta nueva in the step (UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11)", () => {
  it("offers it where the driver has no DRAFT route, saying what it creates, the city by name only", () => {
    const html = choicesHtml({ kind: "ready", routes: [], today: "2026-10-12" }, null, offered);
    const visible = text(html);
    expect(visible).toContain("DRV-0a1b2c3d no tiene rutas en borrador.");
    expect(visible).toContain(
      "«Crear ruta nueva» crea una ruta en borrador para DRV-0a1b2c3d en Culiacán, SIN, la ciudad de las órdenes, con " +
        "la fecha de hoy (hora de Mazatlán), y sigue a la vista previa. Ninguna orden se agrega hasta que confirmes.",
    );
    const button = buttonTag(html, "Crear ruta nueva");
    expect(button).toContain('type="button"');
    expect(button).not.toContain("disabled");
    expect(button).toContain('aria-describedby="inbox-bulk-route-create-offer"');
    // Rutas stays the other way, opened in another tab, and the routes can be searched again.
    expect(visible).toContain("Las rutas también se crean en Rutas. Abrir Rutas en otra pestaña");
    expect(html).toContain('<a target="_blank" rel="noopener noreferrer" href="/ops/routes">Abrir Rutas en otra pestaña</a>');
    expect(buttonTag(html, "Buscar rutas de nuevo")).not.toContain("disabled");
    expect(visible).not.toContain("Crea una para este repartidor en Rutas");
    // No identifier, no radio and no amount.
    expect(visible).not.toMatch(uuidText);
    expect(html).not.toContain(cityId);
    expect(html).not.toContain('type="radio"');
    expect(visible).not.toMatch(/\$|MXN/);
    // Without the city's name it says "the orders' city".
    const unnamed = text(choicesHtml({ kind: "ready", routes: [], today: "2026-10-12" }, null, { ...offered, cityName: null }));
    expect(unnamed).toContain("para DRV-0a1b2c3d en la ciudad de las órdenes, con la fecha de hoy (hora de Mazatlán)");
  });

  it("waits while the route is being created: the buttons cannot be pressed again", () => {
    const html = choicesHtml({ kind: "ready", routes: [], today: "2026-10-12" }, null, { ...offered, kind: "creating" });
    expect(buttonTag(html, "Crear ruta nueva")).toContain('disabled=""');
    expect(buttonTag(html, "Buscar rutas de nuevo")).toContain('disabled=""');
    expect(text(html)).toContain("Creando la ruta en borrador…");
  });

  it("says why orders in different cities get no button, keeping Rutas and the new search", () => {
    const html = choicesHtml({ kind: "ready", routes: [], today: "2026-10-12" }, null, { kind: "mixed_cities" });
    const visible = text(html);
    expect(visible).toContain(`DRV-0a1b2c3d no tiene rutas en borrador. ${bulkRouteMixedCitiesMessage}`);
    expect(html).not.toContain("Crear ruta nueva");
    expect(visible).toContain("Las rutas se crean en Rutas. Abrir Rutas en otra pestaña");
    expect(visible).toContain("Buscar rutas de nuevo");
  });

  it("offers nothing next to DRAFT routes, nor while the routes or the orders cannot be read", () => {
    const listed = choicesHtml({ kind: "ready", routes: [route(1)], today: "2026-10-12" }, null, { kind: "none" });
    expect(listed).not.toContain("Crear ruta nueva");
    const failed = choicesHtml({ kind: "failed", message: "No fue posible cargar las rutas en borrador del repartidor. Intenta de nuevo.", stepUpHref: null });
    expect(failed).not.toContain("Crear ruta nueva");
    expect(text(failed)).toContain("No fue posible cargar las rutas en borrador del repartidor. Intenta de nuevo.");
    expect(failed).not.toContain("no tiene rutas en borrador");
  });

  it("shows the created route selected with its notice, and a refusal or an unconfirmed creation as a warning", () => {
    const created = route(9, { version: 1, stop_count: 0, scheduled_for: "2026-10-11", assignment_cost_cents_total: 0 });
    const html = choicesHtml({ kind: "ready", routes: [created], today: "2026-10-12" }, created.id, { kind: "none" }, {
      tone: "info",
      text: "Se creó «Ruta del 11 de octubre de 2026» en borrador para DRV-0a1b2c3d. Revisa la vista previa y confirma para agregar las órdenes.",
      stepUpHref: null,
    });
    expect(html).toContain(`checked="" value="${created.id}"`);
    expect(text(html)).toContain("Ruta del 11 de octubre de 2026 · sin paradas");
    expect(html).toContain('<p class="notice noticeInfo" role="status">Se creó «Ruta del 11 de octubre de 2026»');
    expect(html).not.toContain("Crear ruta nueva");

    const refused = choicesHtml({ kind: "ready", routes: [], today: "2026-10-12" }, null, offered, {
      tone: "warn",
      text: createRouteConflictMessages.DRIVER_INELIGIBLE ?? "",
      stepUpHref: null,
    });
    expect(refused).toContain(`<p class="notice noticeWarn" role="status">${createRouteConflictMessages.DRIVER_INELIGIBLE}`);
    expect(buttonTag(refused, "Crear ruta nueva")).not.toContain("disabled");

    const mfa = choicesHtml({ kind: "ready", routes: [], today: "2026-10-12" }, null, offered, {
      tone: "warn",
      text: "No se creó la ruta: Esta acción requiere verificar tu identidad (MFA).",
      stepUpHref: "/login?mfa=required&return_url=%2Fops%2Finbox",
    });
    expect(text(mfa)).toContain("No se creó la ruta: Esta acción requiere verificar tu identidad (MFA). Verificar identidad");
    expect(mfa).toContain('href="/login?mfa=required&amp;return_url=%2Fops%2Finbox"');

    const unconfirmed = choicesHtml({ kind: "ready", routes: [created], today: "2026-10-12" }, null, { kind: "none" }, {
      tone: "warn",
      text: unconfirmedRouteText("found", true, driver.driverReference),
      stepUpHref: null,
    });
    expect(text(unconfirmed)).toContain(unconfirmedRouteText("found", true, driver.driverReference));
    expect(unconfirmed.match(/checked=""/g)).toBeNull();
    expect(text(unconfirmed)).not.toMatch(uuidText);
  });

  it("names the created route in the preview once it is selected", () => {
    const created = route(9, { version: 1, stop_count: 0, scheduled_for: "2026-10-11" });
    const html = previewHtml("choosing", {}, created);
    expect(html).toContain('id="inbox-bulk-route-preview-region"');
    expect(text(html.slice(html.indexOf("<caption"), html.indexOf("</caption>")))).toBe(
      "Vista previa: 3 órdenes de DRV-0a1b2c3d a «Ruta del 11 de octubre de 2026». Se agregan al final de la ruta, una por una y en el orden de esta tabla.",
    );
  });
});

describe("a route per day in the step (UI-PHASE3-ROUTES-DAILY-2026-10-12)", () => {
  const today = "2026-10-12";
  const older = [
    route(1, { scheduled_for: "2026-10-11", stop_count: 3 }),
    route(2, { scheduled_for: null, stop_count: 0 }),
  ];

  it("shows the older DRAFT routes and Crear ruta nueva together when none is dated today", () => {
    const html = choicesHtml({ kind: "ready", routes: older, today }, older[0]?.id ?? null, offered);
    const visible = text(html);
    // The older routes stay choosable, in the server's order.
    expect(html.match(/type="radio"/g)).toHaveLength(2);
    expect(visible).toContain("Ruta del 11 de octubre de 2026 · 3 paradas");
    expect(visible).toContain("Ruta sin fecha · sin paradas");
    expect(visible).toContain("De la más reciente a la más antigua.");
    expect(html).toContain(`checked="" value="${older[0]?.id}"`);
    // Below them, what is missing (a route for today) and the button.
    expect(visible).toContain("DRV-0a1b2c3d no tiene una ruta en borrador con la fecha de hoy.");
    expect(visible).not.toContain("no tiene rutas en borrador");
    expect(visible).toContain("«Crear ruta nueva» crea una ruta en borrador para DRV-0a1b2c3d en Culiacán, SIN");
    expect(buttonTag(html, "Crear ruta nueva")).not.toContain("disabled");
    expect(html.indexOf('type="radio"')).toBeLessThan(html.indexOf("Crear ruta nueva</button>"));
    expect(visible).toContain("Las rutas también se crean en Rutas.");
    expect(visible).not.toMatch(uuidText);
    expect(visible).not.toMatch(/\$|MXN/);
  });

  it("says why orders in different cities get no button next to the older routes", () => {
    const visible = text(choicesHtml({ kind: "ready", routes: older, today }, null, { kind: "mixed_cities" }));
    expect(visible).toContain(
      `DRV-0a1b2c3d no tiene una ruta en borrador con la fecha de hoy. ${bulkRouteMixedCitiesMessage}`,
    );
    expect(visible).toContain("Ruta del 11 de octubre de 2026 · 3 paradas");
    expect(visible).not.toContain("Crear ruta nueva");
  });

  it("lists today's routes first, marked hoy, and offers no new route next to them", () => {
    const routes = [older[0]!, route(3, { scheduled_for: today, stop_count: 1 }), older[1]!];
    const html = choicesHtml({ kind: "ready", routes, today }, null, { kind: "none" });
    const labels = [...html.matchAll(/<span>([^<]*)<\/span><\/label>/g)].map((match) => match[1]);
    expect(labels).toEqual([
      "Ruta del 12 de octubre de 2026 (hoy) · 1 parada",
      "Ruta del 11 de octubre de 2026 · 3 paradas",
      "Ruta sin fecha · sin paradas",
    ]);
    expect(text(html)).toContain("Primero las de hoy (hora de Mazatlán); las demás, de la más reciente a la más antigua.");
    expect(html).not.toContain("Crear ruta nueva");
    expect(html).not.toContain("no tiene una ruta en borrador con la fecha de hoy");
  });

  it("drops an older route chosen before when Crear ruta nueva is pressed, so the new route is the selected one", () => {
    const step = readFileSync("src/operations/components/operations-bulk-route.tsx", "utf8");
    const onCreate = step.slice(step.indexOf("onCreate={() => {"), step.indexOf("createRoute();"));
    expect(onCreate).toContain("setRouteId(null);");
    expect(step).toContain("draftRouteChoices(state.routes.routes, state.routes.today)");
  });
});
