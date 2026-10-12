import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import type { City, ServiceArea } from "../contracts/locations";
import type { OrganizationDriver } from "../contracts/organization-driver";
import { syntheticUuid } from "../contracts/ui-001-screens.fixtures";
import type { CreateRouteFormState } from "../state/create-route-form-controller";
import { CreateRouteForm } from "./create-route-form";

/** UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11: "Crear ruta" in Rutas picks from lists, never pasted ids. */
const uuidText = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/;
const text = (html: string) => html.replace(/<[^>]+>/g, " ").replace(/\s+/g, " ").trim();
const noop = () => undefined;

const drivers: readonly OrganizationDriver[] = [
  {
    driver_id: syntheticUuid(0x601),
    driver_reference: "DRV-0a1b2c3d",
    vehicle_type: "MOTORCYCLE",
    status: "ACTIVE",
    active_stop_count: 2,
    last_position_at: null,
  },
  {
    driver_id: syntheticUuid(0x602),
    driver_reference: "DRV-9f8e7d6c",
    vehicle_type: "VAN",
    status: "ACTIVE",
    active_stop_count: 0,
    last_position_at: null,
  },
];
const cities: readonly City[] = [
  { id: syntheticUuid(0x900), name: "Culiacán", state_code: "SIN", country_code: "MX", timezone: "America/Mazatlan" },
];
const areas: readonly ServiceArea[] = [{ id: syntheticUuid(0x951), city_id: syntheticUuid(0x900), name: "Centro", status: "ACTIVE" }];

const loading: CreateRouteFormState = {
  drivers: { kind: "loading" },
  driversTruncated: false,
  cities: { kind: "loading" },
  areas: { kind: "idle" },
  driverId: null,
  cityId: null,
  serviceAreaId: null,
};

const ready: CreateRouteFormState = {
  ...loading,
  drivers: { kind: "ready", items: drivers },
  cities: { kind: "ready", items: cities },
};

function formHtml(form: CreateRouteFormState, mutating = false): string {
  return renderToStaticMarkup(
    createElement(CreateRouteForm, {
      form,
      controller: {
        chooseDriver: noop,
        chooseCity: () => Promise.resolve(),
        chooseArea: noop,
        loadDrivers: () => Promise.resolve(),
        loadCities: () => Promise.resolve(),
        loadAreas: () => Promise.resolve(),
        choice: () => null,
      },
      mutating,
      onCreate: noop,
    }),
  );
}

/** The opening `<select>` tag that follows a label text. */
function selectAfter(html: string, label: string): string {
  const start = html.indexOf(`>${label}</label>`);
  if (start < 0) throw new Error(`No label "${label}".`);
  const match = /<select[^>]*>/.exec(html.slice(start));
  if (match === null) throw new Error(`No select after "${label}".`);
  return match[0];
}

function submitTag(html: string): string {
  const match = /<button[^>]*type="submit"[^>]*>Crear ruta \(borrador\)<\/button>/.exec(html);
  if (match === null) throw new Error("No submit button.");
  return match[0];
}

