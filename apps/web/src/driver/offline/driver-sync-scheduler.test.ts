import { describe, expect, it, vi } from "vitest";
import type { DriverCachePartition } from "../cache/driver-stops-cache";
import type { DriverStop, DriverStopList } from "../contracts/driver-stop";
import type { DriverOfflineQueue } from "./driver-offline-queue";
import {
  DriverSyncApiError,
  type DriverSyncApi,
} from "./driver-sync-api";
import {
  createDriverOfflineOperation,
  type DriverOfflineOperation,
  type DriverProofBlobRecord,
} from "./operation-contract";
import {
  DriverSyncBackoffMilliseconds,
  DriverSyncScheduler,
  driverProofSizeBucket,
  selectRunnableOperation,
} from "./driver-sync-scheduler";
import { OfflineOperationMaximumAgeMilliseconds } from "./offline-operation-age";
import { orderDriverOperations } from "./operation-projection";
import {
  stopList,
  syntheticNavigation,
} from "../contracts/driver-stop.fixtures";

const partition: DriverCachePartition = { key: "D".repeat(43) };
const orderA = "11111111-1111-4111-8111-111111111111";
const orderB = "22222222-2222-4222-8222-222222222222";

describe("driver sync scheduler", () => {
  it("uses the bounded canonical retry schedule", () => {
    expect(DriverSyncBackoffMilliseconds).toEqual([
      1_000, 2_000, 5_000, 10_000, 30_000,
    ]);
  });

  it.each([
    [0, "0-256KB"],
    [256 * 1024, "0-256KB"],
    [256 * 1024 + 1, "256KB-1MB"],
    [1024 * 1024, "256KB-1MB"],
    [1024 * 1024 + 1, "1-5MB"],
    [5 * 1024 * 1024, "1-5MB"],
    [5 * 1024 * 1024 + 1, "5-10MB"],
    [10 * 1024 * 1024, "5-10MB"],
  ])("reports only the bounded proof size bucket for %i bytes", (size, bucket) => {
    expect(driverProofSizeBucket(size)).toBe(bucket);
  });

  it("does not let one waiting order block another order", () => {
    const now = new Date("2026-07-26T12:00:00.000Z");
    const waiting = {
      ...operation("CHECK_IN", orderA, 1, 1),
      status: "WAITING_VALIDATION" as const,
      nextAttemptAt: "2026-07-26T12:00:30.000Z",
    };
    const ready = operation("START_DELIVERY", orderB, 7, 2);
    expect(selectRunnableOperation([waiting, ready], now, false)?.id).toBe(
      ready.id,
    );
    expect(selectRunnableOperation([waiting, ready], now, true)?.id).toBe(
      waiting.id,
    );
  });

  it("checks REST before resending a transition after a lost response", async () => {
    const queued = operation("CHECK_IN", orderA, 3, 1);
    const queue = new MemoryQueue([queued]);
    const transitionOrder = vi.fn();
    const stop: DriverStop = {
      order_id: orderA,
      aggregate_version: 4,
      order_public_id: "ORD_SYNTHETIC",
      stop_type: "PICKUP",
      status: "AT_PICKUP",
      address_summary: "Zona sintética",
      details_available: true,
      contact_short_name: null,
      contact_full_name: null,
      zone_name: null,
      service_window: null,
      cod: null,
      navigation: syntheticNavigation,
    };
    const scheduler = new DriverSyncScheduler({
      partition,
      queue,
      api: {
        transitionOrder,
        createProofUploadSession: vi.fn(),
        finalizeProof: vi.fn(),
        recordCodCollection: vi.fn(),
        openFailedDeliveryIncident: vi.fn(),
      } as DriverSyncApi,
      refreshStops: async () => stopList([stop]),
      onQueueChanged: vi.fn(),
      onAccessRevoked: vi.fn(),
      now: () => new Date("2026-07-26T12:00:00.000Z"),
      randomUuid: () => "99999999-9999-4999-8999-999999999999",
    });
    await scheduler.requestSync(true);
    expect(transitionOrder).not.toHaveBeenCalled();
    expect(queue.operations).toEqual([]);
    await scheduler.dispose();
  });

  it("polls REST with backoff without resending an accepted transition", async () => {
    const queued = {
      ...operation("CHECK_IN", orderA, 3, 1),
      status: "AWAITING_REST_CONFIRMATION" as const,
    };
    const queue = new MemoryQueue([queued]);
    const transitionOrder = vi.fn();
    const scheduler = schedulerFor(queue, { transitionOrder });

    await scheduler.requestSync(true);

    expect(transitionOrder).not.toHaveBeenCalled();
    expect(queue.operations[0]).toMatchObject({
      status: "AWAITING_REST_CONFIRMATION",
      attemptCount: 1,
      nextAttemptAt: "2026-07-26T12:00:01.000Z",
    });
    await scheduler.dispose();
  });

  it.each([
    ["unauthorized", "unauthorized"],
    ["forbidden", "forbidden"],
  ] as const)("clears the active partition on %s", async (category, revoked) => {
    const queued = operation("CHECK_IN", orderA, 3, 1);
    const queue = new MemoryQueue([queued]);
    const onAccessRevoked = vi.fn();
    const scheduler = schedulerFor(queue, {
      transitionOrder: vi
        .fn()
        .mockRejectedValue(new DriverSyncApiError(category)),
    }, onAccessRevoked);

    await scheduler.requestSync(true);

    expect(queue.operations).toEqual([]);
    expect(queue.clearCount).toBe(1);
    expect(onAccessRevoked).toHaveBeenCalledWith(revoked);
    await scheduler.dispose();
  });

  it("purges a missing resource without sending or probing another API", async () => {
    const queued = operation("DELIVERY_PROOF", orderA, 8, 1);
    const queue = new MemoryQueue([queued]);
    const transitionOrder = vi.fn();
    const scheduler = schedulerFor(
      queue,
      { transitionOrder },
      vi.fn(),
      async () => stopList([]),
    );

    await scheduler.requestSync(true);

    expect(queue.operations).toEqual([]);
    expect(transitionOrder).not.toHaveBeenCalled();
    await scheduler.dispose();
  });

  it("defers recoverable failures with the first bounded delay", async () => {
    const queued = operation("CHECK_IN", orderA, 3, 1);
    const queue = new MemoryQueue([queued]);
    const scheduler = schedulerFor(queue, {
      transitionOrder: vi
        .fn()
        .mockRejectedValue(new DriverSyncApiError("recoverable")),
    });

    await scheduler.requestSync(true);

    expect(queue.operations[0]).toMatchObject({
      status: "RETRY_WAIT",
      attemptCount: 1,
      nextAttemptAt: "2026-07-26T12:00:01.000Z",
      safeError: "NETWORK",
    });
    await scheduler.dispose();
  });

  it("marks a conflict and blocks only dependent operations", async () => {
    const first = operation("CHECK_IN", orderA, 3, 1);
    const dependent = operation("PICKUP_PROOF", orderA, 4, 2);
    const independent = operation("START_DELIVERY", orderB, 8, 3);
    const queue = new MemoryQueue([first, dependent, independent]);
    let orderBConfirmed = false;
    const transitionOrder = vi
      .fn()
      .mockRejectedValueOnce(new DriverSyncApiError("conflict"))
      .mockImplementation(async () => {
        orderBConfirmed = true;
        return {
          id: orderB,
          status: "DELIVERING",
          version: 9,
        };
      });
    const scheduler = schedulerFor(
      queue,
      { transitionOrder },
      vi.fn(),
      async () => stopList([
        {
          order_id: orderA,
          aggregate_version: 3,
          order_public_id: "ORD_A",
          stop_type: "PICKUP",
          status: "ASSIGNED",
          address_summary: "Zona A",
          details_available: true,
          contact_short_name: null,
          contact_full_name: null,
          zone_name: null,
          service_window: null,
          cod: null,
          navigation: syntheticNavigation,
        },
        {
          order_id: orderB,
          aggregate_version: orderBConfirmed ? 9 : 8,
          order_public_id: "ORD_B",
          stop_type: "DELIVERY",
          status: orderBConfirmed ? "DELIVERING" : "IN_TRANSIT",
          address_summary: "Zona B",
          details_available: true,
          contact_short_name: null,
          contact_full_name: null,
          zone_name: null,
          service_window: null,
          cod: null,
          navigation: syntheticNavigation,
        },
      ]),
    );

    await scheduler.requestSync(true);

    expect(queue.operations.find((candidate) => candidate.id === first.id))
      .toMatchObject({ status: "NEEDS_ATTENTION" });
    expect(queue.operations.find((candidate) => candidate.id === dependent.id))
      .toMatchObject({ status: "BLOCKED" });
    expect(transitionOrder).toHaveBeenCalledTimes(2);
    await scheduler.dispose();
  });

  it("honors an existing lease and never sends from a second tab", async () => {
    const queue = new MemoryQueue([operation("CHECK_IN", orderA, 3, 1)]);
    queue.leaseAvailable = false;
    const transitionOrder = vi.fn();
    const scheduler = schedulerFor(queue, { transitionOrder });

    await scheduler.requestSync(true);

    expect(transitionOrder).not.toHaveBeenCalled();
    expect(queue.operations).toHaveLength(1);
    await scheduler.dispose();
  });

  it("renews the partition lease while network work remains active", async () => {
    vi.useFakeTimers();
    try {
      const queue = new MemoryQueue([operation("CHECK_IN", orderA, 3, 1)]);
      const transitionOrder = vi.fn(
        (_operation: DriverOfflineOperation, signal?: AbortSignal) =>
          new Promise<never>((_resolve, reject) => {
            signal?.addEventListener(
              "abort",
              () => reject(new DriverSyncApiError("cancelled")),
              { once: true },
            );
          }),
      );
      const scheduler = new DriverSyncScheduler({
        partition,
        queue,
        api: {
          transitionOrder,
          createProofUploadSession: vi.fn(),
          finalizeProof: vi.fn(),
          recordCodCollection: vi.fn(),
          openFailedDeliveryIncident: vi.fn(),
        },
        refreshStops: async () => stopList([{
          order_id: orderA,
          aggregate_version: 3,
          order_public_id: "ORD_A",
          stop_type: "PICKUP",
          status: "ASSIGNED",
          address_summary: "Zona A",
          details_available: true,
          contact_short_name: null,
          contact_full_name: null,
          zone_name: null,
          service_window: null,
          cod: null,
          navigation: syntheticNavigation,
        }]),
        onQueueChanged: vi.fn(),
        onAccessRevoked: vi.fn(),
        now: () => new Date("2026-07-26T12:00:00.000Z"),
        randomUuid: () => "99999999-9999-4999-8999-999999999999",
        leaseMilliseconds: 3_000,
      });

      const active = scheduler.requestSync(true);
      await vi.advanceTimersByTimeAsync(1_000);

      expect(transitionOrder).toHaveBeenCalledOnce();
      expect(queue.renewCount).toBeGreaterThanOrEqual(2);
      await scheduler.dispose();
      await active;
      expect(queue.releaseCount).toBeGreaterThanOrEqual(1);
    } finally {
      vi.useRealTimers();
    }
  });
});

