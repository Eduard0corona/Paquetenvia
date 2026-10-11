import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, resolve } from "node:path";
import { describe, expect, it } from "vitest";

const openApi = readFileSync(
  resolve(process.cwd(), "../../docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml"),
  "utf8",
);
const aiSeven = readFileSync(
  resolve(process.cwd(), "../../docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml"),
  "utf8",
);

/** Sources of the CSV import, incident and COD screens (UI-001-SCREENS-CSV-INC-COD). */
const newScreenSources = [
  "src/app/ops/orders/import/page.tsx",
  "src/app/ops/incidents/page.tsx",
  "src/app/finance/cod/page.tsx",
  "src/operations/api/csv-import-api.ts",
  "src/operations/api/incidents-api.ts",
  "src/operations/components/csv-import-shell.tsx",
  "src/operations/components/incidents-shell.tsx",
  "src/components/ui/feedback.tsx",
  "src/operations/contracts/csv-import.ts",
  "src/operations/contracts/incident.ts",
  "src/operations/contracts/strict-json.ts",
  "src/operations/state/csv-import-controller.ts",
  "src/operations/state/incidents-controller.ts",
  "src/operations/state/use-csv-import.ts",
  "src/operations/state/use-incidents.ts",
];

const ui001Sources = [
  "src/app/ops/orders/new/page.tsx",
  "src/app/finance/settlements/page.tsx",
  "src/operations/api/orders-api.ts",
  "src/operations/api/tenant-request.ts",
  "src/operations/contracts/capabilities.ts",
  "src/operations/contracts/create-order.ts",
  "src/operations/contracts/money.ts",
  "src/operations/contracts/order-wizard.ts",
  "src/operations/components/create-order-shell.tsx",
  "src/operations/components/create-order-summary.tsx",
  "src/operations/components/create-order-wizard.tsx",
  "src/operations/components/focus-later.ts",
  "src/operations/state/active-role.ts",
  "src/operations/state/create-order-controller.ts",
  "src/operations/state/external-store.ts",
  "src/operations/state/pending-submissions.ts",
  "src/operations/state/tenant-error-messages.ts",
  "src/operations/state/use-create-order.ts",
  ...newScreenSources,
  ...listSources("src/finance"),
];

function listSources(directory: string): string[] {
  return readdirSync(directory).flatMap((name) => {
    const path = join(directory, name);
    if (statSync(path).isDirectory()) return listSources(path);
    return /\.tsx?$/.test(name) && !/\.(test|fixtures)\.ts$/.test(name) ? [path] : [];
  });
}

const read = (path: string) => readFileSync(path, "utf8");

