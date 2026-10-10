import { describe, expect, it } from "vitest";
import type { OperationsDashboardOrder, OrderStatus } from "../contracts/operations-dashboard";
import { compareInboxRows, inboxAggregateVersions, mergeInboxSources } from "./inbox-merge";

let sequence = 0;

/** A dashboard row whose updated_at is `minute` minutes after 18:00 UTC. */
function row(minute: number, status: OrderStatus, overrides: Partial<OperationsDashboardOrder> = {}): OperationsDashboardOrder {
  sequence += 1;
  const id = `00000000-0000-4000-8000-${String(sequence).padStart(12, "0")}`;
  const instant = new Date(Date.UTC(2026, 9, 10, 18, minute)).toISOString();
  return {
    order_id: id,
    aggregate_version: 1,
    public_id: `ORD_${String(sequence).padStart(22, "0")}`,
    owner: { organization_id: "11111111-1111-4111-8111-111111111111", display_name: "Dueña" },
    operator: null,
    client: null,
    status,
    created_at: instant,
    updated_at: instant,
    service_type: "SAME_DAY",
    pickup_window: null,
    delivery_window: null,
    delivery_zone: null,
    assignment: null,
    latest_driver_location: null,
    cost_warning: null,
    unassigned_alert: false,
    ...overrides,
  };
}

const minutes = (rows: readonly OperationsDashboardOrder[]) =>
  rows.map((item) => new Date(item.updated_at).getUTCMinutes());

describe("work inbox merge (UI-PHASE3-INBOX-2026-10-10)", () => {
  it("keeps the server order of a single query and reports its next page", () => {
    const items = [row(50, "DELIVERED"), row(40, "DELIVERED"), row(30, "DELIVERED")];
    expect(mergeInboxSources([{ items, nextCursor: "c1" }])).toEqual({ rows: items, hasMore: true });
    expect(mergeInboxSources([{ items, nextCursor: null }])).toEqual({ rows: items, hasMore: false });
  });

  it("merges several status queries newest first and hides what an unfinished query could still precede", () => {
    const failed = [row(55, "FAILED_ATTEMPT"), row(45, "FAILED_ATTEMPT"), row(35, "FAILED_ATTEMPT")];
    const returning = [row(50, "RETURNING"), row(20, "RETURNING")];
    const claims = [row(40, "CLAIM_OPEN"), row(10, "CLAIM_OPEN")];
    // FAILED_ATTEMPT has more pages after 18:35: nothing older may be shown yet, because its
    // next page could hold an order updated between 18:35 and 18:20.
    const first = mergeInboxSources([
      { items: failed, nextCursor: "f1" },
      { items: returning, nextCursor: null },
      { items: claims, nextCursor: null },
    ]);
    expect(minutes(first.rows)).toEqual([55, 50, 45, 40, 35]);
    expect(first.hasMore).toBe(true);

    // After its last page arrives, the held rows appear in order.
    const second = mergeInboxSources([
      { items: [...failed, row(30, "FAILED_ATTEMPT")], nextCursor: null },
      { items: returning, nextCursor: null },
      { items: claims, nextCursor: null },
    ]);
    expect(minutes(second.rows)).toEqual([55, 50, 45, 40, 35, 30, 20, 10]);
    expect(second.hasMore).toBe(false);
  });

  it("uses the newest last row among the unfinished queries as the boundary", () => {
    const transit = [row(59, "IN_TRANSIT"), row(30, "IN_TRANSIT")];
    const delivering = [row(45, "DELIVERING"), row(40, "DELIVERING")];
    const merged = mergeInboxSources([
      { items: transit, nextCursor: "t1" },
      { items: delivering, nextCursor: "d1" },
    ]);
    // DELIVERING may still hold rows between 18:40 and 18:30.
    expect(minutes(merged.rows)).toEqual([59, 45, 40]);
  });

  it("does not let a query that returned no rows hide the others", () => {
    const merged = mergeInboxSources([
      { items: [], nextCursor: "empty" },
      { items: [row(12, "RETURNING")], nextCursor: null },
    ]);
    expect(minutes(merged.rows)).toEqual([12]);
    expect(merged.hasMore).toBe(true);
  });

  it("shows an order once, with its newest snapshot, when two queries returned it", () => {
    const older = row(20, "FAILED_ATTEMPT", { aggregate_version: 3 });
    const newer = { ...older, status: "RESCHEDULED" as const, aggregate_version: 4, updated_at: "2026-10-10T18:25:00Z" };
    const merged = mergeInboxSources([
      { items: [older], nextCursor: null },
      { items: [newer], nextCursor: null },
    ]);
    expect(merged.rows).toEqual([newer]);
    expect(inboxAggregateVersions([{ items: [older], nextCursor: null }, { items: [newer], nextCursor: null }])).toEqual({
      [older.order_id]: 4,
    });
  });

  it("orders like the server: updated_at beyond milliseconds, then the greater order id", () => {
    const base = row(0, "DELIVERED");
    const microLater = { ...base, order_id: "00000000-0000-4000-8000-0000000000a1", updated_at: "2026-10-10T18:00:00.0001234+00:00" };
    const microEarlier = { ...base, order_id: "00000000-0000-4000-8000-0000000000a2", updated_at: "2026-10-10T18:00:00.0000005+00:00" };
    expect(compareInboxRows(microLater, microEarlier)).toBeLessThan(0);
    const sameInstantHigh = { ...base, order_id: "f0000000-0000-4000-8000-000000000000" };
    const sameInstantLow = { ...base, order_id: "0a000000-0000-4000-8000-000000000000" };
    expect(compareInboxRows(sameInstantHigh, sameInstantLow)).toBeLessThan(0);
    expect(compareInboxRows(sameInstantLow, sameInstantLow)).toBe(0);
    expect(
      mergeInboxSources([{ items: [sameInstantLow, sameInstantHigh, microEarlier, microLater], nextCursor: null }]).rows,
    ).toEqual([microLater, microEarlier, sameInstantHigh, sameInstantLow]);
  });
});