/**
 * docs/development/driver-offline-replay-flake.md: a page that goes away
 * mid-pass (a reload, a closed or discarded tab) leaves the partition lease
 * active for its 15 seconds. A sync asked for meanwhile must not be lost.
 */
describe("a sync lease left by a page that went away", () => {
  const start = new Date("2026-07-26T12:00:30.000Z");

  it("replays the queue on its own after the connection returns, once that lease expires", async () => {
    vi.useFakeTimers({ now: start });
    try {
      const queue = new MemoryQueue([operation("CHECK_IN", orderA, 3, 1)]);
      // The page before an offline reload took the lease and went away mid-pass.
      queue.lease = { ownerId: "page-that-went-away", expiresAt: start.getTime() + 15_000 };
      const page = fakePage({ online: false });
      const { scheduler, transitionOrder } = leaseScheduler(queue, page);

      scheduler.start();
      await vi.advanceTimersByTimeAsync(2_000);
      page.goOnline();
      await vi.advanceTimersByTimeAsync(0);
      // That lease is still active: nothing is sent yet.
      expect(transitionOrder).not.toHaveBeenCalled();
      expect(queue.operations).toHaveLength(1);

      // No reload, visibility change or "Sincronizar ahora" follows: the
      // request made when the connection returned runs once the lease expired.
      await vi.advanceTimersByTimeAsync(15_000);
      expect(transitionOrder).toHaveBeenCalledOnce();
      expect(queue.operations).toEqual([]);
      expect(queue.lease).toBeNull();
      await scheduler.dispose();
    } finally {
      vi.useRealTimers();
    }
  });

  it("never sends while another page keeps its lease and sends once that page releases it", async () => {
    vi.useFakeTimers({ now: start });
    try {
      const queue = new MemoryQueue([operation("CHECK_IN", orderA, 3, 1)]);
      queue.lease = { ownerId: "other-tab", expiresAt: start.getTime() + 15_000 };
      const page = fakePage({ online: true });
      const { scheduler, transitionOrder } = leaseScheduler(queue, page);

      scheduler.start();
      await vi.advanceTimersByTimeAsync(10_000);
      // The other tab is still at work and renews its lease.
      queue.lease = { ownerId: "other-tab", expiresAt: Date.now() + 15_000 };
      await vi.advanceTimersByTimeAsync(10_000);
      expect(transitionOrder).not.toHaveBeenCalled();

      queue.lease = null;
      await vi.advanceTimersByTimeAsync(15_000);
      expect(transitionOrder).toHaveBeenCalledOnce();
      expect(queue.operations).toEqual([]);
      await scheduler.dispose();
    } finally {
      vi.useRealTimers();
    }
  });

  it("waits while the page is hidden and syncs when it is shown again", async () => {
    vi.useFakeTimers({ now: start });
    try {
      const queue = new MemoryQueue([operation("CHECK_IN", orderA, 3, 1)]);
      queue.lease = { ownerId: "page-that-went-away", expiresAt: start.getTime() + 15_000 };
      const page = fakePage({ online: true });
      const { scheduler, transitionOrder } = leaseScheduler(queue, page);

      scheduler.start();
      await vi.advanceTimersByTimeAsync(0);
      page.hide();
      await vi.advanceTimersByTimeAsync(60_000);
      expect(transitionOrder).not.toHaveBeenCalled();

      page.show();
      await vi.advanceTimersByTimeAsync(0);
      expect(transitionOrder).toHaveBeenCalledOnce();
      expect(queue.operations).toEqual([]);
      await scheduler.dispose();
    } finally {
      vi.useRealTimers();
    }
  });
});

