import { describe, expect, it } from "vitest";
import { orderStatuses as financeOrderStatuses } from "../../finance/contracts/cod";
import { orderStatuses } from "./operations-dashboard";
import {
  isOrderStatus,
  orderStatusGroupIds,
  orderStatusGroups,
  statusGroup,
  statusesInGroup,
} from "./status-groups";

describe("order status groups", () => {
  it("keeps the five groups in the approved fixed order with their es-MX labels and tones", () => {
    expect(orderStatusGroupIds.map((id) => [orderStatusGroups[id].label, orderStatusGroups[id].tone])).toEqual([
      ["Por preparar", "muted"],
      ["En recolección", "info"],
      ["En ruta", "accent"],
      ["Requiere atención", "warn"],
      ["Terminadas", "ok"],
    ]);
    for (const id of orderStatusGroupIds) expect(orderStatusGroups[id].id).toBe(id);
  });

  it("maps every one of the 17 AI-04 statuses to exactly one group", () => {
    expect(orderStatuses).toHaveLength(17);
    expect(financeOrderStatuses).toEqual(orderStatuses);
    const grouped = orderStatusGroupIds.flatMap((id) => statusesInGroup(id));
    expect([...grouped].sort()).toEqual([...orderStatuses].sort());
    expect(new Set(grouped).size).toBe(17);
    for (const status of orderStatuses) {
      expect(orderStatusGroupIds.filter((id) => statusesInGroup(id).includes(status))).toEqual([statusGroup(status)]);
    }
  });

  it("follows the approved grouping", () => {
    expect(Object.fromEntries(orderStatusGroupIds.map((id) => [id, statusesInGroup(id)]))).toEqual({
      TO_PREPARE: ["DRAFT", "CONFIRMED", "READY_FOR_PICKUP"],
      PICKUP: ["ASSIGNED", "AT_PICKUP", "PICKED_UP"],
      EN_ROUTE: ["IN_TRANSIT", "DELIVERING"],
      NEEDS_ATTENTION: ["FAILED_ATTEMPT", "RESCHEDULED", "RETURNING", "CLAIM_OPEN"],
      FINISHED: ["RETURNED", "DELIVERED", "CLOSED", "CLAIM_RESOLVED", "CANCELLED"],
    });
  });

  it("fails loudly on a status outside the enum", () => {
    expect(() => statusGroup("LOST" as never)).toThrow("Unmapped order status: LOST");
    expect(isOrderStatus("DELIVERING")).toBe(true);
    expect(isOrderStatus("LOST")).toBe(false);
  });
});