describe("UI-001 page policy", () => {
  it("never persists tenant data in the browser nor logs it", () => {
    for (const path of ui001Sources) {
      const source = read(path);
      expect(source, path).not.toMatch(/localStorage|sessionStorage|indexedDB|caches\./);
      expect(source, path).not.toMatch(/console\.|sendBeacon/);
    }
  });

  it("does money in integer cents only", () => {
    for (const path of ui001Sources) {
      const source = read(path);
      expect(source, path).not.toMatch(/parseFloat|toFixed|cents\s*\/\s*100\b|\*\s*100\)/);
    }
  });

  it("calls only AI-05 REST paths", () => {
    const paths = new Set<string>();
    for (const path of ui001Sources) {
      for (const match of read(path).matchAll(/["`](\/api\/v1\/[^"`?$]*)/g)) paths.add(match[1]);
    }
    expect([...paths].sort()).toEqual([
      "/api/v1/cod-records/",
      "/api/v1/incidents",
      "/api/v1/incidents/",
      "/api/v1/orders",
      "/api/v1/orders/",
      "/api/v1/orders/csv/commit",
      "/api/v1/orders/csv/preview",
      "/api/v1/quotes",
      "/api/v1/settlements",
      "/api/v1/settlements/",
    ]);
    for (const [path, operationId] of [
      ["/quotes:", "createQuote"],
      ["/orders:", "createOrder"],
      ["/orders/csv/preview:", "previewOrderCsv"],
      ["/orders/csv/commit:", "commitOrderCsv"],
      ["/orders/{orderId}/incidents:", "openIncident"],
      ["/incidents/{incidentId}/resolution:", "resolveIncident"],
      ["/incidents:", "listIncidents"],
      ["/incidents/{incidentId}:", "getIncident"],
      ["/orders/{orderId}/cod-records:", "recordCodCollection"],
      ["/cod-records/{codId}/reconcile:", "reconcileCod"],
      ["/orders/{orderId}/financials:", "getOrderFinancials"],
      ["/settlements:", "createSettlement"],
      ["/settlements/{settlementId}:", "getSettlement"],
      ["/settlements/{settlementId}/adjustments:", "addSettlementAdjustment"],
      ["/settlements/{settlementId}/approve:", "approveSettlement"],
      ["/settlements/{settlementId}/pay:", "markSettlementPaid"],
      ["/settlements/{settlementId}/void:", "voidSettlement"],
      ["/settlements/{settlementId}/export.csv:", "exportSettlementCsv"],
    ]) {
      const start = openApi.indexOf(`\n  ${path}\n`);
      expect(start, path).toBeGreaterThanOrEqual(0);
      expect(openApi.slice(start, start + 600), path).toContain(`operationId: ${operationId}`);
    }
    expect(openApi).toContain("operationId: listSettlements");
    // API-INC-LIST-PROOFS-2026-09-29: listOrderProofs shares its path with finalizeProof.
    expect(openApi).toContain("operationId: listOrderProofs");
    const incidentsApi = read("src/operations/api/incidents-api.ts");
    for (const suffix of ["/incidents`", "/resolution`", "/proofs`", '"/api/v1/incidents"'])
      expect(incidentsApi).toContain(suffix);
    const codApi = read("src/finance/api/cod-api.ts");
    for (const suffix of ["/financials`", "/cod-records`", "/reconcile`"]) expect(codApi).toContain(suffix);
    const settlementsApi = read("src/finance/api/settlements-api.ts");
    for (const suffix of ["/adjustments", "/approve", "/pay", "/void", "/export.csv"])
      expect(settlementsApi).toContain(`"${suffix}"`);
  });

  it("does not render coordinates on the settlements, CSV, incident or COD screens", () => {
    for (const path of [...listSources("src/finance"), ...newScreenSources]) {
      expect(read(path), path).not.toMatch(/\blat\b|\blng\b|latitude|longitude/i);
    }
  });

  it("keeps /finance network-only and private no-store", () => {
    const worker = read("public/sw.js");
    expect(worker).toContain('url.pathname === "/finance"');
    expect(worker).toContain('url.pathname.startsWith("/finance/")');
    expect(read("next.config.ts")).toContain('{ source: "/finance/:path*", headers: privateNoStore }');
  });

  it("builds the CSV import, incident and COD screens only under their AI-07 contracts", () => {
    expect(aiSeven).toContain("UI-001-SCREENS-CSV-INC-COD");
    for (const [route, contract] of [
      ["/ops/orders/import", "csv_order_import"],
      ["/ops/incidents", "incident_desk"],
      ["/finance/cod", "cod_control"],
    ]) {
      expect(aiSeven).toContain(`\n  ${route}:\n`);
      expect(aiSeven).toContain(`screen_contract: ${contract}`);
      expect(aiSeven).toContain(`\n  ${contract}:\n`);
      expect(statSync(join("src/app", route, "page.tsx")).isFile()).toBe(true);
    }
    // Each operation is called from exactly one API client.
    const owners: ReadonlyArray<[RegExp, string]> = [
      [/csv\/preview|csv\/commit/, "src/operations/api/csv-import-api.ts"],
      [/\/incidents`|\/resolution`/, "src/operations/api/incidents-api.ts"],
      [/cod-records|\/financials`/, "src/finance/api/cod-api.ts"],
    ];
    for (const path of ui001Sources) {
      for (const [pattern, owner] of owners)
        if (path !== owner) expect(read(path), path).not.toMatch(pattern);
    }
  });

  it("uploads CSV bytes only as the AI-05 multipart part, never with the local file name", () => {
    const api = read("src/operations/api/csv-import-api.ts");
    expect(api).toContain('form.append("file"');
    expect(api).toContain("csvUploadFilename");
    for (const path of ui001Sources) {
      const source = read(path);
      expect(source, path).not.toMatch(/\bfile\.name\b|\.webkitRelativePath/);
      if (path !== "src/operations/api/csv-import-api.ts") expect(source, path).not.toMatch(/new FormData\(\)/);
    }
  });
});