/** A CHECK_IN of orderA at version 3 that REST shows applied once it is sent. */
function leaseScheduler(queue: DriverOfflineQueue, page: FakePage) {
  let applied = false;
  const transitionOrder = vi.fn(async () => {
    applied = true;
    return { id: orderA, status: "AT_PICKUP" as const, version: 4 };
  });
  const scheduler = new DriverSyncScheduler({
    partition,
    queue,
    api: {
      transitionOrder,
      createProofUploadSession: vi.fn(),
      finalizeProof: vi.fn(),
      recordCodCollection: vi.fn(),
      openFailedDeliveryIncident: vi.fn(),
    },
    refreshStops: async () =>
      stopList([
        {
          order_id: orderA,
          aggregate_version: applied ? 4 : 3,
          order_public_id: "ORD_A",
          stop_type: "PICKUP",
          status: applied ? "AT_PICKUP" : "ASSIGNED",
          address_summary: "Zona A",
          details_available: true,
          contact_short_name: null,
          contact_full_name: null,
          zone_name: null,
          service_window: null,
          cod: null,
          navigation: syntheticNavigation,
        },
      ]),
    onQueueChanged: vi.fn(),
    onAccessRevoked: vi.fn(),
    now: () => new Date(),
    randomUuid: () => "99999999-9999-4999-8999-999999999999",
    window: page.window,
  });
  return { scheduler, transitionOrder };
}

interface FakePage {
  readonly window: NonNullable<ConstructorParameters<typeof DriverSyncScheduler>[0]["window"]>;
  goOnline(): void;
  hide(): void;
  show(): void;
}

/** The browser events the scheduler listens to: `online` and `visibilitychange`. */
function fakePage(options: { readonly online: boolean }): FakePage {
  const events = new EventTarget();
  const document = Object.assign(new EventTarget(), {
    visibilityState: "visible" as DocumentVisibilityState,
  });
  const navigator = { onLine: options.online };
  return {
    window: {
      addEventListener: events.addEventListener.bind(events),
      removeEventListener: events.removeEventListener.bind(events),
      navigator,
      document,
    } as unknown as FakePage["window"],
    goOnline() {
      navigator.onLine = true;
      events.dispatchEvent(new Event("online"));
    },
    hide() {
      document.visibilityState = "hidden";
      document.dispatchEvent(new Event("visibilitychange"));
    },
    show() {
      document.visibilityState = "visible";
      document.dispatchEvent(new Event("visibilitychange"));
    },
  };
}

