import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { rateLimitMessage } from "../lib/rate-limit-wait";
import { realtimeReloadSpacingMilliseconds } from "./state/reload-spacer";

/**
 * SEC-004 on the panel: AI-07 `panel_rate_limit` records owner answers Q6 (SEC-004-HEAVY-ENDPOINT-MAPPING-2026-10-12),
 * Q9 and Q10 (SEC-004-RELOADS-AND-BULK-2026-10-13); the code shows that text on every screen that calls a limited
 * operation, and AI-05 limits exactly those operations.
 */
const repository = (path: string) => readFileSync(resolve(process.cwd(), "../..", path), "utf8");
const web = (path: string) => readFileSync(resolve(process.cwd(), path), "utf8");
// Comments wrap with a leading "#" on every line; the quotes are read across those wraps.
const uiContracts = repository("docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml")
  .replace(/\n\s*# ?/g, "\n")
  .replace(/\s+/g, " ");
const openApi = repository("docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml");

/** Screen file, the wait it shows, and how it shows it. */
const screens: readonly (readonly [string, string])[] = [
  ["src/operations/components/operations-dashboard-shell.tsx", "<RateLimitNotice operations={dashboardRateLimit} />"],
  ["src/operations/components/operations-inbox-shell.tsx", "<RateLimitNotice operations={inboxRateLimit} />"],
  ["src/operations/components/operations-order-detail-shell.tsx", "<RateLimitNotice operations={orderDetailRateLimit} />"],
  ["src/operations/components/operations-driver-assignment.tsx", "<RateLimitNotice operations={assignableDriversRateLimit} />"],
  ["src/operations/components/operations-bulk-assignment.tsx", "<RateLimitNotice operations={assignableDriversRateLimit} />"],
  ["src/operations/components/operations-orders-shell.tsx", "rateLimit={orderListRateLimit}"],
  ["src/operations/components/create-order-wizard.tsx", "rateLimit={quoteRateLimit}"],
  ["src/operations/components/csv-import-shell.tsx", "rateLimit={csvImportRateLimit}"],
  ["src/operations/components/manual-routes-shell.tsx", "<RateLimitNotice operations={orderSearchRateLimit} />"],
  ["src/finance/components/cod-shell.tsx", "<RateLimitNotice operations={codPendingRateLimit} />"],
  ["src/finance/components/settlements-shell.tsx", "rateLimit={settlementExportRateLimit}"],
  ["src/components/app-shell/order-search-box.tsx", "rateLimitMessage(waitSeconds)"],
];

describe("panel rate limit policy (SEC-004)", () => {
  it("records the owner's answers, text and behavior in AI-07", () => {
    expect(uiContracts).toContain(
      "panel_rate_limit: decisions: - SEC-004-HEAVY-ENDPOINT-MAPPING-2026-10-12 - SEC-004-RELOADS-AND-BULK-2026-10-13",
    );
    expect(uiContracts).toContain("message: Demasiadas solicitudes. Espera N segundos e intenta de nuevo.");
    expect(uiContracts).toContain('y el botón se reactiva solo.');
    expect(uiContracts).toContain("El panel junta los avisos y recarga como máximo cada 3 segundos por pestaña.");
    expect(uiContracts).toContain("Si pasa, el panel pide esperar unos segundos.");
    expect(uiContracts).toContain("at most one every 3 seconds per tab");
  });

  it("shows exactly the owner's text, with N in whole seconds", () => {
    expect(rateLimitMessage(12)).toBe("Demasiadas solicitudes. Espera 12 segundos e intenta de nuevo.");
    expect(realtimeReloadSpacingMilliseconds).toBe(3_000);
  });

  it.each(screens)("%s shows the wait of its operations", (path, usage) => {
    expect(web(path)).toContain(usage);
  });

  it("limits in AI-05 every operation a panel screen waits for", () => {
    for (const operation of [
      "previewOrderCsv",
      "commitOrderCsv",
      "exportSettlementCsv",
      "createQuote",
      "listOrders",
      "listAssignableDrivers",
      "getOperationsQueueCounts",
    ]) {
      const start = openApi.indexOf(`operationId: ${operation}`);
      const next = openApi.indexOf("operationId:", start + 1);
      expect(start, operation).toBeGreaterThan(-1);
      expect(openApi.slice(start, next), operation).toContain("$ref: '#/components/responses/TooManyRequests'");
    }
  });

  it("gathers realtime notices instead of reloading on each one", () => {
    const dashboard = web("src/operations/state/use-operations-dashboard.ts");
    expect(dashboard).toContain('refreshOperations: () => spacer.request("realtime", 250)');
    expect(dashboard).toContain('refreshLocation: () => spacer.request("location", 500)');
    const detail = web("src/operations/state/use-operations-order-detail.ts");
    expect(detail).toContain('refreshOperations: () => spacer.request("realtime", 250)');
    const inbox = web("src/operations/state/use-operations-inbox.ts");
    expect(inbox).toContain("refreshOperations: () => controller.notice()");
    for (const source of [dashboard, detail, inbox]) expect(source).not.toMatch(/setTimeout\([^)]*,\s*(250|500)\)/);
  });
});
