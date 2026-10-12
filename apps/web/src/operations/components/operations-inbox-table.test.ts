import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import type { OperationsDashboardOrder } from "../contracts/operations-dashboard";
import { InboxRow, InboxTableHead } from "./operations-inbox-shell";

// The table header and row are pure presentation; the shell's stateful hook (session, API, realtime) is
// never called by them, so it is replaced and the test loads only what it renders.
vi.mock("../state/use-operations-inbox", () => ({
  useOperationsInbox: () => {
    throw new Error("The inbox state is not part of this test.");
  },
}));

const orderId = "0a1b2c3d-1111-4222-8333-944445555666";

function order(overrides: Partial<OperationsDashboardOrder> = {}): OperationsDashboardOrder {
  return {
    order_id: orderId,
    aggregate_version: 3,
    public_id: "ORD_0000000000000000000042",
    owner: { organization_id: "11111111-1111-4111-8111-111111111111", display_name: "Dueña" },
    operator: null,
    client: null,
    status: "DELIVERING",
    created_at: "2026-10-10T17:00:00Z",
    updated_at: "2026-10-10T18:00:00Z",
    service_type: "SAME_DAY",
    pickup_window: null,
    delivery_window: null,
    delivery_zone: { operating_zone_id: "5a2b3c4d-1111-4222-8333-944445555666", name: "Centro", zone_type: "CORE" },
    assignment: {
      assignment_id: "6b2b3c4d-1111-4222-8333-944445555666",
      assignment_type: "OWN",
      status: "ACTIVE",
      driver_id: "7c2b3c4d-1111-4222-8333-944445555666",
      driver_reference: "DRV-0a1b2c3d",
    },
    latest_driver_location: null,
    total: { currency: "MXN", amount_cents: 123_456 },
    cost_warning: null,
    unassigned_alert: false,
    ...overrides,
  };
}

/** The table as the inbox renders it: its header and one row. */
function table(row: OperationsDashboardOrder): string {
  return renderToStaticMarkup(
    createElement(
      "table",
      null,
      createElement(InboxTableHead),
      createElement(
        "tbody",
        null,
        createElement(InboxRow, {
          row,
          href: `/ops/orders/${row.order_id}?inbox=queue%3Den_route`,
          assigning: false,
          onOpen: () => undefined,
          onAssign: () => undefined,
        }),
      ),
    ),
  );
}

const text = (html: string) => html.replace(/<[^>]+>/g, "").trim();

/** Header labels, then the cells of the row (the guide's row header first), as plain text. */
function cells(html: string) {
  const head = html.slice(html.indexOf("<thead>"), html.indexOf("</thead>"));
  const body = html.slice(html.indexOf("<tbody>"), html.indexOf("</tbody>"));
  return {
    header: [...head.matchAll(/<th\b[^>]*>([\s\S]*?)<\/th>/g)].map((match) => text(match[1] ?? "")),
    row: [...body.matchAll(/<t[hd]\b[^>]*>([\s\S]*?)<\/t[hd]>/g)].map((match) => match[0]),
  };
}

describe("work inbox table (UI-PHASE3-INBOX-TOTAL-2026-10-10)", () => {
  it("lists the approved columns in order, Total labeled IVA incluido", () => {
    expect(cells(table(order())).header).toEqual([
      "Guía",
      "Estado",
      "Destino (zona)",
      "Ventana de entrega",
      "Repartidor",
      "Total (IVA incluido)",
      "Acciones",
    ]);
  });

  it("shows the order total from integer cents as MXN in the Total column", () => {
    const { row } = cells(table(order()));
    expect(row).toHaveLength(7);
    expect(text(row[0] ?? "")).toBe("ORD_0000000000000000000042");
    expect(text(row[2] ?? "")).toBe("Centro");
    expect(text(row[4] ?? "")).toBe("DRV-0a1b2c3d");
    expect(row[5]).toBe('<td class="opsInboxTotal"><span class="tabular">$1,234.56 MXN</span></td>');
  });

  it.each([
    [0, "$0.00 MXN"],
    [5, "$0.05 MXN"],
    [5_200, "$52.00 MXN"],
    [123_456_789_012, "$1,234,567,890.12 MXN"],
    [Number.MAX_SAFE_INTEGER, "$90,071,992,547,409.91 MXN"],
  ])("formats %i cents exactly as %s", (amountCents, expected) => {
    const { row } = cells(table(order({ total: { currency: "MXN", amount_cents: amountCents } })));
    expect(text(row[5] ?? "")).toBe(expected);
  });

  it("refuses to render a total that is not integer cents", () => {
    expect(() => table(order({ total: { currency: "MXN", amount_cents: 12.5 } }))).toThrow();
  });

  it("has no selection column unless the table offers one", () => {
    const html = table(order({ unassigned_alert: true, status: "READY_FOR_PICKUP", assignment: null }));
    expect(html).not.toContain('type="checkbox"');
    expect(text(html)).not.toContain("Seleccionar");
  });

  it("shows no internal identifier next to the total", () => {
    const assigned = text(table(order()));
    expect(assigned).not.toContain(orderId);
    expect(assigned).not.toContain("7c2b3c4d-1111-4222-8333-944445555666");
    expect(assigned).not.toContain("6b2b3c4d-1111-4222-8333-944445555666");
    const unassigned = table(order({ unassigned_alert: true, status: "READY_FOR_PICKUP", assignment: null }));
    expect(text(unassigned)).toContain("Asignar");
    expect(text(cells(unassigned).row[4] ?? "")).toBe("Sin repartidor");
    expect(text(cells(unassigned).row[5] ?? "")).toBe("$1,234.56 MXN");
  });
});

