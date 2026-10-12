import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import type { OperationsDashboardOrder } from "../contracts/operations-dashboard";
import { OperationsFilters } from "./operations-filters";
import { OperationsOrderCard } from "./operations-order-card";

// OBS-001-OPERATED-OWNER-NAME-2026-10-12: the active organization ("Operación Norte") operates an order another
// organization owns, and owns one another organization operates. RLS hides both other organizations' rows, so the
// read model gives them an id and no name.
const activeId = "0d1b2c3d-1111-4222-8333-944445555666";
const hiddenOwnerId = "1e1b2c3d-1111-4222-8333-944445555666";
const hiddenOperatorId = "2f1b2c3d-1111-4222-8333-944445555666";

function order(overrides: Partial<OperationsDashboardOrder> = {}): OperationsDashboardOrder {
  return {
    order_id: "0a1b2c3d-1111-4222-8333-944445555666",
    aggregate_version: 3,
    public_id: "ORD_0000000000000000000042",
    owner: { organization_id: hiddenOwnerId, display_name: null },
    operator: { organization_id: activeId, display_name: "Operación Norte" },
    client: null,
    status: "READY_FOR_PICKUP",
    created_at: "2026-10-10T17:00:00Z",
    updated_at: "2026-10-10T18:00:00Z",
    service_type: "SAME_DAY",
    pickup_window: null,
    delivery_window: null,
    delivery_zone: null,
    assignment: null,
    latest_driver_location: null,
    total: { currency: "MXN", amount_cents: 9_000 },
    cost_warning: null,
    unassigned_alert: true,
    ...overrides,
  };
}

const delegated = order({
  order_id: "3a1b2c3d-1111-4222-8333-944445555666",
  owner: { organization_id: activeId, display_name: "Operación Norte" },
  operator: { organization_id: hiddenOperatorId, display_name: null },
});

function card(row: OperationsDashboardOrder): string {
  return renderToStaticMarkup(
    createElement(OperationsOrderCard, {
      order: row,
      confirm: () => undefined,
      onPublishExternalOffer: () => Promise.resolve(),
    }),
  );
}

/** The plain-text value the card shows for one label. */
function field(html: string, label: string): string | undefined {
  return new RegExp(`<dt>${label}</dt><dd>([\\s\\S]*?)</dd>`).exec(html)?.[1]?.replace(/<[^>]+>/g, "");
}

/** The [value, text] options of one labeled filter. */
function options(html: string, label: string): string[][] {
  const start = html.indexOf(`<label>${label}<select`);
  const select = html.slice(start, html.indexOf("</select>", start));
  return [...select.matchAll(/<option value="([^"]*)"[^>]*>([^<]*)<\/option>/g)].map((match) => [
    match[1] ?? "",
    match[2] ?? "",
  ]);
}

describe("organizations RLS hides (OBS-001-OPERATED-OWNER-NAME-2026-10-12)", () => {
  it("shows the owner of an order it only operates as another organization, without its id", () => {
    const html = card(order());
    expect(field(html, "Dueño")).toBe("Otra organización");
    expect(field(html, "Opera")).toBe("Operación Norte");
    expect(html).not.toContain(hiddenOwnerId);
  });

  it("shows the operator of an order it owns as another organization, without its id", () => {
    const html = card(delegated);
    expect(field(html, "Dueño")).toBe("Operación Norte");
    expect(field(html, "Opera")).toBe("Otra organización");
    expect(html).not.toContain(hiddenOperatorId);
  });

  it("keeps Sin operador for an order nobody else operates", () => {
    expect(field(card(order({ owner: delegated.owner, operator: null })), "Opera")).toBe("Sin operador");
  });

  it("offers as filters only the organizations it can name", () => {
    const html = renderToStaticMarkup(
      createElement(OperationsFilters, {
        filters: {},
        items: [order(), delegated],
        disabled: false,
        onChange: () => undefined,
      }),
    );
    expect(options(html, "Dueño")).toEqual([["", "Todos"], [activeId, "Operación Norte"]]);
    expect(options(html, "Opera")).toEqual([["", "Todos"], [activeId, "Operación Norte"]]);
    expect(html).not.toContain(hiddenOwnerId);
    expect(html).not.toContain(hiddenOperatorId);
    expect(html).not.toContain("Otra organización");
  });
});
