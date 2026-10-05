import { orderStatuses, type OrderStatus } from "./operations-dashboard";
import { orderStatusGroupIds, statusesInGroup, type OrderStatusGroupId } from "./status-groups";
import { exactObject, fail, integer, timestamp } from "./strict-json";

/**
 * UI-PHASE2-QUEUE-COUNTS-2026-10-05: AI-05 getOperationsQueueCounts
 * (GET /operations/queue-counts). Real server counts over every order the
 * active organization reads as owner or operator; integer counts only.
 */
export const operationsQueueIds = [
  "unassigned",
  "needs_attention",
  "price_review",
  "delivered_not_closed",
  "en_route",
] as const;

export type OperationsQueueId = (typeof operationsQueueIds)[number];

export interface OperationsQueueCounts {
  readonly generated_at: string;
  readonly total: number;
  readonly by_status: Readonly<Record<OrderStatus, number>>;
  readonly queues: Readonly<Record<OperationsQueueId, number>>;
}

/** The statuses each status-derived queue sums (AI-05 OperationsQueues). */
const queueStatuses: Readonly<Partial<Record<OperationsQueueId, readonly OrderStatus[]>>> = {
  needs_attention: ["FAILED_ATTEMPT", "RESCHEDULED", "RETURNING", "CLAIM_OPEN"],
  delivered_not_closed: ["DELIVERED"],
  en_route: ["IN_TRANSIT", "DELIVERING"],
};

/**
 * Fail-closed reader: exactly the published keys, every count a non-negative
 * safe integer, a UTC timestamp, and counts that agree with each other
 * (total is the sum of by_status, the status queues are their statuses, and
 * unassigned and price_review never exceed what they are drawn from).
 */
export function parseOperationsQueueCounts(value: unknown): OperationsQueueCounts {
  const object = exactObject(value, ["generated_at", "total", "by_status", "queues"]);
  const generatedAt = timestamp(object.generated_at);
  if (!generatedAt.endsWith("Z") && !generatedAt.endsWith("+00:00")) fail();
  const total = integer(object.total, 0);

  const statusObject = exactObject(object.by_status, orderStatuses);
  const byStatus = Object.fromEntries(
    orderStatuses.map((status) => [status, integer(statusObject[status], 0)]),
  ) as Record<OrderStatus, number>;

  const queueObject = exactObject(object.queues, operationsQueueIds);
  const queues = Object.fromEntries(
    operationsQueueIds.map((queue) => [queue, integer(queueObject[queue], 0)]),
  ) as Record<OperationsQueueId, number>;

  if (sum(orderStatuses, byStatus) !== total) fail();
  for (const [queue, statuses] of Object.entries(queueStatuses) as [OperationsQueueId, readonly OrderStatus[]][]) {
    if (queues[queue] !== sum(statuses, byStatus)) fail();
  }
  if (queues.unassigned > byStatus.READY_FOR_PICKUP + byStatus.RESCHEDULED) fail();
  if (queues.price_review > total) fail();

  return { generated_at: generatedAt, total, by_status: byStatus, queues };
}

/** The server total of each of the five status groups (UI-STATUS-GROUPS-2026-10-05). */
export function statusGroupTotals(
  counts: OperationsQueueCounts,
): Readonly<Record<OrderStatusGroupId, number>> {
  return Object.fromEntries(
    orderStatusGroupIds.map((group) => [group, sum(statusesInGroup(group), counts.by_status)]),
  ) as Record<OrderStatusGroupId, number>;
}

function sum(statuses: readonly OrderStatus[], byStatus: Readonly<Record<OrderStatus, number>>): number {
  return statuses.reduce((accumulator, status) => accumulator + byStatus[status], 0);
}
