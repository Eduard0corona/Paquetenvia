import { readFileSync, statSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

const read = (path: string) => readFileSync(path, "utf8");

/** Every source of the "Órdenes" screen (UI-PHASE3-ORDERS-LIST-2026-10-12). */
const orderListSources = [
  "src/app/ops/orders/page.tsx",
  "src/operations/api/order-list-api.ts",
  "src/operations/components/operations-orders-shell.tsx",
  "src/operations/contracts/order-list.ts",
  "src/operations/state/order-list-controller.ts",
  "src/operations/state/use-order-list.ts",
];

const shell = () => read("src/operations/components/operations-orders-shell.tsx");

describe("orders list page policy (UI-PHASE3-ORDERS-LIST-2026-10-12)", () => {
  it("is declared in AI-07 with its screen contract and served under /ops", () => {
    const aiSeven = read(resolve(process.cwd(), "../../docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml"));
    expect(aiSeven).toContain("\n  /ops/orders:\n");
    expect(aiSeven).toContain("screen_contract: order_list");
    expect(aiSeven).toContain("\n  order_list:\n");
    expect(aiSeven).toContain("decision: UI-PHASE3-ORDERS-LIST-2026-10-12");
    expect(aiSeven).toContain("ORD-LIST-DATE-FILTER-2026-10-12");
    expect(aiSeven).not.toContain("Órdenes menu item of the approved menu - no screen yet");
    expect(statSync("src/app/ops/orders/page.tsx").isFile()).toBe(true);
    expect(read("src/app/ops/layout.tsx")).toContain("<AppShell>");
  });

  it("keeps the list, the typed guía and the filters in memory or the URL only and never logs them", () => {
    for (const path of orderListSources) {
      const source = read(path);
      expect(source, path).not.toMatch(/localStorage|sessionStorage|indexedDB|caches\.|console\.|sendBeacon|document\.cookie/);
    }
    // The guía search never goes to the URL; only status and the period do.
    const contract = read("src/operations/contracts/order-list.ts");
    const viewSearch = contract.slice(contract.indexOf("export function orderListViewSearch"));
    expect(viewSearch.slice(0, viewSearch.indexOf("\n}\n"))).not.toMatch(/public_id|guide|publicId/);
  });

  it("reads only listOrders and shows no personal data or internal id", () => {
    const paths = new Set<string>();
    for (const path of orderListSources) {
      for (const match of read(path).matchAll(/["`](\/api\/v1\/[^"`?$]*)/g)) paths.add(match[1]);
      expect(read(path), path).not.toMatch(/\blat\b|\blng\b|latitude|longitude|phone|tel[eé]fono|email|correo|address|direcci[oó]n/i);
    }
    expect([...paths]).toEqual(["/api/v1/orders"]);
    const source = shell();
    expect(source).toContain("{row.public_id}");
    expect(source).not.toMatch(/>\s*\{row\.id\}/);
    expect(source).toContain("<StatusBadge status={row.status} showGroup={false} />");
    expect(source).toContain("<Money cents={row.total_cents} />");
    expect(source).toContain("Fechas en hora de Mazatlán.");
    expect(source).not.toMatch(/toLocale(Date|Time)?String|new Intl\.DateTimeFormat|toFixed|parseFloat/);
  });

  it("opens the existing order detail and validates the guía like the top-bar search", () => {
    const source = shell();
    expect(source).toContain("operationsOrderHref(row.id)");
    expect(source).toContain("canOpenDetail ? operationsOrderHref(row.id) : null");
    expect(source).toContain("normalizePublicIdInput(value)");
    expect(source).toContain("orderNotFoundMessage");
    // The only actions: search, filters, Actualizar, Cargar más; no order is changed here.
    expect(source).not.toMatch(/type="checkbox"|Asignar|Cancelar orden|transitionOrder|Idempotency/);
  });

  it("shows loading, empty, error, period, access and MFA states and refreshes on visibility and focus", () => {
    const source = shell();
    expect(source).toContain("Cargando órdenes…");
    expect(source).toContain("Tu organización todavía no tiene órdenes.");
    expect(source).toContain("Sin órdenes con estos filtros");
    expect(source).toContain("{state.error}");
    expect(source).toContain("{state.rangeError}");
    expect(source).toContain("<ScreenGate");
    expect(source).toContain("<Feedback");
    const page = read("src/app/ops/orders/page.tsx");
    expect(page).toContain("<Suspense");
    const hook = read("src/operations/state/use-order-list.ts");
    expect(hook).toContain('document.addEventListener("visibilitychange", onVisibility);');
    expect(hook).toContain('window.addEventListener("focus", onFocus);');
    expect(hook).toContain("subscribeToOperationsSession(() => void controller.start())");
    expect(hook).toContain("controller.stop();");
  });
});