/** The table with the selection column of UI-PHASE3-INBOX-PRICE-REVIEW-BULK-2026-10-11. */
function selectableTable(
  rows: readonly { readonly row: OperationsDashboardOrder; readonly selected?: boolean; readonly disabled?: boolean }[],
  allState: "none" | "some" | "all" = "none",
): string {
  return renderToStaticMarkup(
    createElement(
      "table",
      null,
      createElement(InboxTableHead, { selection: { state: allState, disabled: false, onToggleAll: () => undefined } }),
      createElement(
        "tbody",
        null,
        ...rows.map(({ row, selected = false, disabled = false }) =>
          createElement(InboxRow, {
            key: row.order_id,
            row,
            href: `/ops/orders/${row.order_id}?inbox=queue%3Dunassigned`,
            assigning: false,
            selection: { selected, disabled, onToggle: () => undefined },
            onOpen: () => undefined,
            onAssign: () => undefined,
          }),
        ),
      ),
    ),
  );
}

describe("work inbox selection column (UI-PHASE3-INBOX-PRICE-REVIEW-BULK-2026-10-11)", () => {
  const assignable = order({
    order_id: "1a1b2c3d-1111-4222-8333-944445555666",
    public_id: "ORD_0000000000000000000043",
    unassigned_alert: true,
    status: "READY_FOR_PICKUP",
    assignment: null,
  });
  const enRoute = order();

  it("opens the header with Seleccionar todas before the approved columns", () => {
    const html = selectableTable([{ row: assignable }]);
    expect(cells(html).header).toEqual([
      "Seleccionar todas",
      "Guía",
      "Estado",
      "Destino (zona)",
      "Ventana de entrega",
      "Repartidor",
      "Total (IVA incluido)",
      "Acciones",
    ]);
    expect(selectableTable([{ row: assignable }], "all")).toMatch(/<th[^>]*opsInboxSelect[\s\S]*?checked=""/);
  });

  it("offers a checkbox only on rows the server flags as assignable", () => {
    const html = selectableTable([{ row: assignable, selected: true }, { row: enRoute }]);
    const body = html.slice(html.indexOf("<tbody>"));
    expect(body.match(/type="checkbox"/g)).toHaveLength(1);
    expect(text(body)).toContain("Seleccionar la orden ORD_0000000000000000000043");
    expect(text(body)).not.toContain("Seleccionar la orden ORD_0000000000000000000042");
    expect(body).toMatch(/type="checkbox"[^>]*checked=""/);
    // Every row keeps the cell, so the columns stay aligned.
    expect(cells(html).row).toHaveLength(16);
  });

  it("disables a checkbox past the batch limit or while a batch is open, and the single Asignar with it", () => {
    const html = selectableTable([{ row: assignable, disabled: true }]);
    expect(html).toMatch(/type="checkbox" disabled=""/);
    const frozen = renderToStaticMarkup(
      createElement(
        "table",
        null,
        createElement(
          "tbody",
          null,
          createElement(InboxRow, {
            row: assignable,
            href: "/ops/orders/x",
            assigning: false,
            assignDisabled: true,
            onOpen: () => undefined,
            onAssign: () => undefined,
          }),
        ),
      ),
    );
    expect(frozen).toMatch(/<button[^>]*disabled=""[^>]*>Asignar/);
  });
});