describe("Rutas create form (UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11)", () => {
  it("asks for no internal id: driver, city and area are selectors, the date stays a date", () => {
    const html = formHtml(ready);
    expect(html).not.toMatch(/ID del repartidor|ID de la ciudad|ID del área|Pega el ID/);
    expect(html.match(/<select/g)).toHaveLength(3);
    expect(html).toContain(">Repartidor (flota propia)</label>");
    expect(html).toContain(">Ciudad</label>");
    expect(html).toContain(">Área de servicio (opcional)</label>");
    expect(html).toContain('<label>Fecha<input type="date" name="scheduledFor"/></label>');
    expect(html).not.toMatch(/<input[^>]*name="(driver|city|serviceArea)"/);
  });

  it("shows each driver by DRV- reference, vehicle and status, and each city by name and state, never an id", () => {
    const html = formHtml(ready);
    const visible = text(html);
    expect(visible).toContain("DRV-0a1b2c3d · Motocicleta · Activo");
    expect(visible).toContain("DRV-9f8e7d6c · Van · Activo");
    expect(visible).toContain("Culiacán, SIN");
    expect(visible).toContain("Solo repartidores activos de tu flota, por referencia.");
    // Ids are option values only, never text.
    expect(visible).not.toMatch(uuidText);
    expect(html).toContain(`<option value="${syntheticUuid(0x601)}">`);
  });

  it("keeps the area on Toda la ciudad until a city is chosen, then lists its areas", () => {
    const before = formHtml(ready);
    expect(selectAfter(before, "Área de servicio (opcional)")).toContain('disabled=""');
    expect(text(before)).toContain("Toda la ciudad");
    expect(text(before)).toContain("Elige primero la ciudad.");
    const after = formHtml({ ...ready, cityId: cities[0]!.id, areas: { kind: "ready", items: areas } });
    expect(selectAfter(after, "Área de servicio (opcional)")).not.toContain("disabled");
    expect(text(after)).toContain("Toda la ciudad Centro");
    expect(text(after)).toContain("Déjala en «Toda la ciudad» si la ruta cubre toda la ciudad.");
    const none = formHtml({ ...ready, cityId: cities[0]!.id, areas: { kind: "ready", items: [] } });
    expect(text(none)).toContain("Esta ciudad no tiene áreas de servicio activas; la ruta cubre toda la ciudad.");
    const loadingAreas = formHtml({ ...ready, cityId: cities[0]!.id, areas: { kind: "loading" } });
    expect(text(loadingAreas)).toContain("Cargando áreas de servicio…");
  });

  it("is sent only once a driver and a city are chosen", () => {
    expect(submitTag(formHtml(loading))).toContain('disabled=""');
    expect(submitTag(formHtml(ready))).toContain('disabled=""');
    expect(submitTag(formHtml({ ...ready, driverId: drivers[0]!.driver_id }))).toContain('disabled=""');
    expect(submitTag(formHtml({ ...ready, cityId: cities[0]!.id }))).toContain('disabled=""');
    const chosen = { ...ready, driverId: drivers[0]!.driver_id, cityId: cities[0]!.id };
    expect(submitTag(formHtml(chosen))).not.toContain("disabled");
    expect(submitTag(formHtml(chosen, true))).toContain('disabled=""');
  });

  it("says when each list is loading or empty", () => {
    const loadingText = text(formHtml(loading));
    expect(loadingText).toContain("Cargando repartidores…");
    expect(loadingText).toContain("Cargando ciudades…");
    expect(selectAfter(formHtml(loading), "Repartidor (flota propia)")).toContain('disabled=""');
    const empty = text(formHtml({ ...loading, drivers: { kind: "ready", items: [] }, cities: { kind: "ready", items: [] } }));
    expect(empty).toContain("Tu flota no tiene repartidores activos; la ruta necesita uno.");
    expect(empty).toContain("No hay ciudades disponibles.");
    const truncated = text(formHtml({ ...ready, driversTruncated: true }));
    expect(truncated).toContain("Hay más repartidores de los que esta lista muestra; algunos no aparecen.");
  });

  it("says when a list failed, offers to read it again and the MFA step-up back to Rutas", () => {
    const html = formHtml({
      ...loading,
      drivers: { kind: "failed", message: "Esta acción requiere verificar tu identidad (MFA).", stepUpHref: "/login?mfa=required&return_url=%2Fops%2Froutes" },
      cities: { kind: "failed", message: "No fue posible cargar las ciudades.", stepUpHref: null },
    });
    const visible = text(html);
    expect(visible).toContain("Esta acción requiere verificar tu identidad (MFA). Verificar identidad Intentar de nuevo");
    expect(html).toContain('href="/login?mfa=required&amp;return_url=%2Fops%2Froutes"');
    expect(visible).toContain("No fue posible cargar las ciudades. Intentar de nuevo");
    expect(selectAfter(html, "Repartidor (flota propia)")).toContain('aria-invalid="true"');
    expect(html.match(/>Intentar de nuevo<\/button>/g)).toHaveLength(2);
    expect(html.match(/<button type="button"/g)).toHaveLength(2);
  });
});
