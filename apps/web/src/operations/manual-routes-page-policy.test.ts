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

/** UI-PHASE3-ROUTES-NO-IDS-2026-10-12: the rest of the route planner. */
const plannerSources = [
  "src/operations/components/manual-routes-shell.tsx",
  "src/operations/state/manual-routes-controller.ts",
  "src/operations/state/use-manual-routes.ts",
  "src/operations/api/routes-api.ts",
  "src/operations/contracts/manual-route.ts",
];

const read = (path: string) => readFileSync(path, "utf8");
const aiSeven = () => read(resolve(process.cwd(), "../../docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml"));

describe("manual route planner policy", () => {
  it("provides drag and keyboard reorder without client persistence", () => {
    const shell = read("src/operations/components/manual-routes-shell.tsx");
    expect(shell).toContain("draggable=");
    expect(shell).toContain("onDrop=");
    expect(shell).toContain("aria-label={`Mover ${subject} arriba`}");
    expect(shell).toContain("aria-label={`Mover ${subject} abajo`}");
    expect(shell).toContain("Agregar entrega");
    expect(shell).toContain(">Subir<");
    expect(shell).toContain(">Bajar<");
    for (const path of plannerSources)
      expect(read(path), path).not.toMatch(/localStorage|sessionStorage|indexedDB|caches\.|console\.|sendBeacon/);
  });

  it("refetches REST after RouteChanged, a reconnection and a version conflict only", () => {
    const hook = read("src/operations/state/use-manual-routes.ts");
    expect(hook).toContain("RouteChanged: (event) => void controller.routeChanged(event.payload.route_id)");
    expect(hook).toContain("resynchronizeFromRest: () => controller.resynchronize()");
    const controller = read("src/operations/state/manual-routes-controller.ts");
    // Only VERSION_CONFLICT says the route changed, and it reads the route again (tested in the controller).
    expect(controller).toContain('if (code === "VERSION_CONFLICT") return { text: routeChangedMessage, stepUpHref: null, reload: true };');
    expect(controller).toContain("if (failure.reload && routeId !== null) {");
    expect(controller.match(/reload: true/g)).toHaveLength(1);
  });

  it("creates routes from selectors, never from pasted internal ids (UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11)", () => {
    expect(aiSeven()).toContain("    create_form:\n      decision: UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11\n");
    const shell = read("src/operations/components/manual-routes-shell.tsx");
    expect(shell).toContain("<CreateRouteForm form={form.state} controller={form.controller}");
    expect(shell).toContain("onCreate={(input) => void controller.create(input)}");
    const form = read("src/operations/components/create-route-form.tsx");
    expect(`${shell}\n${form}`).not.toMatch(/ID del repartidor|ID de la ciudad|ID del área|Pega el ID/);
    expect(form.match(/<select\b/g)).toHaveLength(3);
    expect(form).toContain('<option value="">Toda la ciudad</option>');
    expect(form).toContain('<input name="scheduledFor" type="date" />');
    expect(form).toContain("disabled={mutating || !ready}");
    // Ids are option values only, never shown.
    expect(form).not.toMatch(/>\s*\{(driver\.driver_id|city\.id|area\.id)\}/);
    // The same drivers client as Repartidores, and only ACTIVE drivers and areas are offered.
    const hook = read("src/operations/state/use-create-route-form.ts");
    expect(hook).toContain("createDriversApi(apiBaseUrl(), session)");
    expect(hook).toContain("createLocationsApi(apiBaseUrl(), session)");
    const controller = read("src/operations/state/create-route-form-controller.ts");
    expect(controller).toContain('unique.filter((driver) => driver.status === "ACTIVE")');
    expect(controller).toContain("activeServiceAreas(areas)");
    for (const path of createFormSources) {
      const source = read(path);
      expect(source, path).not.toMatch(/localStorage|sessionStorage|indexedDB|caches\.|console\.|sendBeacon/);
    }
  });

  it("names routes, drivers and stops without internal ids and says why a change failed (UI-PHASE3-ROUTES-NO-IDS-2026-10-12)", () => {
    const contract = aiSeven();
    expect(contract).toContain("    - UI-PHASE3-ROUTES-NO-IDS-2026-10-12\n");
    for (const block of ["errors", "list", "add_order", "detail"])
      expect(contract).toContain(`    ${block}:\n      decision: UI-PHASE3-ROUTES-NO-IDS-2026-10-12\n`);
    expect(contract).toContain("      order_lookup: GET /orders?public_id= (listOrders)");
    expect(contract).toContain("      order_guides: GET /orders/{orderId} (getOrder)");
    // The route planner speaks through the shared tenant requester, like the inbox: codes and MFA_REQUIRED arrive.
    const api = read("src/operations/api/routes-api.ts");
    expect(api).toContain("const send = createTenantRequester(baseUrl, session);");
    expect(api).not.toMatch(/RoutesApiError|fetch\(/);
    // "Agregar entrega" by guía: the top-bar validation and listOrders client, then addRouteStop.
    expect(api).toContain("findOrderIdByPublicId: (publicId, signal) => orders.findOrderIdByPublicId(publicId, signal)");
    const controller = read("src/operations/state/manual-routes-controller.ts");
    expect(controller).toContain("const publicId = normalizePublicIdInput(this.getSnapshot().guide);");
    expect(controller).toContain("current.id, orderId, current.version, this.randomUuid()");
    // Every change keeps its own fresh key, as the screen always did.
    expect(controller.match(/this\.randomUuid\(\)/g)).toHaveLength(4);
    expect(controller).not.toContain("PendingSubmissions");
    const shell = read("src/operations/components/manual-routes-shell.tsx");
    expect(shell).toContain('label="Guía (ORD_…)"');
    expect(shell).toContain("driver={routeDriverText(form.state, selected.driver_id)}");
    expect(shell).not.toMatch(/shortId|ID de la orden|queryOrder|scheduled_for \?\?/);
    // The list keeps the server's order.
    expect(shell).not.toMatch(/\.sort\(|todayFirst|draftRouteChoices/);
  });

  it("names the driver of every route row from the screen's one drivers read (UI-PHASE3-ROUTES-ROW-DRIVER-2026-10-12)", () => {
    const contract = aiSeven();
    expect(contract).toContain("    - UI-PHASE3-ROUTES-ROW-DRIVER-2026-10-12\n");
    expect(contract).toContain(
      "    list:\n      decision: UI-PHASE3-ROUTES-NO-IDS-2026-10-12\n" +
        "      decision_amendment: UI-PHASE3-ROUTES-ROW-DRIVER-2026-10-12\n",
    );
    const shell = read("src/operations/components/manual-routes-shell.tsx");
    // The list gets the listOrganizationDrivers read of "Crear ruta", the same one that names the detail's driver.
    expect(shell).toContain("drivers={form.state}");
    expect(shell).toContain("const driver = routeListDriverText(drivers, route.driver_id);");
    expect(shell.match(/useCreateRouteForm\(\)/g)).toHaveLength(1);
    // No second drivers read anywhere in the route planner.
    for (const path of plannerSources)
      expect(read(path), path).not.toMatch(/createDriversApi|listDrivers|listOrganizationDrivers\(|\/drivers\b/);
    // The rows keep the server's order and their date names.
    expect(shell).toContain("routes.map((route) => {");
    expect(shell).toContain("`${routeName(route)} · ${driver}`");
  });
});
