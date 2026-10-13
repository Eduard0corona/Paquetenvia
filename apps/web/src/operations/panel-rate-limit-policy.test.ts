import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { rateLimitFallbackSeconds, rateLimitMessage } from "../lib/rate-limit-wait";
import { realtimeReloadSpacingMilliseconds } from "./state/screen-reloads";

/**
 * SEC-004 panel part: the owner's answers Q6 ("Mensaje común con espera"), Q9 ("Espaciar recargas"), Q11 ("Reintenta
 * sola"), Q12 ("1 segundo"), Q13 ("Mensaje común"), Q14 ("Se pausan"), Q15 ("La pide sola") and Q16 ("Toda la
 * pantalla"), checked in the sources the node test environment cannot render (React hooks and screens).
 */
const read = (path: string) => readFileSync(path, "utf8");

function sources(directory: string): string[] {
  return readdirSync(directory).flatMap((name) => {
    const path = join(directory, name);
    if (statSync(path).isDirectory()) return sources(path);
    return /\.(ts|tsx)$/.test(name) && !/\.test\.ts$/.test(name) ? [path] : [];
  });
}

describe("SEC-004 panel limits in the sources", () => {
  it("writes the common text in one place only", () => {
    const code = (path: string) =>
      read(path)
        .split("\n")
        .filter((line) => !/^\s*(\*|\/\/|\/\*)/.test(line));
    const writers = sources("src").filter((path) => code(path).some((line) => /["`]Demasiadas solicitudes/.test(line)));
    expect(writers).toEqual([join("src", "lib", "rate-limit-wait.ts")]);
  });

  it("tags every limited operation the panel calls, and only those", () => {
    const tags = (path: string) => [...read(path).matchAll(/rateLimit: "([a-z_]+)"/g)].map((match) => match[1]);
    expect(tags("src/operations/api/csv-import-api.ts")).toEqual(["csv_import", "csv_import"]);
    expect(tags("src/finance/api/settlements-api.ts")).toEqual(["settlement_export"]);
    expect(tags("src/operations/api/orders-api.ts")).toEqual(["quote"]);
    expect(tags("src/operations/api/order-list-api.ts")).toEqual(["order_list"]);
    expect(tags("src/finance/api/cod-api.ts")).toEqual(["order_list"]);
    expect(tags("src/operations/api/order-actions-api.ts")).toEqual(["order_list"]);
    expect(tags("src/operations/api/assignment-api.ts")).toEqual(["assignable_drivers"]);
    const operations = read("src/operations/api/operations-api.ts");
    expect(operations).toMatch(/parseOperationsDashboard,\s*signal,\s*"dashboard",/);
    expect(operations).toMatch(/parseOperationsQueueCounts,\s*signal,\s*"queue_counts",/);
  });

  it("spaces realtime reloads and pauses the dashboard and the order detail during the wait (Q9, Q11, Q14, Q16)", () => {
    const dashboard = read("src/operations/state/use-operations-dashboard.ts");
    expect(dashboard).toContain('export const dashboardRateLimit: readonly RateLimitedOperation[] = ["dashboard", "queue_counts"];');
    expect(dashboard).toContain('refreshOperations: () => reloads.notice("realtime", 250)');
    expect(dashboard).toContain('refreshLocation: () => reloads.notice("location", 500)');
    expect(dashboard).toContain('if (requirement === "normal" && !reloads.admit(reload)) return null;');
    expect(dashboard).toContain("reloads.rateLimited(reload);");
    expect(dashboard).toContain('if (outcome === "rate_limited" && apiRef.current === api) reloads.rateLimited("realtime");');
    expect(dashboard).toContain("await sleepUntilWaitEnds(rateLimitWaits, dashboardRateLimit, waitAbortRef.current.signal);");
    // A session start that met the wait starts once, by itself, when it ends (realtime included).
    expect(dashboard).toContain("if (rateLimitWaits.isWaiting(dashboardRateLimit)) {\n      startAfterWait();");
    // A first read that met the 429 starts the session again at 0 too, so realtime starts (#253 review note).
    expect(dashboard).toMatch(/isRateLimited\(caught\)\) \{[^}]*setError\(null\);\s*startAfterWait\(\);\s*return;/);
    // Until then the screen has no API, so polling and visibility read nothing and the start runs once.
    expect(dashboard).toMatch(/const startAfterWait = \(\) => \{\s*apiRef\.current = null;/);
    expect(dashboard).not.toContain("scheduleRefresh");

    const detail = read("src/operations/state/use-operations-order-detail.ts");
    expect(detail).toContain('export const orderDetailRateLimit: readonly RateLimitedOperation[] = ["dashboard"];');
    expect(detail).toContain('refreshOperations: () => reloads.notice("realtime", 250)');
    expect(detail).toContain('refreshLocation: () => reloads.notice("realtime", 500)');
    expect(detail).toContain('if (requirement === "normal" && !reloads.admit("realtime")) return null;');
    expect(detail).toContain('reloads.rateLimited("realtime");');
    expect(detail).toContain("await sleepUntilWaitEnds(rateLimitWaits, orderDetailRateLimit, waitAbortRef.current.signal);");
    expect(detail).not.toMatch(/timerRef|schedule\(/);

    const inbox = read("src/operations/state/use-operations-inbox.ts");
    expect(inbox).toContain("refreshOperations: () => controller.notice(),");
    expect(inbox).not.toContain("realtimeTimer");
    expect(read("src/operations/state/use-manual-routes.ts")).toContain(
      "RouteChanged: (event) => controller.notice(event.payload.route_id)",
    );
  });

  it("shows the countdown and holds the buttons of every limited screen", () => {
    const screens: ReadonlyArray<readonly [string, readonly string[]]> = [
      ["src/operations/components/operations-dashboard-shell.tsx", [
        "useRateLimitWait(dashboardRateLimit)",
        "disabled={state.loading || state.accessUnavailable || waiting}",
        "disabled={state.loadingMore || waiting}",
      ]],
      ["src/operations/components/operations-inbox-shell.tsx", [
        "useRateLimitWait(inboxRateLimit)",
        "disabled={state.loading || blocked || waiting}",
        "disabled={state.loadingMore || waiting}",
      ]],
      ["src/operations/components/operations-order-detail-shell.tsx", [
        "useRateLimitWait(orderDetailRateLimit)",
        "onClick={state.refresh} disabled={waiting}",
      ]],
      ["src/operations/components/operations-orders-shell.tsx", [
        "useRateLimitWait(orderListRateLimit)",
        "disabled={state.phase !== \"ready\" || state.loading || waiting}",
        "aria-disabled={state.loadingMore || waiting}",
      ]],
      ["src/finance/components/cod-shell.tsx", [
        "useRateLimitWait(codPendingRateLimit)",
        "disabled={state.pendingLoading || waiting}",
      ]],
      ["src/operations/components/csv-import-shell.tsx", [
        "useRateLimitWait(csvImportRateLimit)",
        "disabled={state.busy || state.fileBytes === null || waiting}",
        "disabled={state.busy || waiting}",
      ]],
      ["src/finance/components/settlements-shell.tsx", [
        "useRateLimitWait(settlementExportRateLimit)",
        "disabled={locked || exportWaiting}",
      ]],
      ["src/operations/components/create-order-wizard.tsx", [
        "useRateLimitWait(quoteRateLimit)",
        "aria-disabled={busy || quoteWaiting}",
      ]],
      ["src/operations/components/operations-driver-assignment.tsx", [
        "useRateLimitWait(assignableDriversRateLimit)",
        "aria-disabled={waiting}",
      ]],
      ["src/operations/components/operations-bulk-assignment.tsx", [
        "useRateLimitWait(assignableDriversRateLimit)",
        "onClick={onRetry} disabled={waiting}",
      ]],
      ["src/operations/components/manual-routes-shell.tsx", [
        "useRateLimitWait(orderSearchRateLimit)",
        "disabled={mutating || !draft || rateLimit.secondsLeft > 0}",
      ]],
      ["src/components/app-shell/order-search-box.tsx", [
        "useRateLimitWait(orderSearchRateLimit)",
        "aria-disabled={busy || waiting}",
        "if (!busy && !waiting) void search();",
      ]],
    ];
    for (const [path, expected] of screens) {
      const source = read(path);
      expect(source, path).toMatch(/<RateLimitNoticeView \{\.\.\.rateLimit\}/);
      for (const text of expected) expect(source, `${path}: ${text}`).toContain(text);
    }
  });

  it("keeps the filters usable during the wait: a change is kept and read when it ends (Q14)", () => {
    // The filter controls never read the wait; only "Actualizar" and "Cargar más" do.
    expect(read("src/operations/components/operations-dashboard-shell.tsx")).toContain("disabled={state.accessUnavailable}\n");
    expect(read("src/operations/components/operations-orders-shell.tsx")).toContain(
      "<OrderGuideSearch guide={state.guide} disabled={state.loading} onGuideChange={onGuideChange} />",
    );
  });
});

describe("SEC-004 panel limits in AI-07 (SEC-004-PANEL-SCREENS-2026-10-13)", () => {
  const repository = (path: string) => readFileSync(resolve(process.cwd(), "../..", path), "utf8");
  const ai07 = repository("docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml").replace(/\s+/g, " ");
  const decisions = repository("docs/normative/v0.6/decision-log.md");

  it("records the text, the countdown and the 60-second fallback the code uses", () => {
    expect(ai07).toContain(`message: ${rateLimitMessage(42).replace("42", "N")}`);
    expect(ai07).toContain(`message_at_one_second: ${rateLimitMessage(1)}`);
    expect(ai07).toContain(`N starts at ${rateLimitFallbackSeconds}, the window of every panel limit`);
    expect(realtimeReloadSpacingMilliseconds).toBe(3_000);
    expect(ai07).toContain("gathers them into at most one reload every 3 seconds per tab");
  });

  it("gives every limited panel screen its rate_limit entry under the owner's decision", () => {
    const entries = ai07.match(/rate_limit: decision: SEC-004-PANEL-SCREENS-2026-10-13/g) ?? [];
    // Dashboard, inbox, order detail, its assignment picker, Rutas, Nueva orden, CSV, Cobros, Órdenes, Liquidaciones.
    expect(entries).toHaveLength(10);
    for (const screen of [
      "/ops/dashboard:", "/ops/inbox:", "/ops/orders/:id:", "/ops/orders:", "/finance/cod:", "/ops/orders/new:",
      "/ops/orders/import:", "/finance/settlements:", "/ops/routes:", "app shell: listOrders",
    ]) {
      expect(ai07.slice(ai07.indexOf("panel_rate_limit:"))).toContain(screen);
    }
  });

  it("has the owner's literal answers Q11 to Q16 in the decision log", () => {
    const row = decisions.split("\n").find((line) => line.includes("| SEC-004-PANEL-SCREENS-2026-10-13 |")) ?? "";
    for (const answer of [
      '"Reintenta sola (Recommended)"',
      '""1 segundo" (Recommended)"',
      '"Mensaje común (Recommended)"',
      '"Se pausan (Recommended)"',
      '"La pide sola (Recommended)"',
      '"Toda la pantalla (Recommended)"',
    ]) {
      expect(row).toContain(`Answer: ${answer}`);
    }
    for (const notChosen of [
      '"Espera a la persona"',
      '"Texto fijo"',
      '"Dejarlo como hoy"',
      '"Siempre activos"',
      '"Botón \'Reintentar\'"',
      '"Solo los conteos"',
    ]) {
      expect(row).toContain(`Not chosen: ${notChosen}`);
    }
  });
});
