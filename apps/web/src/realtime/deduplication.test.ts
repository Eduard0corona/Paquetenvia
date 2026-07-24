import { describe, expect, it, vi } from "vitest";
import { resynchronizeAfterReconnect } from "./base-connection";
import { buildPrivateHubUrl } from "./base-connection";
import { RealtimeEventGuard } from "./deduplication";
import { asUuid, type RealtimeEnvelope, type Uuid } from "./envelope";
import { realtimeEventTypes } from "./event-types";

interface TestPayload {
  readonly value: string;
}

const aggregateId = asUuid("11111111-1111-1111-1111-111111111111");

function event(
  eventId: string,
  aggregateVersion: number,
): RealtimeEnvelope<Uuid, TestPayload> {
  return {
    event_id: asUuid(eventId),
    event_type: realtimeEventTypes.orderStatusChanged,
    occurred_at: "2026-07-24T00:00:00.000Z",
    aggregate_id: aggregateId,
    aggregate_version: aggregateVersion,
    payload: { value: "synthetic" },
  };
}

describe("RealtimeEventGuard", () => {
  it("deduplicates event ids and rejects older aggregate versions", () => {
    const guard = new RealtimeEventGuard();
    const versionTwo = event("22222222-2222-2222-2222-222222222222", 2);
    expect(guard.shouldApply(versionTwo)).toBe(true);
    expect(guard.shouldApply(versionTwo)).toBe(false);
    expect(
      guard.shouldApply(event("33333333-3333-3333-3333-333333333333", 1)),
    ).toBe(false);
    expect(
      guard.shouldApply(event("44444444-4444-4444-4444-444444444444", 3)),
    ).toBe(true);
  });

  it("bounds and expires the event id cache", () => {
    let now = 1_000;
    const guard = new RealtimeEventGuard(
      {
        maximumEventIds: 1,
        maximumAggregates: 1,
        eventTtlMilliseconds: 10,
      },
      () => now,
    );
    const first = event("22222222-2222-2222-2222-222222222222", 1);
    expect(guard.shouldApply(first)).toBe(true);
    expect(
      guard.shouldApply(event("33333333-3333-3333-3333-333333333333", 2)),
    ).toBe(true);
    now += 11;
    expect(guard.shouldApply({ ...first, aggregate_version: 3 })).toBe(true);
  });

  it("runs mandatory REST synchronization and replaces aggregate versions", async () => {
    const guard = new RealtimeEventGuard();
    const synchronize = vi.fn(async () => ({
      aggregate_versions: { [aggregateId]: 8 },
    }));

    await resynchronizeAfterReconnect(guard, synchronize);

    expect(synchronize).toHaveBeenCalledOnce();
    expect(
      guard.shouldApply(event("55555555-5555-5555-5555-555555555555", 7)),
    ).toBe(false);
    expect(
      guard.shouldApply(event("66666666-6666-6666-6666-666666666666", 9)),
    ).toBe(true);
  });
});

describe("connection URL", () => {
  it("adds only the untrusted organization selector to a private hub URL", () => {
    const url = new URL(
      buildPrivateHubUrl(
        "https://localhost:7443",
        "/hubs/operations",
        aggregateId,
      ),
    );
    expect(url.pathname).toBe("/hubs/operations");
    expect([...url.searchParams.keys()]).toEqual(["organization_id"]);
    expect(url.searchParams.get("organization_id")).toBe(aggregateId);
  });
});
