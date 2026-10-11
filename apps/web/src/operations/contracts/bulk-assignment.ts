import {
  ineligibilityText,
  vehicleLabel,
  type AssignableDriver,
  type IneligibilityReason,
  type VehicleType,
} from "./assignable-driver";
import { formatMxnCentsWithCurrency, parseMxnToCents, sumCents } from "./money";
import type { OperationsDashboardOrder } from "./operations-dashboard";

/**
 * UI-PHASE3-INBOX-PRICE-REVIEW-BULK-2026-10-11 ("Costo por orden"): "Asignar seleccionadas…" in
 * the work inbox. A bulk assignment is a sequence of the existing single-order operations
 * (listAssignableDrivers per order, then assignDriver per order), never a new atomic flow; the
 * backend and RLS decide every assignment again. Everything here is pure: selection, the drivers
 * eligible for every selected order, the per-order costs in integer cents and the texts.
 */

/** At most this many orders per batch: each one is sent separately and the preview lists them all. */
export const bulkSelectionLimit = 20;

/** Only rows the server flags as assignable (`unassigned_alert`) can be selected. */
export function isSelectableRow(row: OperationsDashboardOrder): boolean {
  return row.unassigned_alert;
}

function selectableIds(rows: readonly OperationsDashboardOrder[]): string[] {
  return rows.filter(isSelectableRow).map((row) => row.order_id);
}

/** The selected ids that are still listed as selectable, in table order. */
export function currentSelection(
  selected: readonly string[],
  rows: readonly OperationsDashboardOrder[],
): readonly string[] {
  const chosen = new Set(selected);
  return selectableIds(rows).filter((id) => chosen.has(id));
}

/** Selects or clears one row; a new selection beyond the limit is refused (returned unchanged). */
export function toggleSelection(
  selected: readonly string[],
  rows: readonly OperationsDashboardOrder[],
  orderId: string,
  limit = bulkSelectionLimit,
): readonly string[] {
  const current = currentSelection(selected, rows);
  if (current.includes(orderId)) return current.filter((id) => id !== orderId);
  if (current.length >= limit || !selectableIds(rows).includes(orderId)) return current;
  return currentSelection([...current, orderId], rows);
}

export type SelectAllState = "none" | "some" | "all";

/** "all" once every selectable row that fits in one batch is selected. */
export function selectAllState(
  selected: readonly string[],
  rows: readonly OperationsDashboardOrder[],
  limit = bulkSelectionLimit,
): SelectAllState {
  const current = currentSelection(selected, rows).length;
  const fits = Math.min(selectableIds(rows).length, limit);
  if (current === 0) return "none";
  return current >= fits ? "all" : "some";
}

/**
 * "Seleccionar todas": selects the loaded selectable rows in table order up to the limit, keeping
 * what was already selected; when they are all selected already, clears the selection.
 */
export function toggleSelectAll(
  selected: readonly string[],
  rows: readonly OperationsDashboardOrder[],
  limit = bulkSelectionLimit,
): readonly string[] {
  if (selectAllState(selected, rows, limit) === "all") return [];
  const next = [...currentSelection(selected, rows)];
  for (const id of selectableIds(rows)) {
    if (next.length >= limit) break;
    if (!next.includes(id)) next.push(id);
  }
  return currentSelection(next, rows);
}

/** One order of a batch as the preview shows it; no personal data, the total in integer cents. */
export interface BulkOrder {
  readonly orderId: string;
  readonly publicId: string;
  /** Delivery zone name, or null when the order has none. */
  readonly zoneName: string | null;
  /** The order total, integer MXN cents with IVA included (UI-PHASE3-INBOX-TOTAL-2026-10-10). */
  readonly totalCents: number;
}

/** The batch the selection opens, in table order. */
export function bulkOrders(
  selected: readonly string[],
  rows: readonly OperationsDashboardOrder[],
): readonly BulkOrder[] {
  const chosen = new Set(currentSelection(selected, rows));
  return rows
    .filter((row) => chosen.has(row.order_id))
    .map((row) => ({
      orderId: row.order_id,
      publicId: row.public_id,
      zoneName: row.delivery_zone?.name ?? null,
      totalCents: row.total.amount_cents,
    }));
}

/** Why one order keeps a driver out of the batch. */
export interface DriverBlock {
  readonly order: BulkOrder;
  /** The policy reasons the order's list gave; empty when the driver is not in that list at all. */
  readonly reasons: readonly IneligibilityReason[];
}

export interface BulkDriverChoice {
  readonly driverId: string;
  readonly driverReference: string;
  readonly vehicleType: VehicleType;
  /** The highest count of deliveries in progress any of the lists reported. */
  readonly activeAssignmentCount: number;
  /** The orders that keep this driver out; empty when the driver is eligible for every order. */
  readonly blockedBy: readonly DriverBlock[];
}

export interface BulkEligibility {
  /** Drivers eligible for every order of the batch: the only ones that can be picked. */
  readonly eligible: readonly BulkDriverChoice[];
  /** Every other driver, with the orders that keep it out and why. */
  readonly blocked: readonly BulkDriverChoice[];
}

/**
 * Intersects the listAssignableDrivers answer of each order: a driver can be picked only when it
 * is listed and eligible for every order. Display order follows the single-order picker.
 */
