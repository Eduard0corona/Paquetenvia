import { describe, expect, it } from "vitest";
import type { AssignableDriver, IneligibilityReason } from "./assignable-driver";
import {
  bulkAssignmentConfirmation,
  bulkCostInvalidMessage,
  bulkCostSumTooLargeMessage,
  bulkOrders,
  bulkRunSummary,
  bulkSelectionLimit,
  currentSelection,
  driverBlockText,
  intersectEligibility,
  isSelectableRow,
  parseBulkCosts,
  selectAllState,
  toggleSelectAll,
  toggleSelection,
  type BulkOrder,
} from "./bulk-assignment";
import type { OperationsDashboardOrder } from "./operations-dashboard";

let sequence = 0;
function row(assignable: boolean, overrides: Partial<OperationsDashboardOrder> = {}): OperationsDashboardOrder {
  sequence += 1;
  return {
    order_id: `00000000-0000-4000-8000-${String(sequence).padStart(12, "0")}`,
    aggregate_version: 1,
    public_id: `ORD_${String(sequence).padStart(22, "0")}`,
    owner: { organization_id: "11111111-1111-4111-8111-111111111111", display_name: "Dueña" },
    operator: null,
    client: null,
    status: assignable ? "READY_FOR_PICKUP" : "IN_TRANSIT",
    created_at: "2026-10-11T18:00:00Z",
    updated_at: "2026-10-11T18:00:00Z",
    service_type: "SAME_DAY",
    pickup_window: null,
    delivery_window: null,
    delivery_zone: { operating_zone_id: "5a2b3c4d-1111-4222-8333-944445555666", name: "Centro", zone_type: "CORE" },
    assignment: null,
    latest_driver_location: null,
    total: { currency: "MXN", amount_cents: 12_345 },
    cost_warning: null,
    unassigned_alert: assignable,
    ...overrides,
  };
}

function driver(
  id: number,
  reasons: readonly IneligibilityReason[] = [],
  active = 0,
  vehicle: AssignableDriver["vehicle_type"] = "MOTORCYCLE",
): AssignableDriver {
  const hex = id.toString(16).padStart(8, "0");
  return {
    driver_id: `${hex}-0000-4000-8000-000000000000`,
    driver_reference: `DRV-${hex}`,
    vehicle_type: vehicle,
    eligible: reasons.length === 0,
    ineligibility_reasons: reasons,
    active_assignment_count: active,
  };
}

function order(index: number): BulkOrder {
  return {
    orderId: `00000000-0000-4000-8000-${String(900 + index).padStart(12, "0")}`,
    publicId: `ORD_${String(900 + index).padStart(22, "0")}`,
    zoneName: index % 2 === 0 ? "Centro" : null,
    totalCents: 10_000 + index,
  };
}

