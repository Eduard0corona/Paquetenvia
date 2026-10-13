import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import { defaultOrderListView, orderListRangeTooLongMessage, type OrderListItem } from "../contracts/order-list";
import type { OrderListState } from "../state/order-list-controller";
import { OrderListRow, OrderListScreen } from "./operations-orders-shell";

// The screen is pure presentation over a state; the shell's hook (session, API, router) is never called here.
vi.mock("../state/use-order-list", () => ({
  useOrderList: () => {
    throw new Error("The list state is not part of this test.");
  },
}));
vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: () => undefined, replace: () => undefined }),
  useSearchParams: () => new URLSearchParams(),
}));

const orderId = "0a1b2c3d-1111-4222-8333-944445555666";
const row: OrderListItem = {
  id: orderId,
  public_id: "ORD_AbCdEfGhIjKlMnOpQrSt_-",
  status: "FAILED_ATTEMPT",
  total_cents: 123_456,
  created_at: null,
};

function state(overrides: Partial<OrderListState> = {}): OrderListState {
  return {
    phase: "ready",
    role: "DISPATCHER",
    canOpenDetail: true,
    view: defaultOrderListView,
    guide: null,
    rows: [],
    nextCursor: null,
    loaded: true,
    loading: false,
    loadingMore: false,
    rangeError: null,
    error: null,
    message: null,
    stepUpHref: null,
    lastUpdated: "2026-10-12T17:00:00.000Z",
    ...overrides,
  };
}

function render(value: OrderListState): string {
  return renderToStaticMarkup(
    createElement(OrderListScreen, {
      state: value,
      onViewChange: () => undefined,
      onGuideChange: () => undefined,
      onRefresh: () => undefined,
      onLoadMore: () => undefined,
      onOpen: () => undefined,
    }),
  );
}

function text(html: string): string {
  return html.replace(/<[^>]+>/g, " ").replace(/\s+/g, " ");
}