describe("confirmation reads that cannot be trusted (UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11)", () => {
  it("keeps the queued action and ends the pass when the confirmation read cannot answer", async () => {
    const queued = operation("CHECK_IN", orderA, 3, 1);
    const queue = new MemoryQueue([queued]);
    const transitionOrder = vi.fn();
    const refreshStops = vi.fn(async (): Promise<DriverStopList> => {
      throw new DriverSyncApiError("cancelled");
    });
    const scheduler = schedulerFor(
      queue,
      { transitionOrder },
      vi.fn(),
      refreshStops,
    );

    await scheduler.requestSync(true);

    expect(refreshStops).toHaveBeenCalledOnce();
    expect(transitionOrder).not.toHaveBeenCalled();
    expect(queue.operations).toEqual([queued]);
    await scheduler.dispose();
  });

  it("keeps the queued action when the driver logs out during a pass", async () => {
    const queued = operation("CHECK_IN", orderA, 3, 1);
    const queue = new MemoryQueue([queued]);
    const transitionOrder = vi.fn();
    const holder: { scheduler?: DriverSyncScheduler; logout?: Promise<void> } =
      {};
    const refreshStops = vi.fn(
      async (signal?: AbortSignal): Promise<DriverStopList> => {
        // "Cerrar sesión" stops every controller while the read is in flight;
        // the closed stops controller throws instead of answering an empty list.
        holder.logout = holder.scheduler!.dispose();
        expect(signal?.aborted).toBe(true);
        throw new DriverSyncApiError("cancelled");
      },
    );
    holder.scheduler = schedulerFor(
      queue,
      { transitionOrder },
      vi.fn(),
      refreshStops,
    );

    await holder.scheduler.requestSync(true);
    await holder.logout;

    expect(refreshStops).toHaveBeenCalledOnce();
    expect(transitionOrder).not.toHaveBeenCalled();
    expect(queue.operations).toEqual([queued]);
    expect(queue.clearCount).toBe(0);
  });

  it("never takes a stop for gone when the answer left out a stop that broke the contract", async () => {
    const queued = operation("CHECK_IN", orderA, 3, 1);
    const queue = new MemoryQueue([queued]);
    const transitionOrder = vi.fn(async () => ({
      id: orderA,
      status: "AT_PICKUP" as const,
      version: 4,
    }));
    const scheduler = schedulerFor(
      queue,
      { transitionOrder },
      vi.fn(),
      async () => ({ ...stopList([]), droppedCount: 1 }),
    );

    await scheduler.requestSync(true);

    expect(transitionOrder).toHaveBeenCalledOnce();
    expect(queue.operations).toEqual([
      expect.objectContaining({
        id: queued.id,
        status: "AWAITING_REST_CONFIRMATION",
      }),
    ]);
    await scheduler.dispose();
  });

  it("orders each order's actions by dependency and the orders by their oldest action", () => {
    const now = new Date("2026-07-26T12:00:00.000Z");
    const otherOrder = operation("START_DELIVERY", orderB, 8, 1);
    const delivery = operation("DELIVERY_PROOF", orderA, 9, 2);
    const collection = codOperation(orderA, 9, 3);

    expect(
      orderDriverOperations([collection, delivery, otherOrder]).map(
        (value) => value.id,
      ),
    ).toEqual([otherOrder.id, collection.id, delivery.id]);
    expect(
      selectRunnableOperation([delivery, collection], now, false)?.id,
    ).toBe(collection.id);
    expect(
      selectRunnableOperation(
        [delivery, collection],
        now,
        false,
        new Set([orderA]),
      )?.id,
    ).toBe(collection.id);
    expect(
      selectRunnableOperation([delivery], now, false, new Set([orderA])),
    ).toBeNull();
  });
});

