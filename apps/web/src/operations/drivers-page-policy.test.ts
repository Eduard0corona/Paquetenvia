import { readFileSync, statSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

const read = (path: string) => readFileSync(path, "utf8");

/** Every source of the "Repartidores" screen (UI-PHASE3-DRIVERS-2026-10-11). */
const driverSources = [
  "src/app/ops/drivers/page.tsx",
  "src/operations/api/drivers-api.ts",
  "src/operations/components/operations-drivers-shell.tsx",
  "src/operations/contracts/organization-driver.ts",
  "src/operations/state/drivers-controller.ts",
  "src/operations/state/use-drivers.ts",
];

const shell = () => read("src/operations/components/operations-drivers-shell.tsx");

describe("drivers page policy (UI-PHASE3-DRIVERS-2026-10-11)", () => {
  it("is declared in AI-07 with its screen contract and served under /ops", () => {
    const aiSeven = read(resolve(process.cwd(), "../../docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml"));
    expect(aiSeven).toContain("\n  /ops/drivers:\n");
    expect(aiSeven).toContain("screen_contract: driver_roster");
    expect(aiSeven).toContain("\n  driver_roster:\n");
    expect(aiSeven).toContain("decision: UI-PHASE3-DRIVERS-2026-10-11");
    expect(statSync("src/app/ops/drivers/page.tsx").isFile()).toBe(true);
    expect(read("src/app/ops/layout.tsx")).toContain("<AppShell>");
  });

  it("keeps driver data in memory only and never logs it", () => {
    for (const path of driverSources) {
      const source = read(path);
      expect(source, path).not.toMatch(/localStorage|sessionStorage|indexedDB|caches\.|console\.|sendBeacon/);
    }
  });

  it("reads only listOrganizationDrivers and shows no coordinate, personal data or internal id", () => {
    const paths = new Set<string>();
    for (const path of driverSources) {
      for (const match of read(path).matchAll(/["`](\/api\/v1\/[^"`?$]*)/g)) paths.add(match[1]);
      expect(read(path), path).not.toMatch(/\blat\b|\blng\b|latitude|longitude|accuracy|phone|tel[eé]fono|email|correo/i);
    }
    expect([...paths]).toEqual(["/api/v1/drivers"]);
    const source = shell();
    expect(source).toContain("{driver.driver_reference}");
    expect(source).not.toMatch(/>\s*\{driver\.driver_id\}/);
    expect(source).toContain("vehicleLabel(driver.vehicle_type)");
    expect(source).toContain("organizationDriverStatusLabels[driver.status]");
    expect(source).toContain("activeStopsText(driver.active_stop_count)");
    expect(source).toContain("<DateTime value={driver.last_position_at} />");
    expect(source).toContain("Horarios mostrados en hora de Mazatlán.");
    expect(source).not.toMatch(/toLocale(Date|Time)?String|new Intl\.DateTimeFormat/);
  });

  it("offers no action on a driver: only Actualizar and Cargar más", () => {
    const source = shell();
    const buttons = [...source.matchAll(/<button[\s\S]*?>\s*([^<{]*)/g)];
    expect(buttons.length).toBe(2);
    expect(source).toContain("Actualizar");
    expect(source).toContain('"Cargar más"');
    expect(source).not.toMatch(/<Link|href=|<form|type="checkbox"|type="radio"|Asignar|Dar de (alta|baja)|Eliminar/);
  });

  it("shows loading, empty, error and access states and refreshes on visibility and focus", () => {
    const source = shell();
    expect(source).toContain("Cargando repartidores…");
    expect(source).toContain("Tu flota todavía no tiene repartidores registrados.");
    expect(source).toContain("{state.error}");
    expect(source).toContain("<ScreenGate");
    expect(source).toContain("<Feedback");
    const hook = read("src/operations/state/use-drivers.ts");
    expect(hook).toContain('document.addEventListener("visibilitychange", onVisibility);');
    expect(hook).toContain('window.addEventListener("focus", onFocus);');
    expect(hook).toContain("subscribeToOperationsSession(() => void controller.start())");
    expect(hook).toContain("controller.stop();");
  });
});