describe("bulk selection (UI-PHASE3-INBOX-PRICE-REVIEW-BULK-2026-10-11)", () => {
  it("selects only the rows the server flags as assignable, in table order", () => {
    const rows = [row(true), row(false), row(true)];
    expect(rows.map(isSelectableRow)).toEqual([true, false, true]);
    let selected = toggleSelection([], rows, rows[2].order_id);
    selected = toggleSelection(selected, rows, rows[0].order_id);
    expect(selected).toEqual([rows[0].order_id, rows[2].order_id]);
    // A row that cannot be assigned never joins the selection.
    expect(toggleSelection(selected, rows, rows[1].order_id)).toEqual(selected);
    // Toggling again clears it.
    expect(toggleSelection(selected, rows, rows[0].order_id)).toEqual([rows[2].order_id]);
  });

  it("caps a batch at 20 orders", () => {
    expect(bulkSelectionLimit).toBe(20);
    const rows = Array.from({ length: 25 }, () => row(true));
    let selected: readonly string[] = [];
    for (const item of rows) selected = toggleSelection(selected, rows, item.order_id);
    expect(selected).toHaveLength(20);
    expect(selected).toEqual(rows.slice(0, 20).map((item) => item.order_id));
    // Over the limit a new row is refused, and freeing one place admits it.
    expect(toggleSelection(selected, rows, rows[24].order_id)).toEqual(selected);
    const freed = toggleSelection(selected, rows, rows[0].order_id);
    expect(toggleSelection(freed, rows, rows[24].order_id)).toContain(rows[24].order_id);
  });

  it("selects every loaded assignable row up to the limit with Seleccionar todas, then clears", () => {
    const few = [row(true), row(false), row(true)];
    expect(selectAllState([], few)).toBe("none");
    const all = toggleSelectAll([], few);
    expect(all).toEqual([few[0].order_id, few[2].order_id]);
    expect(selectAllState(all, few)).toBe("all");
    expect(selectAllState([few[2].order_id], few)).toBe("some");
    // From a partial selection it completes it; from a full one it clears.
    expect(toggleSelectAll([few[2].order_id], few)).toEqual(all);
    expect(toggleSelectAll(all, few)).toEqual([]);

    const many = Array.from({ length: 23 }, (_, index) => row(index !== 5));
    const capped = toggleSelectAll([many[22].order_id], many);
    expect(capped).toHaveLength(20);
    expect(capped).toContain(many[22].order_id);
    expect(capped).not.toContain(many[5].order_id);
    expect(selectAllState(capped, many)).toBe("all");
    expect(toggleSelectAll(capped, many)).toEqual([]);
  });

  it("keeps only the selected rows that are still listed as assignable", () => {
    const rows = [row(true), row(true)];
    const selected = toggleSelectAll([], rows);
    const refreshed = [rows[1], { ...rows[0], unassigned_alert: false }];
    expect(currentSelection(selected, refreshed)).toEqual([rows[1].order_id]);
    expect(currentSelection(["not-listed", rows[1].order_id], [rows[1]])).toEqual([rows[1].order_id]);
  });

  it("builds the batch in table order with only non-personal fields and integer cents", () => {
    const rows = [row(true), row(true, { delivery_zone: null, total: { currency: "MXN", amount_cents: 0 } }), row(true)];
    const batch = bulkOrders([rows[2].order_id, rows[1].order_id], rows);
    expect(batch).toEqual([
      { orderId: rows[1].order_id, publicId: rows[1].public_id, zoneName: null, totalCents: 0 },
      { orderId: rows[2].order_id, publicId: rows[2].public_id, zoneName: "Centro", totalCents: 12_345 },
    ]);
  });
});

describe("drivers eligible for every selected order", () => {
  const first = order(1);
  const second = order(2);
  const third = order(3);

  it("offers only drivers eligible for every order and names the orders that block the others", () => {
    const shared = driver(1, [], 2);
    const lighter = driver(2, [], 0, "CAR");
    const blockedOnce = driver(3);
    const blockedTwice = driver(4);
    const eligibility = intersectEligibility(
      [first, second, third],
      new Map([
        [first.orderId, [shared, lighter, blockedOnce, blockedTwice]],
        [second.orderId, [shared, lighter, { ...blockedOnce, eligible: false, ineligibility_reasons: ["DOCUMENT_EXPIRED"] }, { ...blockedTwice, eligible: false, ineligibility_reasons: ["SERVICE_AREA_NOT_ELIGIBLE", "SERVICE_AREA_REQUIRED"] }]],
        [third.orderId, [{ ...shared, active_assignment_count: 3 }, lighter, blockedOnce]],
      ]),
    );
    // Fewer deliveries in progress first; the highest count any list reported is shown.
    expect(eligibility.eligible.map((choice) => choice.driverReference)).toEqual(["DRV-00000002", "DRV-00000001"]);
    expect(eligibility.eligible[1].activeAssignmentCount).toBe(3);
    expect(eligibility.eligible.every((choice) => choice.blockedBy.length === 0)).toBe(true);

    const once = eligibility.blocked.find((choice) => choice.driverReference === "DRV-00000003");
    expect(once?.blockedBy.map((block) => block.order.publicId)).toEqual([second.publicId]);
    expect(driverBlockText(once!)).toBe(`No puede llevar ${second.publicId} (Tiene un documento vencido).`);

    // Not listed for an order at all also keeps the driver out; repeated reasons read once.
    const twice = eligibility.blocked.find((choice) => choice.driverReference === "DRV-00000004");
    expect(twice?.blockedBy.map((block) => block.order.publicId)).toEqual([second.publicId, third.publicId]);
    expect(driverBlockText(twice!)).toBe(
      `No puede llevar ${second.publicId} (No cubre la zona de la orden) y ${third.publicId} (no aparece en su lista).`,
    );
  });

  it("offers nobody when no driver is eligible for every order", () => {
    const eligibility = intersectEligibility(
      [first, second],
      new Map([
        [first.orderId, [driver(1), driver(2, ["DRIVER_UNAVAILABLE"])]],
        [second.orderId, [driver(1, ["TOTAL_WEIGHT_EXCEEDED"]), driver(2)]],
      ]),
    );
    expect(eligibility.eligible).toEqual([]);
    expect(eligibility.blocked).toHaveLength(2);
    expect(intersectEligibility([first], new Map([[first.orderId, []]]))).toEqual({ eligible: [], blocked: [] });
  });
});