describe("OPS-003 offline operation expiry", () => {
  const captured = "2026-07-26T12:00:01.000Z";
  const justInside = new Date(
    Date.parse(captured) + OfflineOperationMaximumAgeMilliseconds,
  );
  const justOutside = new Date(justInside.getTime() + 1);

  it("replays the same capture instant on every attempt", async () => {
    const queue = new MemoryQueue([operation("CHECK_IN", orderA, 3, 1)]);
    const seen: string[] = [];
    const transitionOrder = vi.fn(async (value: DriverOfflineOperation) => {
      seen.push(value.clientOccurredAt);
      throw new DriverSyncApiError("recoverable");
    });
    const scheduler = expiryScheduler(queue, { transitionOrder }, justInside);

    await scheduler.requestSync(true);
    await scheduler.requestSync(true);

    expect(seen).toEqual([captured, captured]);
    expect(queue.operations[0]).toMatchObject({
      clientOccurredAt: captured,
      status: "RETRY_WAIT",
    });
    await scheduler.dispose();
  });

  it("still sends an operation exactly at the maximum age", async () => {
    const queue = new MemoryQueue([operation("CHECK_IN", orderA, 3, 1)]);
    const transitionOrder = vi
      .fn()
      .mockRejectedValue(new DriverSyncApiError("recoverable"));
    const onOperationExpired = vi.fn();
    const scheduler = expiryScheduler(
      queue,
      { transitionOrder },
      justInside,
      onOperationExpired,
    );

    await scheduler.requestSync(true);

    expect(transitionOrder).toHaveBeenCalledOnce();
    expect(onOperationExpired).not.toHaveBeenCalled();
    expect(queue.operations).toHaveLength(1);
    await scheduler.dispose();
  });

  it("drops an operation past the maximum age locally without sending it", async () => {
    const queued = operation("CHECK_IN", orderA, 3, 1);
    const queue = new MemoryQueue([queued]);
    const transitionOrder = vi.fn();
    const onOperationExpired = vi.fn();
    const scheduler = expiryScheduler(
      queue,
      { transitionOrder },
      justOutside,
      onOperationExpired,
    );

    await scheduler.requestSync(true);

    expect(transitionOrder).not.toHaveBeenCalled();
    expect(queue.operations).toEqual([]);
    expect(onOperationExpired).toHaveBeenCalledExactlyOnceWith(
      expect.objectContaining({ id: queued.id }),
    );
    await scheduler.dispose();
  });

  it.each<[string, Error]>([
    ["a network failure", new DriverSyncApiError("recoverable")],
    ["an answer that breaks the contract", new DriverSyncApiError("invalid-contract")],
    ["an unexpected error", new Error("synthetic")],
  ])(
    "keeps an accepted action waiting for REST when the read right after the 2xx fails with %s, so it never expires as unsent",
    async (_name, failure) => {
      const queued = operation("CHECK_IN", orderA, 3, 1);
      const queue = new MemoryQueue([queued]);
      const transitionOrder = vi.fn(async () => ({
        id: orderA,
        status: "AT_PICKUP" as const,
        version: 4,
      }));
      const onOperationExpired = vi.fn();
      const answers: (() => DriverStopList)[] = [
        // Before sending: REST does not show the action yet.
        () => stopList([stopA("ASSIGNED", 3)]),
        // The confirmation read right after the server accepted it.
        () => {
          throw failure;
        },
        // A later read that still lags behind.
        () => stopList([stopA("ASSIGNED", 3)]),
      ];
      const refreshStops = vi.fn(async () => {
        const answer = answers.shift();
        if (!answer) throw new Error("unexpected read");
        return answer();
      });
      let clock = new Date(captured);
      const scheduler = new DriverSyncScheduler({
        partition,
        queue,
        api: {
          transitionOrder,
          createProofUploadSession: vi.fn(),
          finalizeProof: vi.fn(),
          recordCodCollection: vi.fn(),
          openFailedDeliveryIncident: vi.fn(),
        },
        refreshStops,
        onQueueChanged: vi.fn(),
        onAccessRevoked: vi.fn(),
        onOperationExpired,
        now: () => clock,
        randomUuid: () => "99999999-9999-4999-8999-999999999999",
        upload: vi.fn().mockResolvedValue(undefined),
      });

      await scheduler.requestSync(true);
      expect(transitionOrder).toHaveBeenCalledOnce();
      expect(queue.operations).toEqual([
        expect.objectContaining({
          id: queued.id,
          status: "AWAITING_REST_CONFIRMATION",
          attemptCount: 1,
          nextAttemptAt: "2026-07-26T12:00:02.000Z",
          safeError: null,
        }),
      ]);

      // Still within the window, REST lags: it keeps waiting and is not sent again.
      clock = new Date(Date.parse(captured) + 60_000);
      await scheduler.requestSync(true);
      expect(queue.operations).toEqual([
        expect.objectContaining({ status: "AWAITING_REST_CONFIRMATION", attemptCount: 2 }),
      ]);

      // Past the OPS-003 window it leaves the queue without the "venció" notice: the server has it.
      clock = justOutside;
      await scheduler.requestSync(true);
      expect(queue.operations).toEqual([]);
      expect(transitionOrder).toHaveBeenCalledOnce();
      expect(onOperationExpired).not.toHaveBeenCalled();
      expect(refreshStops).toHaveBeenCalledTimes(3);
      await scheduler.dispose();
    },
  );

  it("confirms by REST before expiring an action the server already applied", async () => {
    const queue = new MemoryQueue([operation("CHECK_IN", orderA, 3, 1)]);
    const onOperationExpired = vi.fn();
    const scheduler = expiryScheduler(
      queue,
      {},
      justOutside,
      onOperationExpired,
      async () => stopList([stopA("AT_PICKUP", 4)]),
    );

    await scheduler.requestSync(true);

    expect(queue.operations).toEqual([]);
    expect(onOperationExpired).not.toHaveBeenCalled();
    await scheduler.dispose();
  });

  it("drops the operation permanently on 409 OFFLINE_OPERATION_EXPIRED and never retries it", async () => {
    const queued = operation("CHECK_IN", orderA, 3, 1);
    const queue = new MemoryQueue([queued]);
    const transitionOrder = vi
      .fn()
      .mockRejectedValue(
        new DriverSyncApiError("conflict", "OFFLINE_OPERATION_EXPIRED"),
      );
    const onOperationExpired = vi.fn();
    const scheduler = expiryScheduler(
      queue,
      { transitionOrder },
      justInside,
      onOperationExpired,
    );

    await scheduler.requestSync(true);
    await scheduler.requestSync(true);

    expect(queue.operations).toEqual([]);
    expect(transitionOrder).toHaveBeenCalledOnce();
    expect(onOperationExpired).toHaveBeenCalledExactlyOnceWith(
      expect.objectContaining({ id: queued.id }),
    );
    await scheduler.dispose();
  });

  it("holds the dependent chain of an expired operation for attention", async () => {
    const expired = operation("CHECK_IN", orderA, 3, 1);
    const dependent = operation("START_TRANSIT", orderA, 4, 2);
    const later = operation("START_DELIVERY", orderA, 5, 3);
    const independent = operation("START_DELIVERY", orderB, 8, 4);
    const queue = new MemoryQueue([expired, dependent, later, independent]);
    const transitionOrder = vi
      .fn()
      .mockRejectedValueOnce(
        new DriverSyncApiError("conflict", "OFFLINE_OPERATION_EXPIRED"),
      )
      .mockRejectedValue(new DriverSyncApiError("recoverable"));
    const onOperationExpired = vi.fn();
    const scheduler = expiryScheduler(
      queue,
      { transitionOrder },
      justInside,
      onOperationExpired,
    );

    await scheduler.requestSync(true);

    expect(queue.operations.map((value) => [value.id, value.status])).toEqual([
      [dependent.id, "NEEDS_ATTENTION"],
      [later.id, "BLOCKED"],
      [independent.id, "RETRY_WAIT"],
    ]);
    expect(queue.operations[0].safeError).toBe("VERSION_CONFLICT");
    expect(onOperationExpired).toHaveBeenCalledExactlyOnceWith(
      expect.objectContaining({ id: expired.id }),
    );
    expect(transitionOrder).toHaveBeenCalledTimes(2);
    await scheduler.dispose();
  });

  it("also drops dependents that are themselves past the maximum age", async () => {
    const expired = operation("CHECK_IN", orderA, 3, 1);
    const dependent = operation("START_TRANSIT", orderA, 4, 2);
    const queue = new MemoryQueue([expired, dependent]);
    const onOperationExpired = vi.fn();
    const scheduler = expiryScheduler(
      queue,
      {},
      new Date(Date.parse(dependent.clientOccurredAt) + OfflineOperationMaximumAgeMilliseconds + 1),
      onOperationExpired,
    );

    await scheduler.requestSync(true);

    expect(queue.operations).toEqual([]);
    expect(onOperationExpired.mock.calls.map(([value]) => value.id)).toEqual([
      expired.id,
      dependent.id,
    ]);
    await scheduler.dispose();
  });

  it("discards an action held for attention once it outlives the window", async () => {
    const held = {
      ...operation("CHECK_IN", orderA, 3, 1),
      status: "NEEDS_ATTENTION" as const,
      safeError: "VERSION_CONFLICT" as const,
    };
    const blocked = {
      ...operation("START_TRANSIT", orderA, 4, 2),
      status: "BLOCKED" as const,
    };
    const inside = new MemoryQueue([held, blocked]);
    const insideScheduler = expiryScheduler(inside, {}, justInside);
    await insideScheduler.requestSync(true);
    expect(inside.operations.map((value) => value.status)).toEqual([
      "NEEDS_ATTENTION",
      "BLOCKED",
    ]);
    await insideScheduler.dispose();

    const outside = new MemoryQueue([held, blocked]);
    const onOperationExpired = vi.fn();
    const transitionOrder = vi.fn();
    const outsideScheduler = expiryScheduler(
      outside,
      { transitionOrder },
      justOutside,
      onOperationExpired,
    );
    await outsideScheduler.requestSync(true);

    expect(outside.operations.map((value) => [value.id, value.status])).toEqual([
      [blocked.id, "NEEDS_ATTENTION"],
    ]);
    expect(onOperationExpired).toHaveBeenCalledExactlyOnceWith(
      expect.objectContaining({ id: held.id }),
    );
    expect(transitionOrder).not.toHaveBeenCalled();
    await outsideScheduler.dispose();
  });

  it.each(["createProofUploadSession", "finalizeProof"] as const)(
    "drops a proof operation and its local photo when %s answers OFFLINE_OPERATION_EXPIRED",
    async (step) => {
      const queued = operation("PICKUP_PROOF", orderA, 4, 1);
      const queue = new MemoryQueue([queued]);
      queue.proofs.set(queued.id, proofFor(queued));
      const expiredError = new DriverSyncApiError(
        "conflict",
        "OFFLINE_OPERATION_EXPIRED",
      );
      const api = {
        createProofUploadSession:
          step === "createProofUploadSession"
            ? vi.fn().mockRejectedValue(expiredError)
            : vi.fn().mockResolvedValue(grant()),
        finalizeProof: vi.fn().mockRejectedValue(expiredError),
        transitionOrder: vi.fn(),
      };
      const onOperationExpired = vi.fn();
      const scheduler = expiryScheduler(
        queue,
        api,
        justInside,
        onOperationExpired,
        async () => stopList([stopA("AT_PICKUP", 4)]),
      );

      await scheduler.requestSync(true);

      expect(queue.operations).toEqual([]);
      expect(queue.proofs.size).toBe(0);
      expect(api.transitionOrder).not.toHaveBeenCalled();
      expect(api.finalizeProof).toHaveBeenCalledTimes(
        step === "finalizeProof" ? 1 : 0,
      );
      expect(onOperationExpired).toHaveBeenCalledExactlyOnceWith(
        expect.objectContaining({ id: queued.id }),
      );
      await scheduler.dispose();
    },
  );

  it("keeps a transition 409 without code as a conflict for attention (clock ahead or state)", async () => {
    const queue = new MemoryQueue([operation("CHECK_IN", orderA, 3, 1)]);
    const onOperationExpired = vi.fn();
    const scheduler = expiryScheduler(
      queue,
      {
        transitionOrder: vi
          .fn()
          .mockRejectedValue(new DriverSyncApiError("conflict")),
      },
      justInside,
      onOperationExpired,
    );

    await scheduler.requestSync(true);

    expect(queue.operations[0]).toMatchObject({
      status: "NEEDS_ATTENTION",
      safeError: "VERSION_CONFLICT",
    });
    expect(onOperationExpired).not.toHaveBeenCalled();
    await scheduler.dispose();
  });

  it("keeps a proof 409 INVALID_REQUEST as rejected evidence (clock ahead)", async () => {
    const queued = operation("PICKUP_PROOF", orderA, 4, 1);
    const queue = new MemoryQueue([queued]);
    queue.proofs.set(queued.id, proofFor(queued));
    const onOperationExpired = vi.fn();
    const scheduler = expiryScheduler(
      queue,
      {
        createProofUploadSession: vi
          .fn()
          .mockRejectedValue(
            new DriverSyncApiError("conflict", "INVALID_REQUEST"),
          ),
      },
      justInside,
      onOperationExpired,
      async () => stopList([stopA("AT_PICKUP", 4)]),
    );

    await scheduler.requestSync(true);

    expect(queue.operations[0]).toMatchObject({
      id: queued.id,
      status: "NEEDS_ATTENTION",
      safeError: "EVIDENCE_REJECTED",
    });
    expect(queue.proofs.has(queued.id)).toBe(true);
    expect(onOperationExpired).not.toHaveBeenCalled();
    await scheduler.dispose();
  });
});

