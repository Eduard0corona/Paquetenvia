import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

/** UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11: the selectors of "Crear ruta". */
const createFormSources = [
  "src/operations/components/create-route-form.tsx",
  "src/operations/state/create-route-form-controller.ts",
  "src/operations/state/use-create-route-form.ts",
  "src/operations/api/locations-api.ts",
  "src/operations/contracts/locations.ts",
];

describe("manual route planner policy", () => {
  it("provides drag and keyboard reorder without client persistence", () => {
    const shell = readFileSync("src/operations/components/manual-routes-shell.tsx", "utf8");
    const state = readFileSync("src/operations/state/use-manual-routes.ts", "utf8");
    expect(shell).toContain("draggable=");
    expect(shell).toContain("onDrop=");
    expect(shell).toContain("Mover orden");
    expect(shell).toContain("Agregar entrega");
    expect(shell).toContain(">Subir<");
    expect(shell).toContain(">Bajar<");
    expect(`${shell}\n${state}`).not.toMatch(/localStorage|sessionStorage|indexedDB|caches\./i);
  });

  it("refetches REST after RouteChanged reconnect and stale conflict", () => {
    const state = readFileSync("src/operations/state/use-manual-routes.ts", "utf8");
    expect(state).toContain("RouteChanged:");
    expect(state).toContain("resynchronizeFromRest");
    expect(state).toContain("error.category === \"conflict\"");
    expect(state).toContain("loadDetail(selectedRef.current.id)");
  });

  it("creates routes from selectors, never from pasted internal ids (UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11)", () => {
    const aiSeven = readFileSync(resolve(process.cwd(), "../../docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml"), "utf8");
    expect(aiSeven).toContain("    create_form:\n      decision: UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11\n");
    const shell = readFileSync("src/operations/components/manual-routes-shell.tsx", "utf8");
    expect(shell).toContain("<CreateRouteForm form={form.state} controller={form.controller}");
    expect(shell).toContain("onCreate={(input) => void state.create(input)}");
    const form = readFileSync("src/operations/components/create-route-form.tsx", "utf8");
    expect(`${shell}\n${form}`).not.toMatch(/ID del repartidor|ID de la ciudad|ID del área|Pega el ID/);
    expect(form.match(/<select\b/g)).toHaveLength(3);
    expect(form).toContain('<option value="">Toda la ciudad</option>');
    expect(form).toContain('<input name="scheduledFor" type="date" />');
    expect(form).toContain("disabled={mutating || !ready}");
    // Ids are option values only, never shown.
    expect(form).not.toMatch(/>\s*\{(driver\.driver_id|city\.id|area\.id)\}/);
    // The same drivers client as Repartidores, and only ACTIVE drivers and areas are offered.
    const hook = readFileSync("src/operations/state/use-create-route-form.ts", "utf8");
    expect(hook).toContain("createDriversApi(apiBaseUrl(), session)");
    expect(hook).toContain("createLocationsApi(apiBaseUrl(), session)");
    const controller = readFileSync("src/operations/state/create-route-form-controller.ts", "utf8");
    expect(controller).toContain('unique.filter((driver) => driver.status === "ACTIVE")');
    expect(controller).toContain("activeServiceAreas(areas)");
    for (const path of createFormSources) {
      const source = readFileSync(path, "utf8");
      expect(source, path).not.toMatch(/localStorage|sessionStorage|indexedDB|caches\.|console\.|sendBeacon/);
    }
  });
});
