import { readFileSync, statSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

const read = (path: string) => readFileSync(path, "utf8");

/** Every source of the "Repartidores" screen (UI-PHASE3-DRIVERS-2026-10-11; DRV-DOC-001-IN-PERSON-2026-11-02). */
const driverSources = [
  "src/app/ops/drivers/page.tsx",
  "src/operations/api/drivers-api.ts",
  "src/operations/api/driver-documents-api.ts",
  "src/operations/components/operations-drivers-shell.tsx",
  "src/operations/components/operations-driver-documents.tsx",
  "src/operations/contracts/organization-driver.ts",
  "src/operations/contracts/driver-document.ts",
  "src/operations/state/drivers-controller.ts",
  "src/operations/state/use-drivers.ts",
  "src/operations/state/use-minute-clock.ts",
];

const shell = () => read("src/operations/components/operations-drivers-shell.tsx");
const documentsDialog = () => read("src/operations/components/operations-driver-documents.tsx");

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

  it("reads only listOrganizationDrivers and the document operations and shows no coordinate, personal data or internal id", () => {
    const paths = new Set<string>();
    for (const path of driverSources) {
      for (const match of read(path).matchAll(/["`](\/api\/v1\/[^"`?$]*)/g)) paths.add(match[1]);
      expect(read(path), path).not.toMatch(/\blat\b|\blng\b|latitude|longitude|accuracy|phone|tel[eé]fono|email|correo/i);
    }
    // listOrganizationDrivers, listDriverDocuments, and the record and revoke paths under /drivers/{driverId}/.
    expect([...paths].sort()).toEqual(["/api/v1/drivers", "/api/v1/drivers/", "/api/v1/drivers/documents"]);
    const api = read("src/operations/api/driver-documents-api.ts");
    expect(api).toContain("`/api/v1/drivers/${encodeURIComponent(driverId)}/documents`");
    expect(api).toContain(
      "`/api/v1/drivers/${encodeURIComponent(driverId)}/documents/${encodeURIComponent(documentId)}/revoke`",
    );
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

  it("offers no action on a driver itself: only Actualizar and Cargar más", () => {
    const source = shell();
    const buttons = [...source.matchAll(/<button[\s\S]*?>\s*([^<{]*)/g)];
    expect(buttons.length).toBe(2);
    expect(source).toContain("Actualizar");
    expect(source).toContain('"Cargar más"');
    expect(source).not.toMatch(/<Link|href=|<form|type="checkbox"|type="radio"|Asignar|Dar de (alta|baja)|Eliminar/);
  });

  it("offers on documents only what AI-07 lists, and never a photo, a copy, a file or a document number (Y3)", () => {
    const source = documentsDialog();
    // Documentos, Revocar, Cerrar, Registrar documento, Cancelar and the form's Registrar: nothing else.
    expect(source.match(/<button\b/g)).toHaveLength(6);
    const labels = [...source.matchAll(/>\s*(Documentos|Revocar|Cerrar|Registrar documento|Cancelar)\s*<\/button>/g)].map(
      (match) => match[1],
    );
    expect(labels.sort()).toEqual(["Cancelar", "Cerrar", "Documentos", "Registrar documento", "Revocar"]);
    expect(source).toContain('{pending ? "Registrando…" : "Registrar"}');
    expect(source).not.toMatch(/type="file"|capture=|getUserMedia|FileReader|<img|<canvas|<video/);
    const code = source.replace(/\/\*[\s\S]*?\*\//g, "").replace(/^\s*\/\/.*$/gm, "");
    expect(code).not.toMatch(/n[uú]mero|folio|foto|photo|document_number|object_key|sha256/i);
    // Only the dispatch operations: nothing of the driver's own upload (registerMyDriverDocument stays reserved).
    expect(read("src/operations/api/driver-documents-api.ts")).not.toMatch(/registerMyDriverDocument|\/driver\/me\//);
    // The documents part exists only while listDriverDocuments answers documents_enabled true (Y2).
    expect(shell()).toContain("{state.documentsEnabled && <th scope=\"col\">Documentos</th>}");
    expect(shell()).toContain("state.documentsEnabled && openDriverId !== null");
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