describe("SEC-004 photo limits", () => {
  // Owner answer Q2 ("60 cada paso"): past the limit, "la app espera lo que
  // indique el servidor y sigue sola"; Q6: "la cola espera lo que indique el
  // servidor y reintenta sin mensaje".
  const start = Date.parse("2026-07-26T12:00:30.000Z");

  function photoScheduler(
    queue: MemoryQueue,
    api: Partial<DriverSyncApi>,
    clock: { now: number },
  ): DriverSyncScheduler {
    return new DriverSyncScheduler({
      partition,
      queue,
      api: {
        transitionOrder: vi.fn(),
        createProofUploadSession: vi.fn(),
        finalizeProof: vi.fn(),
        recordCodCollection: vi.fn(),
        openFailedDeliveryIncident: vi.fn(),
        ...api,
      },
      refreshStops: async () =>
        stopList([
          stopA("AT_PICKUP", 4),
          { ...stopA("AT_PICKUP", 4), order_id: orderB, order_public_id: "ORD_B" },
        ]),
      onQueueChanged: vi.fn(),
      onAccessRevoked: vi.fn(),
      onOperationExpired: vi.fn(),
      now: () => new Date(clock.now),
      randomUuid: () => "99999999-9999-4999-8999-999999999999",
      upload: vi.fn().mockResolvedValue(undefined),
    });
  }

  function photo(orderId: string, idTail: number): DriverOfflineOperation {
    return operation("PICKUP_PROOF", orderId, 4, idTail);
  }

  it("waits the server's Retry-After for the whole queue, without a message or a rejected photo", async () => {
    const first = photo(orderA, 1);
    const second = photo(orderB, 2);
    const queue = new MemoryQueue([first, second]);
    queue.proofs.set(first.id, proofFor(first));
    queue.proofs.set(second.id, proofFor(second));
    const clock = { now: start };
    const createProofUploadSession = vi
      .fn()
      .mockRejectedValue(new DriverSyncApiError("rate-limited", null, 60_000));
    const scheduler = photoScheduler(queue, { createProofUploadSession }, clock);

    await scheduler.requestSync(true);

    // One 429, then the queue waits: the other order's photo is not tried.
    expect(createProofUploadSession).toHaveBeenCalledTimes(1);
    expect(queue.operations.find((value) => value.id === first.id)).toMatchObject({
      status: "RETRY_WAIT",
      attemptCount: 1,
      nextAttemptAt: "2026-07-26T12:01:30.000Z",
      safeError: null,
      rejectionCode: null,
    });
    expect(queue.operations.find((value) => value.id === second.id)).toMatchObject({
      status: "PENDING",
      attemptCount: 0,
    });
    expect(queue.proofs.has(first.id)).toBe(true);
    expect(queue.proofs.has(second.id)).toBe(true);

    // "Sincronizar ahora" during the wait sends nothing either.
    clock.now = start + 59_999;
    await scheduler.requestSync(true);
    expect(createProofUploadSession).toHaveBeenCalledTimes(1);

    // Once the wait is over the queue resumes by itself, with the same keys.
    clock.now = start + 60_000;
    await scheduler.requestSync(false);
    expect(createProofUploadSession).toHaveBeenCalledTimes(2);
    expect(createProofUploadSession.mock.calls[1][0]).toMatchObject({
      id: first.id,
      sessionIdempotencyKey: first.sessionIdempotencyKey,
    });
    await scheduler.dispose();
  });

  it("keeps the granted session and the uploaded photo when finalize answers 429", async () => {
    const queued = photo(orderA, 1);
    const queue = new MemoryQueue([queued]);
    queue.proofs.set(queued.id, proofFor(queued));
    const clock = { now: start };
    const createProofUploadSession = vi.fn().mockResolvedValue(grant());
    const finalizeProof = vi
      .fn()
      .mockRejectedValue(new DriverSyncApiError("rate-limited", null, 5_000));
    const scheduler = photoScheduler(queue, { createProofUploadSession, finalizeProof }, clock);

    await scheduler.requestSync(true);

    expect(queue.operations[0]).toMatchObject({
      status: "RETRY_WAIT",
      uploadSessionId: grant().id,
      uploadAccepted: true,
      nextAttemptAt: "2026-07-26T12:00:35.000Z",
      safeError: null,
    });
    expect(queue.proofs.has(queued.id)).toBe(true);

    clock.now = start + 5_000;
    await scheduler.requestSync(false);

    // The retry only confirms: no new session and no second upload.
    expect(createProofUploadSession).toHaveBeenCalledTimes(1);
    expect(finalizeProof).toHaveBeenCalledTimes(2);
    expect(finalizeProof.mock.calls[1][1]).toBe(grant().id);
    await scheduler.dispose();
  });

  it("waits the bounded backoff when the 429 does not say how long", async () => {
    const queued = photo(orderA, 1);
    const queue = new MemoryQueue([queued]);
    queue.proofs.set(queued.id, proofFor(queued));
    const scheduler = photoScheduler(
      queue,
      {
        createProofUploadSession: vi
          .fn()
          .mockRejectedValue(new DriverSyncApiError("rate-limited")),
      },
      { now: start },
    );

    await scheduler.requestSync(true);

    expect(queue.operations[0]).toMatchObject({
      status: "RETRY_WAIT",
      nextAttemptAt: new Date(start + DriverSyncBackoffMilliseconds[0]).toISOString(),
      safeError: null,
    });
    await scheduler.dispose();
  });
});

