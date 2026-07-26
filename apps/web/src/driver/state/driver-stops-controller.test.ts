import { describe, expect, it, vi } from "vitest";
import {
  DriverStopsApiError,
  type DriverStopsApi,
} from "../api/driver-stops-api";
import {
  type DriverCachePartition,
  type DriverStopsCache,
  type DriverStopsSnapshot,
} from "../cache/driver-stops-cache";
import type { DriverStop } from "../contracts/driver-stop";
import type {
  DriverStopsRealtimeCallbacks,
  DriverStopsRealtimeFactory,
} from "../realtime/driver-stops-realtime";
import type { DriverSession } from "../session/driver-session";
import type { DriverStopsTelemetry } from "../telemetry/driver-stops-telemetry";
import { DriverStopsController } from "./driver-stops-controller";

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

class SequenceApi implements DriverStopsApi {
  public calls = 0;

  public constructor(
    private readonly responses: Array<
      readonly DriverStop[] | DriverStopsApiError
    >,
  ) {}

  public async listStops(): Promise<readonly DriverStop[]> {
    const response =
      this.responses[Math.min(this.calls, this.responses.length - 1)];
    this.calls += 1;
    if (response instanceof DriverStopsApiError) throw response;
    return response;
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
  responses: Array<readonly DriverStop[] | DriverStopsApiError>,
  failRealtime = false,
) {
  const api = new SequenceApi(responses);
  const realtime = new RealtimeHarness(failRealtime);
  const controller = new DriverStopsController({
    baseUrl: "https://api.synthetic.test",
    session,
    api,
    cache,
    realtimeFactory: realtime,
    telemetry,
    now: () => new Date("2026-07-26T12:00:00.000Z"),
  });
  return { controller, api, realtime };
}