/** UI-PHASE3-ORDERS-LIST-2026-10-12: what "Órdenes" shows in each state. */
describe("Órdenes screen", () => {
  it("shows guía, exact status and total in integer cents, linked to the detail, and no identifier", () => {
    const html = render(state({ rows: [row], nextCursor: "c1" }));
    expect(html).toContain("<h1>Órdenes</h1>");
    expect(html).toContain(`href="/ops/orders/${orderId}"`);
    expect(html).toContain(">ORD_AbCdEfGhIjKlMnOpQrSt_-</a>");
    expect(text(html)).toContain("Intento fallido");
    expect(text(html)).toContain("$1,234.56");
    expect(text(html)).toContain("Total (IVA incluido)");
    expect(text(html)).not.toContain(orderId);
    expect(text(html)).toContain("Se muestra 1 orden; hay más por cargar.");
    expect(html).toContain(">Cargar más</button>");
    expect(text(html)).toContain("Fechas en hora de Mazatlán.");
  });

  it("lists without links for a role that cannot open the detail (VIEWER)", () => {
    const html = render(state({ role: "VIEWER", canOpenDetail: false, rows: [row] }));
    expect(html).not.toContain("href=");
    expect(html).not.toContain("opsInboxRow");
    expect(text(html)).toContain("ORD_AbCdEfGhIjKlMnOpQrSt_-");
    expect(text(html)).toContain("El detalle de cada orden lo abren despacho y administración");
  });

  it("offers the guía search, one status or all grouped as everywhere and the Mazatlán period", () => {
    const html = render(state({ view: { status: "DELIVERED", from: "2026-10-01", to: "2026-10-07" } }));
    expect(html).toContain('aria-label="Buscar orden por guía"');
    expect(text(html)).toContain("Guía (ORD_…)");
    expect(html).toContain('<option value="">Todos los estados</option>');
    for (const group of ["Por preparar", "En recolección", "En ruta", "Requiere atención", "Terminadas"])
      expect(html).toContain(`<optgroup label="${group}">`);
    expect(html).toContain('<option value="DELIVERED" selected="">Entregada</option>');
    expect(text(html)).toContain("Creada desde (hora de Mazatlán)");
    expect(text(html)).toContain("Creada hasta (hora de Mazatlán)");
    expect(html).toContain('type="date" min="2000-01-01" value="2026-10-01"');
    expect(html).toContain('type="date" min="2000-01-01" value="2026-10-07"');
    expect(html).not.toContain("aria-invalid");
    expect(html).toContain(">Limpiar filtros</button>");
  });

  // UI-PHASE3-ORDERS-LIST-2026-10-12 (owner, "En dos pasos (Recommended)"): "Creada" only once the API sends it.
  it("adds the Creada column in Mazatlán time only when the API sends created_at", () => {
    const without = render(state({ rows: [row] }));
    expect(without).not.toContain(">Creada</th>");
    expect(without.slice(without.indexOf("<table"))).not.toContain("<time");
    expect(text(without)).not.toContain("Sin dato");
    const dated = { ...row, id: "0a1b2c3d-1111-4222-8333-944445555667", created_at: "2026-10-12T17:04:05.1234567Z" };
    const html = render(state({ rows: [dated, row] }));
    expect(html).toContain('<th scope="col">Estado</th><th scope="col">Creada</th><th scope="col" class="opsAmount">');
    expect(html).toContain('<time dateTime="2026-10-12T17:04:05.1234567Z">12 oct 2026, 10:04</time>');
    // An order without the field in the same page reads "Sin dato".
    expect(text(html)).toContain("Sin dato");
  });

  // ORD-ORDER-CREATED-AT-2026-10-12: the API sends created_at on every Order, written as System.Text.Json writes a UTC
  // DateTimeOffset read from PostgreSQL; the column then shows on its own, in Mazatlán time, for every row.
  it("shows the Creada column for every order once the API sends created_at as the server writes it", () => {
    const rows = [
      { ...row, id: "0a1b2c3d-1111-4222-8333-944445555668", created_at: "2026-10-12T17:04:05.123456+00:00" },
      { ...row, id: "0a1b2c3d-1111-4222-8333-944445555669", created_at: "2026-10-05T06:30:00+00:00" },
    ];
    const html = render(state({ rows }));
    expect(html).toContain('<th scope="col">Estado</th><th scope="col">Creada</th><th scope="col" class="opsAmount">');
    expect(html).toContain('<time dateTime="2026-10-12T17:04:05.123456+00:00">12 oct 2026, 10:04</time>');
    expect(html).toContain('<time dateTime="2026-10-05T06:30:00+00:00">4 oct 2026, 23:30</time>');
    expect(text(html)).not.toContain("Sin dato");
  });

  it("keeps the focus on the page: no remount of the guía search, a focusable summary and Cargar más", () => {
    const searched = render(state({ guide: row.public_id, rows: [row], nextCursor: "c1" }));
    expect(searched).toContain('<button type="button" class="btnLink">Quitar búsqueda</button>');
    expect(searched).toContain('<p class="opsSummaryNote" tabindex="-1">');
    expect(searched).toContain('aria-disabled="false">Cargar más</button>');
    const paging = render(state({ rows: [row], nextCursor: "c1", loadingMore: true }));
    expect(paging).toContain('aria-disabled="true">Cargando…</button>');
    expect(paging).not.toMatch(/<button[^>]*disabled=""[^>]*>Cargando…/);
  });

  it.each([
    [state({ loaded: false, loading: true, lastUpdated: null }), "Cargando órdenes…"],
    [state(), "Sin órdenes Tu organización todavía no tiene órdenes."],
    [state({ view: { status: "CLOSED", from: null, to: null } }), "Sin órdenes con estos filtros"],
    [state({ guide: row.public_id }), "No encontramos esa guía Revisa el número de guía."],
    [state({ rows: [row], error: "No fue posible actualizar la lista de órdenes." }), "No fue posible actualizar la lista de órdenes."],
    [state({ loaded: false, rangeError: orderListRangeTooLongMessage }), "Elige un periodo de 31 días o menos."],
  ])("shows its loading, empty, error and period states (%#)", (value, expected) => {
    expect(text(render(value))).toContain(expected);
  });

  it("offers the step-up for MFA and the gate for no session or no access, without any list", () => {
    const mfa = render(
      state({ rows: [], loaded: true, message: "Esta acción requiere verificar tu identidad (MFA).", stepUpHref: "/login?mfa=required" }),
    );
    expect(text(mfa)).toContain("Verificar identidad");
    expect(text(mfa)).not.toContain("Sin órdenes");
    for (const phase of ["no_session", "access_unavailable", "loading"] as const) {
      const html = render(state({ phase, rows: [row] }));
      expect(html).not.toContain("<table");
      expect(html).not.toContain("Buscar orden por guía");
    }
    expect(text(render(state({ phase: "access_unavailable" })))).toContain(
      "Tu rol en la organización activa no consulta órdenes.",
    );
    expect(text(render(state({ phase: "no_session" })))).toContain("Sin sesión");
  });

  it("opens the detail from a click on the row, not from its own controls", () => {
    const onOpen = vi.fn();
    const element = OrderListRow({ row, href: `/ops/orders/${orderId}`, onOpen });
    const click = (target: unknown) =>
      element.props.onClick({ target } as unknown as Parameters<typeof element.props.onClick>[0]);
    vi.stubGlobal("window", { getSelection: () => ({ toString: () => "" }) });
    vi.stubGlobal("Element", class {});
    try {
      const cell = Object.assign(new (globalThis as unknown as { Element: new () => object }).Element(), {
        closest: () => null,
      });
      click(cell);
      expect(onOpen).toHaveBeenCalledWith(`/ops/orders/${orderId}`);
      const link = Object.assign(new (globalThis as unknown as { Element: new () => object }).Element(), {
        closest: () => ({}),
      });
      click(link);
      expect(onOpen).toHaveBeenCalledOnce();
    } finally {
      vi.unstubAllGlobals();
    }
    expect(OrderListRow({ row, href: null, onOpen }).props.onClick).toBeUndefined();
  });
});
