import { describe, expect, it, vi } from "vitest";
import {
  DriverStopsApiError,
  type DriverStopsApi,
  type DriverStopsListOptions,
} from "../api/driver-stops-api";
import {
  createDriverCachePartition,
  type DriverCachePartition,
  type DriverStopsCache,
  type DriverStopsSnapshot,
} from "../cache/driver-stops-cache";
import type { DriverStop, DriverStopList } from "../contracts/driver-stop";
import type {
  DriverStopsRealtimeCallbacks,
  DriverStopsRealtimeFactory,
} from "../realtime/driver-stops-realtime";
import type { DriverSession } from "../session/driver-session";
import type { DriverStopsTelemetry } from "../telemetry/driver-stops-telemetry";
import {
  DriverStopsController,
  mergeConfirmedStops,
  withoutStaleStops,
} from "./driver-stops-controller";
import {
  stopList,
  syntheticNavigation,
  withoutStopDetails,
} from "../contracts/driver-stop.fixtures";

const session: DriverSession = {
  organizationId: "11111111-1111-1111-1111-111111111111",
  cacheNamespace: "opaque-session-0001",
  getAccessToken: () => "memory-only",
};
const firstStop: DriverStop = {
  order_id: "22222222-2222-2222-2222-222222222222",
  aggregate_version: 3,
  order_public_id: "ORD_abcdefghijklmnopqrstuv",
  stop_type: "PICKUP",
  status: "ASSIGNED",
  address_summary: "Centro, Culiacán",
  details_available: true,
  contact_short_name: null,
  contact_full_name: null,
  zone_name: null,
  service_window: null,
  cod: null,
  navigation: syntheticNavigation,
};
const secondStop: DriverStop = {
  ...firstStop,
  order_id: "33333333-3333-3333-3333-333333333333",
  aggregate_version: 4,
  order_public_id: "ORD_bcdefghijklmnopqrstuvw",
  stop_type: "DELIVERY",
  status: "IN_TRANSIT",
};