describe("cost per order (Costo por orden)", () => {
  const orders = [order(1), order(2), order(3)];

  it("requires a cost for every order, typed in pesos and read as integer cents", () => {
    const check = parseBulkCosts(orders, {
      [orders[0].orderId]: "45",
      [orders[1].orderId]: "0.05",
      [orders[2].orderId]: " 1234.5 ",
    });
    expect(check.ok).toBe(true);
    if (!check.ok) return;
    expect([...check.costs.entries()]).toEqual([
      [orders[0].orderId, 4_500],
      [orders[1].orderId, 5],
      [orders[2].orderId, 123_450],
    ]);
    expect(check.totalCents).toBe(127_955);
    expect([...check.costs.values()].every(Number.isSafeInteger)).toBe(true);
  });

  it("allows zero as the single-order picker does", () => {
    const check = parseBulkCosts([orders[0]], { [orders[0].orderId]: "0" });
    expect(check).toEqual({ ok: true, costs: new Map([[orders[0].orderId, 0]]), totalCents: 0 });
  });

  it.each(["", "   ", "-1", "45.001", "45,00", "$45", "1e3", "abc", "12345678901234"])(
    "refuses %j and names the order",
    (text) => {
      const check = parseBulkCosts(orders, {
        [orders[0].orderId]: "10",
        [orders[1].orderId]: text,
        [orders[2].orderId]: "10",
      });
      expect(check.ok).toBe(false);
      if (check.ok) return;
      expect([...check.errors.entries()]).toEqual([[orders[1].orderId, bulkCostInvalidMessage]]);
      expect(check.message).toBe("Revisa el costo de 1 orden.");
    },
  );

  it("starts empty: nothing is prefilled and every order needs its cost", () => {
    const check = parseBulkCosts(orders, {});
    expect(check.ok).toBe(false);
    if (check.ok) return;
    expect(check.errors.size).toBe(3);
    expect(check.message).toBe("Revisa el costo de 3 órdenes.");
  });

  it("refuses a sum beyond safe integer cents instead of rounding it", () => {
    // Each cost is a safe integer (999,999,999,999,999 cents); nine still sum safely, ten do not.
    const ten = Array.from({ length: 10 }, (_, index) => order(10 + index));
    const typed = Object.fromEntries(ten.map((item) => [item.orderId, "9999999999999.99"]));
    expect(parseBulkCosts(ten, typed)).toMatchObject({ ok: false, message: bulkCostSumTooLargeMessage });
    expect(parseBulkCosts(ten.slice(0, 9), typed).ok).toBe(true);
  });
});

describe("confirmation and outcome texts", () => {
  const choice = {
    driverId: "00000001-0000-4000-8000-000000000000",
    driverReference: "DRV-00000001",
    vehicleType: "MOTORCYCLE" as const,
    activeAssignmentCount: 0,
    blockedBy: [],
  };

  it("names the driver reference, the number of orders and the sum of the costs", () => {
    expect(bulkAssignmentConfirmation(choice, 4, 18_000)).toEqual({
      title: "¿Asignar 4 órdenes?",
      description:
        "Se asignarán 4 órdenes al repartidor DRV-00000001 (motocicleta); los costos suman $180.00 MXN. " +
        "Se envían una por una y cada fila mostrará su resultado.",
      confirmLabel: "Asignar 4 órdenes",
    });
    expect(bulkAssignmentConfirmation(choice, 1, 0)).toMatchObject({
      title: "¿Asignar 1 orden?",
      confirmLabel: "Asignar 1 orden",
    });
    expect(bulkAssignmentConfirmation(choice, 1, 0).description).toContain("Se asignará 1 orden");
    expect(() => bulkAssignmentConfirmation(choice, 2, 12.5)).toThrow();
  });

  it("never hides a partial failure in the summary", () => {
    expect(bulkRunSummary(4, 4)).toBe("Se asignaron las 4 órdenes.");
    expect(bulkRunSummary(1, 1)).toBe("Se asignó la orden.");
    expect(bulkRunSummary(3, 4)).toBe("Se asignaron 3 de 4 órdenes. Revisa el motivo de las demás en su fila.");
    expect(bulkRunSummary(0, 2)).toBe("No se asignó ninguna orden. Revisa el motivo en cada fila.");
  });
});