function expiryScheduler(
  queue: MemoryQueue,
  apiOverrides: Partial<DriverSyncApi>,
  now: Date,
  onOperationExpired: (value: DriverOfflineOperation) => void = vi.fn(),
  refreshStops: () => Promise<DriverStopList> = async () => stopList([
    stopA("ASSIGNED", 3),
    {
      order_id: orderB,
      aggregate_version: 8,
      order_public_id: "ORD_B",
      stop_type: "DELIVERY",
      status: "IN_TRANSIT",
      address_summary: "Zona B",
      details_available: true,
      contact_short_name: null,
      contact_full_name: null,
      zone_name: null,
      service_window: null,
      cod: null,
      navigation: syntheticNavigation,
    },
  ]),
): DriverSyncScheduler {
  return new DriverSyncScheduler({
    partition,
    queue,
    api: {
      transitionOrder: vi.fn(),
      createProofUploadSession: vi.fn(),
      finalizeProof: vi.fn(),
      recordCodCollection: vi.fn(),
      openFailedDeliveryIncident: vi.fn(),
      ...apiOverrides,
    },
    refreshStops,
    onQueueChanged: vi.fn(),
    onAccessRevoked: vi.fn(),
    onOperationExpired,
    now: () => now,
    randomUuid: () => "99999999-9999-4999-8999-999999999999",
    upload: vi.fn().mockResolvedValue(undefined),
  });
}

function stopA(
  status: DriverStop["status"],
  aggregateVersion: number,
): DriverStop {
  return {
    order_id: orderA,
    aggregate_version: aggregateVersion,
    order_public_id: "ORD_A",
    stop_type: "PICKUP",
    status,
    address_summary: "Zona A",
    details_available: true,
    contact_short_name: null,
    contact_full_name: null,
    zone_name: null,
    service_window: null,
    cod: null,
    navigation: syntheticNavigation,
  };
}

function proofFor(value: DriverOfflineOperation): DriverProofBlobRecord {
  return {
    schemaVersion: 1,
    partitionKey: partition.key,
    operationId: value.id,
    blob: new Blob([new Uint8Array([1])], { type: "image/png" }),
    contentType: "image/png",
    sizeBytes: 1,
    sha256: "a".repeat(64),
  };
}

function grant() {
  return {
    id: "44444444-4444-4444-8444-444444444444",
    status: "CREATED",
    uploadUrl: "https://objects.synthetic.test/quarantine",
    expiresAt: "2026-07-30T00:00:00.000Z",
    requiredHeaders: { "Content-Type": "image/png" },
  };
}

