import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

const read = (path: string) => readFileSync(path, "utf8");

/** Every source of the work inbox (UI-PHASE3-INBOX-2026-10-10). */
const inboxSources = [
  "src/app/ops/inbox/page.tsx",
  "src/operations/components/operations-inbox-shell.tsx",
  "src/operations/contracts/inbox.ts",
  "src/operations/state/inbox-controller.ts",
  "src/operations/state/inbox-merge.ts",
  "src/operations/state/use-operations-inbox.ts",
  // UI-PHASE3-INBOX-PRICE-REVIEW-BULK-2026-10-11: "Asignar seleccionadas…".
  "src/operations/components/operations-bulk-assignment.tsx",
  "src/operations/contracts/bulk-assignment.ts",
  "src/operations/state/bulk-assignment-controller.ts",
];

const shell = () => read("src/operations/components/operations-inbox-shell.tsx");

describe("work inbox page policy (UI-PHASE3-INBOX-2026-10-10)", () => {
  it("is declared in AI-07 as the landing of DISPATCHER and PLATFORM_ADMIN", () => {
    const aiSeven = read(resolve(process.cwd(), "../../docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml"));
    expect(aiSeven).toContain("\n  /ops/inbox:\n");
    expect(aiSeven).toContain("decision: UI-PHASE3-INBOX-2026-10-10");
    const landing = read("src/auth/bff-session-installation.ts");
    expect(landing).toContain('if (canOpenWorkInbox(role)) return "/ops/inbox";');
  });

  it("keeps tenant data in memory only and never logs it", () => {
    for (const path of inboxSources) {
      const source = read(path);
      expect(source, path).not.toMatch(/localStorage|sessionStorage|indexedDB|caches\.|console\.|sendBeacon/);
      expect(source, path).not.toMatch(/parseFloat|toFixed/);
    }
  });

  it("reads only existing operations (dashboard, queue counts, assignable drivers, assignDriver), never a new endpoint", () => {
    for (const path of inboxSources) {
      const source = read(path);
      expect(source, path).not.toMatch(/\bfetch\(|\/api\/v1\//);
    }
    const hook = read("src/operations/state/use-operations-inbox.ts");
    expect(hook).toContain("createOperationsApi(clientApiBaseUrl(), session)");
    expect(hook).toContain("createOperationsDashboardRealtime(");
    const controller = read("src/operations/state/inbox-controller.ts");
    expect(controller).toContain("inboxQueries(this.getSnapshot().view)");
    expect(controller).toContain("api.queueCounts(signal)");
    // The bulk assignment calls the order detail's operations, one order at a time.
    const bulk = read("src/operations/components/operations-bulk-assignment.tsx");
    expect(bulk).toContain("createAssignmentApi(apiBaseUrl, session)");
    const bulkController = read("src/operations/state/bulk-assignment-controller.ts");
    expect(bulkController).toContain("this.api.listAssignableDrivers(orderId, cursor, this.abort.signal)");
    expect(bulkController).toContain("await this.api.assignDriver(");
    expect(bulkController).not.toMatch(/Promise\.all|transitionOrder|routes/);
  });

  it("shows server counts on every queue tab, price review a server list like the others", () => {
    const source = shell();
    expect(source).toContain("inboxQueueCount(state.counts, id)");
    expect(source).toContain('"Sin dato"');
    expect(source).toContain("sin aplicar filtros");
    // UI-PHASE3-INBOX-PRICE-REVIEW-BULK-2026-10-11: every tab is a link to its server list; the
    // count-only tile and its note are gone.
    expect(source).not.toMatch(/opsInboxQueueStatic|priceReviewQueue|Solo conteo/);
    const tabs = source.slice(source.indexOf("inboxQueueTabs.map((id) => ("), source.indexOf("</nav>"));
    expect(tabs).toContain("<Link");
    expect(read("src/operations/contracts/inbox.ts")).toContain('if (queue.flag === "priceReview") return [{ ...shared, priceReview: true, ...status }];');
  });

  it("offers Abrir and Asignar per row, reusing the driver picker, and bulk assignment only through a preview", () => {
    const source = shell();
    expect(source).toContain("inboxOrderHref(row.order_id, view)");
    expect(source).toContain("<OperationsDriverAssignment");
    expect(source).toContain("{row.unassigned_alert && (");
    expect(source).toContain('onOrderChanged={() => state.refresh("assignment")}');
    // Checkboxes only on rows the server flags as assignable, "Seleccionar todas", a capped batch and
    // the approved footer; no bulk status change exists.
    expect(source.match(/type="checkbox"/g)).toHaveLength(2);
    expect(source).toContain("{row.unassigned_alert && (\n            <label>");
    expect(source).toContain("Seleccionar todas");
    expect(source).toContain("Asignar seleccionadas…");
    expect(source).toContain("selected.length >= bulkSelectionLimit");
    expect(source).toContain("Las acciones en lote siempre muestran una vista previa antes de ejecutarse.");
    expect(source).toContain("inboxFooterText({");
    expect(source).not.toMatch(/Cambiar estado|transitionOrder|Agregar a ruta/);
    expect(source).toContain('onChanged={() => state.refresh("assignment")}');
    // The batch belongs to the session, not to the view: changing the queue never stops a running batch.
    expect(source).toContain("batch.sessionGeneration === state.sessionGeneration ? batch : null");
    const bulk = read("src/operations/components/operations-bulk-assignment.tsx");
    expect(bulk).toContain("<strong>Vista previa:</strong>");
    expect(bulk).toContain("bulkAssignmentConfirmation(run.driver, state.orders.length, check.totalCents)");
    expect(bulk).toContain("parseBulkCosts(state.orders, typed)");
    expect(bulk).toContain("eligibility.eligible.map((choice) => (");
    // Drivers by their DRV- reference, ids never shown, times in Mazatlán; the only amount is the order
    // total (UI-PHASE3-INBOX-TOTAL-2026-10-10), integer cents through the shared Money component.
    expect(source).toContain("row.assignment?.driver_reference");
    expect(source).not.toMatch(/>\s*\{row\.(order_id|assignment\??\.driver_id)\}/);
    expect(source).toContain("<Money cents={row.total.amount_cents} />");
    expect(source).toContain("Total ({vatIncludedLabel})");
    expect(source.match(/<Money\b/g)).toHaveLength(1);
    expect(source).not.toMatch(/cost_cents|margin|cod_|price_net/);
    expect(source).toContain("formatServiceWindow(row.delivery_window)");
    expect(source).toContain("Horarios mostrados en hora de Mazatlán.");
    expect(source).not.toMatch(/toLocale(Date|Time)?String|new Intl\.DateTimeFormat/);
  });

  it("keeps the queue and the chips in the URL and filters only on the server", () => {
    const source = shell();
    expect(source).toContain("useSearchParams()");
    expect(source).toContain("router.replace(inboxHref(next), { scroll: false })");
    // No queue or chip is computed by filtering the loaded rows.
    expect(source).not.toMatch(/rows\.filter\(|state\.rows\.filter\(/);
    expect(read("src/app/ops/inbox/page.tsx")).toContain("<Suspense");
  });

  it("returns from the order detail to the same inbox view", () => {
    const page = read("src/app/ops/orders/[orderId]/page.tsx");
    expect(page).toContain("inboxHref={inboxReturnHref(query[inboxReturnParameter])}");
    const detail = read("src/operations/components/operations-order-detail-shell.tsx");
    expect(detail).toContain("Volver a la bandeja");
    expect(detail).toContain("inboxHref === null ? null");
    // The detail keeps a single page note (the Playwright detail check reads it).
    expect(detail.match(/className="pageNote"/g)).toHaveLength(1);
  });

  it("keeps the dashboard and its positions view reachable as Tablero", () => {
    const nav = read("src/components/app-shell/nav-items.ts");
    expect(nav).toContain('href: "/ops/dashboard",\n    label: "Tablero"');
    expect(shell()).toContain('href="/ops/dashboard?view=positions"');
    const dashboard = read("src/operations/components/operations-dashboard-shell.tsx");
    // The view comes from the URL while rendering (contracts/dashboard-view.ts, tested there).
    expect(dashboard).toContain("parseDashboardView(searchParams)");
    expect(dashboard).toContain("<OperationsPositions");
  });
});
