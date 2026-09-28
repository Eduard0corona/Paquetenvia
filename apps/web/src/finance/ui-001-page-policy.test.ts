import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, resolve } from "node:path";
import { describe, expect, it } from "vitest";

const openApi = readFileSync(
  resolve(process.cwd(), "../../docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml"),
  "utf8",
);

const ui001Sources = [
  "src/app/ops/orders/new/page.tsx",
  "src/app/finance/settlements/page.tsx",
  "src/operations/api/orders-api.ts",
  "src/operations/api/tenant-request.ts",
  "src/operations/contracts/capabilities.ts",
  "src/operations/contracts/create-order.ts",
  "src/operations/contracts/money.ts",
  "src/operations/components/create-order-shell.tsx",
  "src/operations/state/active-role.ts",
  "src/operations/state/create-order-controller.ts",
  "src/operations/state/external-store.ts",
  "src/operations/state/pending-submissions.ts",
  "src/operations/state/tenant-error-messages.ts",
  "src/operations/state/use-create-order.ts",
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

describe("UI-001 orders and settlements page policy", () => {
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
    expect([...paths].sort()).toEqual(["/api/v1/orders", "/api/v1/quotes", "/api/v1/settlements", "/api/v1/settlements/"]);
    for (const [path, operationId] of [
      ["/quotes:", "createQuote"],
      ["/orders:", "createOrder"],
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
    const settlementsApi = read("src/finance/api/settlements-api.ts");
    for (const suffix of ["/adjustments", "/approve", "/pay", "/void", "/export.csv"])
      expect(settlementsApi).toContain(`"${suffix}"`);
  });

  it("does not render coordinates on the settlements screen", () => {
    for (const path of listSources("src/finance")) {
      expect(read(path), path).not.toMatch(/\blat\b|\blng\b|latitude|longitude/i);
    }
  });

  it("keeps /finance network-only and private no-store", () => {
    const worker = read("public/sw.js");
    expect(worker).toContain('url.pathname === "/finance"');
    expect(worker).toContain('url.pathname.startsWith("/finance/")');
    expect(read("next.config.ts")).toContain('{ source: "/finance/:path*", headers: privateNoStore }');
  });

  it("builds no CSV import, incident or COD screen without an AI-07 contract", () => {
    for (const path of ui001Sources) {
      expect(read(path), path).not.toMatch(/csv\/preview|csv\/commit|\/incidents|cod-records/);
    }
  });
});
