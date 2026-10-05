import { describe, expect, it } from "vitest";
import { orderStatuses } from "./operations-dashboard";
import { parseOperationsQueueCounts, statusGroupTotals } from "./queue-counts";
import { ContractViolationError } from "./strict-json";

function zeros(): Record<string, number> {
  return Object.fromEntries(orderStatuses.map((status) => [status, 0]));
}

function valid() {
  const byStatus = {
    ...zeros(),
    READY_FOR_PICKUP: 3,
    RESCHEDULED: 1,
    FAILED_ATTEMPT: 2,
    RETURNING: 1,
    IN_TRANSIT: 4,
    DELIVERING: 1,
    DELIVERED: 2,
    CLOSED: 5,
  };
  return {
    generated_at: "2026-10-05T18:00:00+00:00",
    total: 19,
    by_status: byStatus,
    queues: {
      unassigned: 2,
      needs_attention: 4,
      price_review: 1,
      delivered_not_closed: 2,
      en_route: 5,
    },
  };
}

describe("operations queue counts contract (UI-PHASE2-QUEUE-COUNTS-2026-10-05)", () => {
  it("reads the published shape with every status, zeros included", () => {
    const counts = parseOperationsQueueCounts(valid());
    expect(Object.keys(counts.by_status)).toEqual([...orderStatuses]);
    expect(counts.by_status.CANCELLED).toBe(0);
    expect(counts.total).toBe(19);
    expect(counts.queues).toEqual({
      unassigned: 2,
      needs_attention: 4,
      price_review: 1,
      delivered_not_closed: 2,
      en_route: 5,
    });
    expect(parseOperationsQueueCounts({ ...valid(), generated_at: "2026-10-05T18:00:00Z" }).total).toBe(19);
  });

  it("sums the server counts into the five status groups", () => {
    expect(statusGroupTotals(parseOperationsQueueCounts(valid()))).toEqual({
      TO_PREPARE: 3,
      PICKUP: 0,
      EN_ROUTE: 5,
      NEEDS_ATTENTION: 4,
      FINISHED: 7,
    });
  });

  const broken: [string, (value: ReturnType<typeof valid>) => unknown][] = [
    ["not an object", () => []],
    ["an extra top-level key", (value) => ({ ...value, items: [] })],
    ["a missing queue", (value) => ({ ...value, queues: { ...value.queues, en_route: undefined } })],
    ["an extra queue", (value) => ({ ...value, queues: { ...value.queues, overdue: 0 } })],
    ["a missing status", (value) => {
      const byStatus: Record<string, number> = { ...value.by_status };
      delete byStatus.CANCELLED;
      return { ...value, by_status: byStatus };
    }],
    ["an unknown status", (value) => ({ ...value, by_status: { ...value.by_status, LOST: 0 } })],
    ["a negative count", (value) => ({ ...value, by_status: { ...value.by_status, CLOSED: -1 }, total: 13 })],
    ["a fractional count", (value) => ({ ...value, by_status: { ...value.by_status, CLOSED: 5.5 }, total: 19.5 })],
    ["a string count", (value) => ({ ...value, total: "19" })],
    ["an unsafe integer", (value) => ({ ...value, total: Number.MAX_SAFE_INTEGER + 1 })],
    ["a total that is not the sum", (value) => ({ ...value, total: 20 })],
    ["needs_attention off its statuses", (value) => ({ ...value, queues: { ...value.queues, needs_attention: 3 } })],
    ["en_route off its statuses", (value) => ({ ...value, queues: { ...value.queues, en_route: 4 } })],
    ["delivered_not_closed off DELIVERED", (value) => ({ ...value, queues: { ...value.queues, delivered_not_closed: 1 } })],
    ["more unassigned than ready or rescheduled", (value) => ({ ...value, queues: { ...value.queues, unassigned: 5 } })],
    ["more price reviews than orders", (value) => ({ ...value, queues: { ...value.queues, price_review: 20 } })],
    ["a non-UTC timestamp", (value) => ({ ...value, generated_at: "2026-10-05T12:00:00-06:00" })],
    ["a timestamp without offset", (value) => ({ ...value, generated_at: "2026-10-05T18:00:00" })],
  ];

  it.each(broken)("fails closed on %s", (_name, mutate) => {
    expect(() => parseOperationsQueueCounts(mutate(valid()))).toThrow(ContractViolationError);
  });
});