describe("DriverStopsController", () => {
  it("replaces the complete snapshot and preserves REST order", async () => {
    const cache = new MemoryCache();
    const harness = createHarness(cache, [[firstStop, secondStop]]);
    await harness.controller.start();
    expect(harness.controller.current.phase).toBe("ready");
    expect(harness.controller.current.stops).toEqual([firstStop, secondStop]);
    expect(cache.replacements).toBe(1);
    expect(cache.onlySnapshot()?.stops).toEqual([firstStop, secondStop]);
  });

  it("a later 200 removes stops no longer assigned", async () => {
    const cache = new MemoryCache();
    const harness = createHarness(cache, [[firstStop, secondStop], [secondStop]]);
    await harness.controller.start();
    await harness.controller.retry();
    expect(harness.controller.current.stops).toEqual([secondStop]);
    expect(cache.onlySnapshot()?.stops).toEqual([secondStop]);
  });

  it("shows an empty response as a non-error state", async () => {
    const harness = createHarness(new MemoryCache(), [[]]);
    await harness.controller.start();
    expect(harness.controller.current.phase).toBe("empty");
  });

  it("falls back only to the active partition after network failure", async () => {
    const cache = new MemoryCache();
    const harness = createHarness(cache, [
      [firstStop],
      new DriverStopsApiError("recoverable"),
    ]);
    await harness.controller.start();
    await harness.controller.retry();
    expect(harness.controller.current.phase).toBe("offline");
    expect(harness.controller.current.stops).toEqual([firstStop]);
    expect(harness.controller.current.realtime).toBe("offline");
  });

  it("shows the safe offline-empty state when no snapshot exists", async () => {
    const harness = createHarness(new MemoryCache(), [
      new DriverStopsApiError("recoverable"),
    ]);
    await harness.controller.start();
    expect(harness.controller.current.phase).toBe("offline-empty");
    expect(harness.controller.current.stops).toEqual([]);
  });

  it.each(["unauthorized", "forbidden"] as const)(
    "%s clears memory and the active partition without offline fallback",
    async (category) => {
      const cache = new MemoryCache();
      const harness = createHarness(cache, [
        [firstStop],
        new DriverStopsApiError(category),
      ]);
      await harness.controller.start();
      await harness.controller.retry();
      expect(harness.controller.current.phase).toBe(category);
      expect(harness.controller.current.stops).toEqual([]);
      expect(cache.size).toBe(0);
      expect(cache.clears).toBe(1);
    },
  );

  it("invalid contract never overwrites or renders a prior snapshot", async () => {
    const cache = new MemoryCache();
    const harness = createHarness(cache, [
      [firstStop],
      new DriverStopsApiError("invalid-contract"),
    ]);
    await harness.controller.start();
    const prior = cache.onlySnapshot();
    await harness.controller.retry();
    expect(harness.controller.current.phase).toBe("invalid-contract");
    expect(harness.controller.current.stops).toEqual([]);
    expect(cache.onlySnapshot()).toEqual(prior);
    expect(cache.replacements).toBe(1);
  });

  it("a cancelled refresh never replaces cache", async () => {
    const cache = new MemoryCache();
    const harness = createHarness(cache, [
      [firstStop],
      new DriverStopsApiError("cancelled"),
    ]);
    await harness.controller.start();
    await harness.controller.retry();
    expect(cache.replacements).toBe(1);
  });

  it("debounces relevant realtime bursts and replaces from REST", async () => {
    vi.useFakeTimers();
    try {
      const harness = createHarness(new MemoryCache(), [
        [firstStop],
        [secondStop],
      ]);
      await harness.controller.start();
      harness.realtime.callbacks?.refreshFromSignal();
      harness.realtime.callbacks?.refreshFromSignal();
      harness.realtime.callbacks?.refreshFromSignal();
      expect(harness.api.calls).toBe(1);
      await vi.advanceTimersByTimeAsync(250);
      expect(harness.api.calls).toBe(2);
      expect(harness.controller.current.stops).toEqual([secondStop]);
    } finally {
      vi.useRealTimers();
    }
  });

  it("reconnect forces REST resynchronization and returns aggregate cursors", async () => {
    const harness = createHarness(new MemoryCache(), [
      [firstStop],
      [secondStop],
    ]);
    await harness.controller.start();
    harness.realtime.callbacks?.stateChanged("reconnecting");
    expect(harness.controller.current.realtime).toBe("reconnecting");
    const cursors = await harness.realtime.callbacks!.resynchronizeFromRest();
    expect(cursors).toEqual([secondStop]);
    expect(harness.controller.current.stops).toEqual([secondStop]);
  });

  it("realtime startup failure does not invalidate the REST snapshot", async () => {
    const harness = createHarness(new MemoryCache(), [[firstStop]], true);
    await harness.controller.start();
    expect(harness.controller.current.phase).toBe("ready");
    expect(harness.controller.current.stops).toEqual([firstStop]);
    expect(harness.controller.current.realtime).toBe("offline");
  });

  it("keeps today's completed count in the state and the saved snapshot", async () => {
    const cache = new MemoryCache();
    const harness = createHarness(cache, [stopList([firstStop], 2)]);
    await harness.controller.start();
    expect(harness.controller.current.completedToday).toBe(2);
    expect(cache.onlySnapshot()?.completedToday).toBe(2);
  });

  it("confirms without details and never wipes the details the phone holds", async () => {
    const cache = new MemoryCache();
    const confirmed = { ...withoutStopDetails(firstStop), aggregate_version: 4, status: "AT_PICKUP" as const };
    const harness = createHarness(cache, [
      stopList([firstStop]),
      stopList([confirmed], 1),
    ]);
    await harness.controller.start();

    const list = await harness.controller.refreshForSync();

    expect(harness.api.details).toEqual([true, false]);
    expect(list.stops).toEqual([confirmed]);
    const kept = { ...confirmed, details_available: true, navigation: firstStop.navigation };
    expect(harness.controller.current.stops).toEqual([kept]);
    expect(harness.controller.current.completedToday).toBe(1);
    expect(cache.onlySnapshot()?.stops).toEqual([kept]);
  });

  it("asks for the details of a stop a confirmation brought without them", async () => {
    vi.useFakeTimers();
    try {
      const harness = createHarness(new MemoryCache(), [
        stopList([firstStop]),
        stopList([withoutStopDetails(firstStop), withoutStopDetails(secondStop)]),
        stopList([firstStop, secondStop]),
      ]);
      await harness.controller.start();
      await harness.controller.refreshForSync();
      expect(harness.controller.current.stops[1].details_available).toBe(false);

      await vi.advanceTimersByTimeAsync(250);

      expect(harness.api.details).toEqual([true, false, true]);
      expect(harness.controller.current.stops).toEqual([firstStop, secondStop]);
    } finally {
      vi.useRealTimers();
    }
  });

  it("keeps the saved details when a full read cannot read them and asks no more", async () => {
    vi.useFakeTimers();
    try {
      const harness = createHarness(new MemoryCache(), [
        stopList([firstStop]),
        stopList([withoutStopDetails(firstStop), withoutStopDetails(secondStop)]),
      ]);
      await harness.controller.start();
      await harness.controller.retry();
      await vi.advanceTimersByTimeAsync(1_000);

      expect(harness.api.calls).toBe(2);
      expect(harness.controller.current.stops).toEqual([
        firstStop,
        withoutStopDetails(secondStop),
      ]);
    } finally {
      vi.useRealTimers();
    }
  });

  it("keeps the details saved on the phone when the first read after opening cannot read them", async () => {
    const cache = new MemoryCache();
    const partition = await createDriverCachePartition(session);
    await cache.replaceSnapshot(partition, {
      schemaVersion: 2,
      synchronizedAt: "2026-07-26T11:00:00.000Z",
      stops: [firstStop],
      completedToday: 0,
    });
    const harness = createHarness(cache, [
      stopList([withoutStopDetails(firstStop), withoutStopDetails(secondStop)]),
    ]);

    await harness.controller.start();

    expect(harness.controller.current.stops).toEqual([
      firstStop,
      withoutStopDetails(secondStop),
    ]);
    expect(cache.onlySnapshot()?.stops[0]).toEqual(firstStop);
  });

  it("never answers a confirmation with an empty list once closed or aborted", async () => {
    const harness = createHarness(new MemoryCache(), [stopList([firstStop]), stopList([])]);
    await expect(harness.controller.refreshForSync()).rejects.toMatchObject({
      category: "recoverable",
    });
    await harness.controller.start();
    const aborted = new AbortController();
    aborted.abort();
    await expect(harness.controller.refreshForSync(aborted.signal)).rejects.toMatchObject({
      category: "cancelled",
    });
    await harness.controller.dispose();
    await expect(harness.controller.refreshForSync()).rejects.toMatchObject({
      category: "cancelled",
    });
    expect(harness.api.calls).toBe(1);
  });

  it("refuses the confirmation when the logout happens while it is in flight", async () => {
    const cache = new MemoryCache();
    const harness = createHarness(cache, [stopList([firstStop]), stopList([])]);
    await harness.controller.start();
    harness.api.beforeAnswer = () => void harness.controller.dispose();

    await expect(harness.controller.refreshForSync()).rejects.toBeInstanceOf(
      DriverStopsApiError,
    );
    expect(cache.onlySnapshot()?.stops).toEqual([firstStop]);
  });

  it("reports stops left out or kept without details as count buckets only", async () => {
    const stopsRejected = vi.fn();
    const harness = createHarness(
      new MemoryCache(),
      [{ ...stopList([firstStop]), droppedCount: 2, degradedCount: 1 }],
      false,
      { ...telemetry, stopsRejected },
    );
    await harness.controller.start();
    expect(stopsRejected.mock.calls).toEqual([
      ["dropped", "2-5"],
      ["degraded", "1"],
    ]);
  });

  it("merges details only for the same order and stop type", () => {
    const pickup = firstStop;
    const nowDelivery = { ...withoutStopDetails(firstStop), stop_type: "DELIVERY" as const };
    expect(mergeConfirmedStops([pickup], [nowDelivery])).toEqual({
      stops: [nowDelivery],
      missingDetails: true,
    });
    expect(mergeConfirmedStops([pickup], [withoutStopDetails(pickup)])).toEqual({
      stops: [pickup],
      missingDetails: false,
    });
    expect(mergeConfirmedStops([pickup], [])).toEqual({
      stops: [],
      missingDetails: false,
    });
  });

  it("dispose cancels timers, requests and the previous connection", async () => {
    vi.useFakeTimers();
    try {
      const harness = createHarness(new MemoryCache(), [[firstStop]]);
      await harness.controller.start();
      harness.realtime.callbacks?.refreshFromSignal();
      await harness.controller.dispose();
      await vi.advanceTimersByTimeAsync(300);
      expect(harness.api.calls).toBe(1);
      expect(harness.realtime.stops).toBe(1);
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("stale stop reads (DRV-STALE-STOP-READ-2026-10-14)", () => {
  const atPickup: DriverStop = {
    ...firstStop,
    aggregate_version: 4,
    status: "AT_PICKUP",
  };

  it("keeps the newer stop when a full read that left before the confirmation lands after it", async () => {
    const cache = new MemoryCache();
    const harness = await gatedHarness(cache, [firstStop, secondStop]);

    // The reconnect read leaves while the stop is ASSIGNED at version 3.
    const resync = harness.realtime.callbacks!.resynchronizeFromRest();
    const stale = await harness.api.read(2);
    // The check-in is applied and its confirmation read is taken first.
    harness.api.server = [atPickup, secondStop];
    harness.api.completedToday = 1;
    const confirmation = harness.controller.refreshForSync();
    (await harness.api.read(3)).release();
    await confirmation;
    const confirmed = harness.controller.current.stops[0];
    expect(confirmed).toMatchObject({ status: "AT_PICKUP", aggregate_version: 4 });

    stale.release();
    const cursors = await resync;

    // The stop stays AT_PICKUP at version 4, with the details it had; the other
    // stop of the read and today's count are taken as before.
    expect(harness.controller.current.stops).toEqual([confirmed, secondStop]);
    expect(harness.controller.current.completedToday).toBe(0);
    expect(cache.onlySnapshot()?.stops).toEqual([confirmed, secondStop]);
    // The realtime cursor gets the version the phone kept.
    expect(cursors).toEqual([confirmed, secondStop]);
  });

  it("takes an equal version as before", async () => {
    const harness = await gatedHarness(new MemoryCache(), [atPickup]);
    const stale = harness.controller.retry();
    const full = await harness.api.read(2);
    // Same version, other data (for example a zone renamed meanwhile).
    harness.api.server = [{ ...atPickup, zone_name: "Zona sintética nueva" }];
    const confirmation = harness.controller.refreshForSync();
    (await harness.api.read(3)).release();
    await confirmation;
    expect(harness.controller.current.stops[0].zone_name).toBe("Zona sintética nueva");

    full.release();
    await stale;

    expect(harness.controller.current.stops).toEqual([atPickup]);
  });

  it("never brings back, from an older version, a stop that left the list meanwhile", async () => {
    const harness = await gatedHarness(new MemoryCache(), [firstStop, secondStop]);
    const stale = harness.controller.retry();
    const full = await harness.api.read(2);
    harness.api.server = [atPickup, secondStop];
    const checkIn = harness.controller.refreshForSync();
    (await harness.api.read(3)).release();
    await checkIn;
    // Dispatch takes the order away: it leaves the driver's list.
    harness.api.server = [secondStop];
    const unassigned = harness.controller.refreshForSync();
    (await harness.api.read(4)).release();
    await unassigned;
    expect(harness.controller.current.stops).toEqual([secondStop]);

    full.release();
    await stale;

    expect(harness.controller.current.stops).toEqual([secondStop]);
  });

  it("never brings back a stop that left the list meanwhile from its last accepted version either (Q6c)", async () => {
    const harness = await gatedHarness(new MemoryCache(), [firstStop, secondStop]);
    harness.api.server = [atPickup, secondStop];
    const checkIn = harness.controller.refreshForSync();
    const confirmation = await harness.api.read(2);
    // This full read leaves after the server answered version 4 too.
    const stale = harness.controller.retry();
    const full = await harness.api.read(3);
    confirmation.release();
    await checkIn;
    harness.api.server = [secondStop];
    const unassigned = harness.controller.refreshForSync();
    (await harness.api.read(4)).release();
    await unassigned;

    full.release();
    await stale;

    expect(harness.controller.current.stops).toEqual([secondStop]);
  });

  describe("a stop removed while another read is on its way (Q6c)", () => {
    const heldStop: DriverStop = { ...firstStop, aggregate_version: 5 };

    it("is not brought back, on screen or in the snapshot, by a slow full read that left before the removal", async () => {
      const cache = new MemoryCache();
      const harness = await gatedHarness(cache, [heldStop, secondStop]);
      // A slow full read leaves while the stop is assigned at version 5.
      const stale = harness.controller.retry();
      const full = await harness.api.read(2);
      // Dispatch takes the order away; the confirmation read is the first to
      // see it, and nothing it brings carries the order.
      harness.api.server = [secondStop];
      const confirmation = harness.controller.refreshForSync();
      (await harness.api.read(3)).release();
      await confirmation;
      expect(harness.controller.current.stops).toEqual([secondStop]);

      full.release();
      await stale;

      expect(harness.controller.current.stops).toEqual([secondStop]);
      expect(cache.onlySnapshot()?.stops).toEqual([secondStop]);
    });

    it("takes it again from a read sent after the removal (reassigned back), and the slow read still loses", async () => {
      const cache = new MemoryCache();
      const harness = await gatedHarness(cache, [heldStop, secondStop]);
      const stale = harness.controller.retry();
      const full = await harness.api.read(2);
      harness.api.server = [secondStop];
      const removal = harness.controller.refreshForSync();
      (await harness.api.read(3)).release();
      await removal;
      // Dispatch assigns the order to this driver again (version 7).
      const back: DriverStop = { ...heldStop, aggregate_version: 7 };
      harness.api.server = [back, secondStop];
      const after = harness.controller.refreshForSync();
      (await harness.api.read(4)).release();
      await after;
      expect(
        harness.controller.current.stops.map((stop) => [stop.order_id, stop.aggregate_version]),
      ).toEqual([
        [back.order_id, 7],
        [secondStop.order_id, 4],
      ]);
      const shown = harness.controller.current.stops;

      full.release();
      await stale;

      expect(harness.controller.current.stops).toEqual(shown);
      expect(cache.onlySnapshot()?.stops).toEqual(shown);
    });

    it("takes it from a read already on its way that brings it at the held version + 1 (closed without a version bump, then reassigned)", async () => {
      // D8 closes the assignment after FAILED_ATTEMPT -> RESCHEDULED without
      // bumping the order: the stop leaves the list at the version the app holds.
      const rescheduled: DriverStop = {
        ...heldStop,
        aggregate_version: 6,
        status: "RESCHEDULED",
      };
      const harness = await gatedHarness(new MemoryCache(), [rescheduled, secondStop]);
      harness.api.server = [secondStop];
      const closing = harness.controller.refreshForSync();
      const removal = await harness.api.read(2);
      // A new assignment to this driver bumps the order to version 7; a full
      // read answered after it is on its way when the removal is taken.
      const reassigned: DriverStop = {
        ...heldStop,
        aggregate_version: 7,
        status: "ASSIGNED",
      };
      harness.api.server = [reassigned, secondStop];
      const newer = harness.controller.retry();
      const full = await harness.api.read(3);
      removal.release();
      await closing;
      expect(harness.controller.current.stops).toEqual([secondStop]);

      full.release();
      await newer;

      expect(harness.controller.current.stops).toEqual([reassigned, secondStop]);
    });

    it("lets a read already on its way bring back, as today, a newly assigned stop that a stale read dropped", async () => {
      const harness = await gatedHarness(new MemoryCache(), [secondStop]);
      // A slow full read leaves before the order is assigned to this driver.
      const stale = harness.controller.retry();
      const full = await harness.api.read(2);
      harness.api.server = [firstStop, secondStop];
      const preRead = harness.controller.refreshForSync();
      (await harness.api.read(3)).release();
      await preRead;
      // The sync's confirmation read is on its way when the stale read lands.
      const transitioned: DriverStop = { ...secondStop, aggregate_version: 5 };
      harness.api.server = [firstStop, transitioned];
      const confirmation = harness.controller.refreshForSync();
      const confirmed = await harness.api.read(4);

      full.release();
      await stale;
      // Residual risk (DRV-001): the stale read drops the newly assigned stop.
      expect(harness.controller.current.stops).toEqual([secondStop]);

      confirmed.release();
      await confirmation;

      expect(
        harness.controller.current.stops.map((stop) => [stop.order_id, stop.aggregate_version]),
      ).toEqual([
        [firstStop.order_id, 3],
        [transitioned.order_id, 5],
      ]);
    });
  });

  it("drops, as before (DRV-001), a stop the late read does not bring", async () => {
    const harness = await gatedHarness(new MemoryCache(), [firstStop]);
    const stale = harness.controller.retry();
    const full = await harness.api.read(2);
    // A stop assigned meanwhile arrives with the confirmation read.
    harness.api.server = [firstStop, secondStop];
    const confirmation = harness.controller.refreshForSync();
    (await harness.api.read(3)).release();
    await confirmation;
    expect(harness.controller.current.stops).toHaveLength(2);

    full.release();
    await stale;

    // Residual risk: it is missing until the next read brings it again.
    expect(harness.controller.current.stops).toEqual([firstStop]);
  });

  it("applies a lower version from a read sent after the higher one was accepted (version reset)", async () => {
    const cache = new MemoryCache();
    const harness = await gatedHarness(cache, [atPickup]);
    // The database was restored from a backup: the order is at version 3 again.
    harness.api.server = [firstStop];

    const read = harness.controller.retry();
    (await harness.api.read(2)).release();
    await read;

    expect(harness.controller.current.stops).toEqual([firstStop]);
    expect(cache.onlySnapshot()?.stops).toEqual([firstStop]);
  });

  it("takes answers one at a time, so a stale one waiting for a snapshot write still loses", async () => {
    const cache = new GatedWriteCache();
    const harness = await gatedHarness(cache, [firstStop]);
    const stale = harness.controller.retry();
    const full = await harness.api.read(2);
    harness.api.server = [atPickup];
    const confirmation = harness.controller.refreshForSync();
    // The confirmation arrives first and its snapshot write is slow.
    cache.holdWrites = true;
    (await harness.api.read(3)).release();
    await vi.waitFor(() => expect(cache.heldWrites).toBe(1));
    // The stale answer arrives while that write is still pending.
    full.release();
    cache.releaseWrites();
    await confirmation;
    await stale;

    expect(harness.controller.current.stops[0]).toMatchObject({
      status: "AT_PICKUP",
      aggregate_version: 4,
    });
    expect(cache.onlySnapshot()?.stops[0]).toMatchObject({
      status: "AT_PICKUP",
      aggregate_version: 4,
    });
  });

  it("keeps the held stop, drops a stale stop that is not held and takes the rest", () => {
    const held = [atPickup];
    const incoming = [firstStop, { ...secondStop, aggregate_version: 3 }];
    const newer = new Map([
      [firstStop.order_id, 4],
      [secondStop.order_id, 5],
    ]);
    expect(withoutStaleStops(held, incoming, newer)).toEqual({
      stops: [atPickup],
      taken: [],
    });
    expect(withoutStaleStops(held, incoming, new Map())).toEqual({
      stops: incoming,
      taken: incoming,
    });
    expect(
      withoutStaleStops(held, [atPickup], new Map([[atPickup.order_id, 4]])),
    ).toEqual({ stops: [atPickup], taken: [atPickup] });
  });
});

class MemoryCache implements DriverStopsCache {
  private readonly records = new Map<string, DriverStopsSnapshot>();
  public replacements = 0;
  public clears = 0;

  public get size(): number {
    return this.records.size;
  }

  public onlySnapshot(): DriverStopsSnapshot | null {
    return [...this.records.values()][0] ?? null;
  }

  public async readSnapshot(
    partition: DriverCachePartition,
  ): Promise<DriverStopsSnapshot | null> {
    return this.records.get(partition.key) ?? null;
  }

  public async replaceSnapshot(
    partition: DriverCachePartition,
    snapshot: DriverStopsSnapshot,
  ): Promise<void> {
    this.replacements += 1;
    this.records.set(partition.key, structuredClone(snapshot));
  }

  public async clearPartition(partition: DriverCachePartition): Promise<void> {
    this.clears += 1;
    this.records.delete(partition.key);
  }
}

type SequenceResponse = readonly DriverStop[] | DriverStopList | DriverStopsApiError;

class SequenceApi implements DriverStopsApi {
  public calls = 0;
  /** Whether each call asked for the protected details. */
  public readonly details: boolean[] = [];
  public beforeAnswer: (() => void) | null = null;

  public constructor(private readonly responses: SequenceResponse[]) {}

  public async listStops(
    _signal?: AbortSignal,
    options?: DriverStopsListOptions,
  ): Promise<DriverStopList> {
    const response =
      this.responses[Math.min(this.calls, this.responses.length - 1)];
    this.calls += 1;
    this.details.push(options?.details !== false);
    this.beforeAnswer?.();
    if (response instanceof DriverStopsApiError) throw response;
    return Array.isArray(response)
      ? stopList(response as readonly DriverStop[])
      : (response as DriverStopList);
  }
}

class RealtimeHarness implements DriverStopsRealtimeFactory {
  public callbacks: DriverStopsRealtimeCallbacks | null = null;
  public starts = 0;
  public stops = 0;

  public constructor(private readonly failStart: boolean) {}

  public create(
    _baseUrl: string,
    _session: DriverSession,
    callbacks: DriverStopsRealtimeCallbacks,
  ) {
    this.callbacks = callbacks;
    return {
      start: async () => {
        this.starts += 1;
        if (this.failStart) throw new Error("offline");
      },
      stop: async () => {
        this.stops += 1;
      },
    };
  }
}

const telemetry: DriverStopsTelemetry = {
  loadCompleted: vi.fn(),
  loadFailed: vi.fn(),
  realtimeStateChanged: vi.fn(),
};

function createHarness(
  cache: DriverStopsCache,
  responses: SequenceResponse[],
  failRealtime = false,
  stopsTelemetry: DriverStopsTelemetry = telemetry,
) {
  const api = new SequenceApi(responses);
  const realtime = new RealtimeHarness(failRealtime);
  const controller = new DriverStopsController({
    baseUrl: "https://api.synthetic.test",
    session,
    api,
    cache,
    realtimeFactory: realtime,
    telemetry: stopsTelemetry,
    now: () => new Date("2026-07-26T12:00:00.000Z"),
  });
  return { controller, api, realtime };
}

/**
 * A stops API whose server answers when the request arrives (the current
 * `server` stops, without details for a confirmation read) and whose answer
 * reaches the app only when the test releases it.
 */
class GatedStopsApi implements DriverStopsApi {
  public server: readonly DriverStop[];
  public completedToday = 0;
  private readonly reads: { readonly release: () => void }[] = [];

  public constructor(server: readonly DriverStop[]) {
    this.server = server;
  }

  /** The n-th read (1-based) once it was sent. */
  public async read(count: number): Promise<{ readonly release: () => void }> {
    await vi.waitFor(() => expect(this.reads.length).toBeGreaterThanOrEqual(count));
    return this.reads[count - 1];
  }

  public async listStops(
    signal?: AbortSignal,
    options?: DriverStopsListOptions,
  ): Promise<DriverStopList> {
    const details = options?.details !== false;
    const answer = stopList(
      this.server.map((stop) => (details ? stop : withoutStopDetails(stop))),
      this.completedToday,
    );
    let release: () => void = () => undefined;
    const arrived = new Promise<void>((resolve) => {
      release = resolve;
    });
    this.reads.push({ release });
    await arrived;
    if (signal?.aborted) throw new DriverStopsApiError("cancelled");
    return answer;
  }
}

/** A snapshot store whose writes can be held, to make one answer slow to take. */
class GatedWriteCache extends MemoryCache {
  public holdWrites = false;
  public heldWrites = 0;
  private release: () => void = () => undefined;
  private readonly released = new Promise<void>((resolve) => {
    this.release = resolve;
  });

  public releaseWrites(): void {
    this.release();
  }

  public override async replaceSnapshot(
    partition: DriverCachePartition,
    snapshot: DriverStopsSnapshot,
  ): Promise<void> {
    if (this.holdWrites) {
      this.heldWrites += 1;
      await this.released;
    }
    await super.replaceSnapshot(partition, snapshot);
  }
}

/** A started controller over GatedStopsApi; its first read already answered `initial`. */
async function gatedHarness(
  cache: DriverStopsCache,
  initial: readonly DriverStop[],
) {
  const api = new GatedStopsApi(initial);
  const realtime = new RealtimeHarness(false);
  const controller = new DriverStopsController({
    baseUrl: "https://api.synthetic.test",
    session,
    api,
    cache,
    realtimeFactory: realtime,
    telemetry,
    now: () => new Date("2026-07-26T12:00:00.000Z"),
  });
  const started = controller.start();
  (await api.read(1)).release();
  await started;
  return { controller, api, realtime };
}