function schedulerFor(
  queue: MemoryQueue,
  apiOverrides: Partial<DriverSyncApi>,
  onAccessRevoked = vi.fn(),
  refreshStops: () => Promise<DriverStopList> = async () => stopList([
    {
      order_id: orderA,
      aggregate_version: 3,
      order_public_id: "ORD_A",
      stop_type: "PICKUP",
      status: "ASSIGNED",
      address_summary: "Zona A",
      details_available: true,
      contact_short_name: null,
      contact_full_name: null,
      zone_name: null,
      service_window: null,
      cod: null,
      navigation: syntheticNavigation,
    },
    {
      order_id: orderB,
      aggregate_version: 8,
      order_public_id: "ORD_B",
      stop_type: "DELIVERY",
      status: "IN_TRANSIT",
      address_summary: "Zona B",
      details_available: true,
      contact_short_name: null,
      contact_full_name: null,
      zone_name: null,
      service_window: null,
      cod: null,
      navigation: syntheticNavigation,
    },
  ]),
): DriverSyncScheduler {
  const api: DriverSyncApi = {
    transitionOrder: vi.fn(),
    createProofUploadSession: vi.fn(),
    finalizeProof: vi.fn(),
    recordCodCollection: vi.fn(),
    openFailedDeliveryIncident: vi.fn(),
    ...apiOverrides,
  };
  return new DriverSyncScheduler({
    partition,
    queue,
    api,
    refreshStops,
    onQueueChanged: vi.fn(),
    onAccessRevoked,
    now: () => new Date("2026-07-26T12:00:00.000Z"),
    randomUuid: () => "99999999-9999-4999-8999-999999999999",
  });
}

function codOperation(
  orderId: string,
  expectedVersion: number,
  idTail: number,
): DriverOfflineOperation {
  return createDriverOfflineOperation({
    partitionKey: partition.key,
    orderId,
    kind: "COD_COLLECTION",
    expectedVersion,
    cod: { amountCents: 125_000, reference: "ORD_A 2026-07-26T05:00:00-07:00" },
    now: () => new Date(`2026-07-26T12:00:0${idTail}.000Z`),
    randomUuid: () =>
      `00000000-0000-4000-8000-${idTail.toString().padStart(12, "0")}`,
  });
}

function operation(
  kind: Parameters<typeof createDriverOfflineOperation>[0]["kind"],
  orderId: string,
  expectedVersion: number,
  idTail: number,
): DriverOfflineOperation {
  return createDriverOfflineOperation({
    partitionKey: partition.key,
    orderId,
    kind,
    expectedVersion,
    proof: kind.endsWith("_PROOF")
      ? {
          contentType: "image/png",
          sizeBytes: 1,
          sha256: "a".repeat(64),
        }
      : undefined,
    now: () => new Date(`2026-07-26T12:00:0${idTail}.000Z`),
    randomUuid: () =>
      `00000000-0000-4000-8000-${idTail.toString().padStart(12, "0")}`,
  });
}

class MemoryQueue implements DriverOfflineQueue {
  public operations: DriverOfflineOperation[];
  public clearCount = 0;
  public leaseAvailable = true;
  /**
   * The partition lease as IndexedDbDriverOfflineQueue keeps it: one owner
   * and an expiry; another owner takes it only once it expired or was
   * released.
   */
  public lease: { ownerId: string; expiresAt: number } | null = null;
  public renewCount = 0;
  public releaseCount = 0;

  public constructor(operations: DriverOfflineOperation[]) {
    this.operations = [...operations];
  }

  public async listOperations(): Promise<readonly DriverOfflineOperation[]> {
    return this.operations;
  }

  public readonly proofs = new Map<string, DriverProofBlobRecord>();

  public async readProof(
    _partition: DriverCachePartition,
    operationId: string,
  ): Promise<DriverProofBlobRecord | null> {
    return this.proofs.get(operationId) ?? null;
  }

  public async enqueue(
    _partition: DriverCachePartition,
    operation: DriverOfflineOperation,
  ): Promise<void> {
    this.operations.push(operation);
  }

  public async replaceOperation(
    _partition: DriverCachePartition,
    operation: DriverOfflineOperation,
  ): Promise<void> {
    this.operations = this.operations.map((candidate) =>
      candidate.id === operation.id ? operation : candidate,
    );
  }

  public async replaceOperationAndDeleteProof(
    partitionValue: DriverCachePartition,
    operationValue: DriverOfflineOperation,
  ): Promise<void> {
    await this.replaceOperation(partitionValue, operationValue);
    this.proofs.delete(operationValue.id);
  }

  public async deleteOperation(
    _partition: DriverCachePartition,
    operationId: string,
  ): Promise<void> {
    this.operations = this.operations.filter(
      (candidate) => candidate.id !== operationId,
    );
    this.proofs.delete(operationId);
  }

  public async rebuildOperation(
    _partition: DriverCachePartition,
    replacedOperationId: string,
    operationValue: DriverOfflineOperation,
  ): Promise<void> {
    await this.deleteOperation(partition, replacedOperationId);
    this.operations.push(operationValue);
  }

  public async clearPartition(): Promise<void> {
    this.clearCount += 1;
    this.operations = [];
  }

  public async acquireLease(
    _partition: DriverCachePartition,
    ownerId: string,
    now: Date,
    durationMilliseconds: number,
  ): Promise<boolean> {
    if (!this.leaseAvailable) return false;
    if (
      this.lease !== null &&
      this.lease.ownerId !== ownerId &&
      this.lease.expiresAt > now.getTime()
    ) {
      return false;
    }
    this.lease = { ownerId, expiresAt: now.getTime() + durationMilliseconds };
    return true;
  }

  public async renewLease(
    _partition: DriverCachePartition,
    ownerId: string,
    now: Date,
    durationMilliseconds: number,
  ): Promise<boolean> {
    this.renewCount += 1;
    if (this.lease !== null && this.lease.ownerId !== ownerId) return false;
    this.lease = { ownerId, expiresAt: now.getTime() + durationMilliseconds };
    return true;
  }

  public async releaseLease(
    _partition: DriverCachePartition,
    ownerId: string,
  ): Promise<void> {
    this.releaseCount += 1;
    if (this.lease?.ownerId === ownerId) this.lease = null;
  }
}