export function intersectEligibility(
  orders: readonly BulkOrder[],
  driversByOrder: ReadonlyMap<string, readonly AssignableDriver[]>,
): BulkEligibility {
  const seen = new Map<string, AssignableDriver>();
  for (const order of orders) {
    for (const driver of driversByOrder.get(order.orderId) ?? []) {
      const prior = seen.get(driver.driver_id);
      if (prior === undefined || driver.active_assignment_count > prior.active_assignment_count)
        seen.set(driver.driver_id, driver);
    }
  }
  const choices = [...seen.values()].map((driver): BulkDriverChoice => {
    const blockedBy: DriverBlock[] = [];
    for (const order of orders) {
      const listed = (driversByOrder.get(order.orderId) ?? []).find((item) => item.driver_id === driver.driver_id);
      if (listed === undefined) blockedBy.push({ order, reasons: [] });
      else if (!listed.eligible) blockedBy.push({ order, reasons: listed.ineligibility_reasons });
    }
    return {
      driverId: driver.driver_id,
      driverReference: driver.driver_reference,
      vehicleType: driver.vehicle_type,
      activeAssignmentCount: driver.active_assignment_count,
      blockedBy,
    };
  });
  // Fewer deliveries in progress first, then the reference: the single-order picker's display order.
  const byLoad = (left: BulkDriverChoice, right: BulkDriverChoice) =>
    left.activeAssignmentCount - right.activeAssignmentCount ||
    (left.driverReference < right.driverReference ? -1 : left.driverReference > right.driverReference ? 1 : 0);
  return {
    eligible: choices.filter((choice) => choice.blockedBy.length === 0).sort(byLoad),
    blocked: choices.filter((choice) => choice.blockedBy.length > 0).sort(byLoad),
  };
}

/** Which orders keep a driver out and why, in plain es-MX. */
export function driverBlockText(choice: BulkDriverChoice): string {
  const parts = choice.blockedBy.map(
    (block) =>
      `${block.order.publicId} (${block.reasons.length === 0 ? "no aparece en su lista" : ineligibilityText(block.reasons)})`,
  );
  return `No puede llevar ${joinSpanish(parts)}.`;
}

function joinSpanish(parts: readonly string[]): string {
  if (parts.length <= 1) return parts.join("");
  return `${parts.slice(0, -1).join(", ")} y ${parts[parts.length - 1]}`;
}

export const bulkCostInvalidMessage = "Escribe un costo válido en pesos, por ejemplo 45.00.";
export const bulkCostSumTooLargeMessage = "La suma de los costos es demasiado grande; revisa los montos.";

export type BulkCostCheck =
  | {
      readonly ok: true;
      /** Integer cents per order id (zero allowed, as in the single-order picker). */
      readonly costs: ReadonlyMap<string, number>;
      readonly totalCents: number;
    }
  | {
      readonly ok: false;
      /** The message of each order whose cost is missing or not valid. */
      readonly errors: ReadonlyMap<string, string>;
      readonly message: string;
    };

/**
 * Reads the cost typed for each order (MXN) into integer cents with parseMxnToCents. Every cost
 * is required: nothing is prefilled until the tariff table exists. The sum must stay a safe integer.
 */
export function parseBulkCosts(
  orders: readonly BulkOrder[],
  typed: Readonly<Record<string, string>>,
): BulkCostCheck {
  const costs = new Map<string, number>();
  const errors = new Map<string, string>();
  for (const order of orders) {
    const cents = parseMxnToCents(typed[order.orderId] ?? "");
    if (cents === null) errors.set(order.orderId, bulkCostInvalidMessage);
    else costs.set(order.orderId, cents);
  }
  if (errors.size > 0)
    return {
      ok: false,
      errors,
      message: errors.size === 1 ? "Revisa el costo de 1 orden." : `Revisa el costo de ${errors.size} órdenes.`,
    };
  const totalCents = sumCents([...costs.values()]);
  if (totalCents === null) return { ok: false, errors, message: bulkCostSumTooLargeMessage };
  return { ok: true, costs, totalCents };
}

function orderCountText(count: number): string {
  return count === 1 ? "1 orden" : `${count} órdenes`;
}

/** The ConfirmDialog of a batch: names the driver, the number of orders and the sum of the costs. */
export function bulkAssignmentConfirmation(
  driver: BulkDriverChoice,
  orderCount: number,
  totalCents: number,
): { readonly title: string; readonly description: string; readonly confirmLabel: string } {
  const orders = orderCountText(orderCount);
  return {
    title: `¿Asignar ${orders}?`,
    description:
      `Se asignará${orderCount === 1 ? "" : "n"} ${orders} al repartidor ${driver.driverReference} ` +
      `(${vehicleLabel(driver.vehicleType).toLowerCase()}); los costos suman ${formatMxnCentsWithCurrency(totalCents)}. ` +
      "Se envían una por una y cada fila mostrará su resultado.",
    confirmLabel: `Asignar ${orders}`,
  };
}

/** What a finished batch says at the top of its results. */
export function bulkRunSummary(assigned: number, total: number): string {
  if (assigned === total) return total === 1 ? "Se asignó la orden." : `Se asignaron las ${total} órdenes.`;
  if (assigned === 0) return "No se asignó ninguna orden. Revisa el motivo en cada fila.";
  return `Se asignaron ${assigned} de ${total} órdenes. Revisa el motivo de las demás en su fila.`;
}
