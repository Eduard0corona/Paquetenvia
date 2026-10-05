import { orderStatuses, type OrderStatus } from "./operations-dashboard";

/**
 * UI-001 phase 2A: the five user-facing groups of the 17 AI-04 order statuses
 * (owner decision 2026-10-05, "Sí a los 5 grupos de estado"). A group is only a
 * presentation aid: filters, detail views and history keep the exact status.
 */
export const orderStatusGroupIds = [
  "TO_PREPARE",
  "PICKUP",
  "EN_ROUTE",
  "NEEDS_ATTENTION",
  "FINISHED",
] as const;

export type OrderStatusGroupId = (typeof orderStatusGroupIds)[number];

/** Design-token family (src/app/globals.css) that colors a group; never the only signal. */
export type OrderStatusGroupTone = "muted" | "info" | "accent" | "warn" | "ok";

export interface OrderStatusGroup {
  readonly id: OrderStatusGroupId;
  readonly label: string;
  readonly tone: OrderStatusGroupTone;
}

export const orderStatusGroups: Readonly<Record<OrderStatusGroupId, OrderStatusGroup>> = {
  TO_PREPARE: { id: "TO_PREPARE", label: "Por preparar", tone: "muted" },
  PICKUP: { id: "PICKUP", label: "En recolección", tone: "info" },
  EN_ROUTE: { id: "EN_ROUTE", label: "En ruta", tone: "accent" },
  NEEDS_ATTENTION: { id: "NEEDS_ATTENTION", label: "Requiere atención", tone: "warn" },
  FINISHED: { id: "FINISHED", label: "Terminadas", tone: "ok" },
};

/** Total over OrderStatus: a status added to the enum fails to compile until it is grouped. */
export function statusGroup(status: OrderStatus): OrderStatusGroupId {
  switch (status) {
    case "DRAFT":
    case "CONFIRMED":
    case "READY_FOR_PICKUP":
      return "TO_PREPARE";
    case "ASSIGNED":
    case "AT_PICKUP":
    case "PICKED_UP":
      return "PICKUP";
    case "IN_TRANSIT":
    case "DELIVERING":
      return "EN_ROUTE";
    case "FAILED_ATTEMPT":
    case "RESCHEDULED":
    case "RETURNING":
    case "CLAIM_OPEN":
      return "NEEDS_ATTENTION";
    case "DELIVERED":
    case "RETURNED":
    case "CLAIM_RESOLVED":
    case "CLOSED":
    case "CANCELLED":
      return "FINISHED";
    default: {
      const unmapped: never = status;
      throw new Error(`Unmapped order status: ${String(unmapped)}`);
    }
  }
}

/** The statuses of a group in the AI-04 enum order. */
export function statusesInGroup(group: OrderStatusGroupId): readonly OrderStatus[] {
  return orderStatuses.filter((status) => statusGroup(status) === group);
}

export function isOrderStatus(value: string): value is OrderStatus {
  return (orderStatuses as readonly string[]).includes(value);
}
