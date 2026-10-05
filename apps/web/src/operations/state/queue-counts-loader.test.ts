import { describe, expect, it } from "vitest";
import type { OperationsQueueCounts } from "../contracts/queue-counts";
import { QueueCountsLoader, type QueueCountsView } from "./queue-counts-loader";

function counts(total: number): OperationsQueueCounts {
  return {
    generated_at: "2026-10-05T18:00:00Z",
    total,
    by_status: {
      DRAFT: total,
      CONFIRMED: 0,
      READY_FOR_PICKUP: 0,
      ASSIGNED: 0,
      AT_PICKUP: 0,
      PICKED_UP: 0,
      IN_TRANSIT: 0,
      DELIVERING: 0,
      FAILED_ATTEMPT: 0,
      RESCHEDULED: 0,
      RETURNING: 0,
      RETURNED: 0,
      DELIVERED: 0,
      CLOSED: 0,
      CLAIM_OPEN: 0,
      CLAIM_RESOLVED: 0,
      CANCELLED: 0,
    },
    queues: { unassigned: 0, needs_attention: 0, price_review: 0, delivered_not_closed: 0, en_route: 0 },
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

describe("queue counts loader (UI-PHASE2-QUEUE-COUNTS-2026-10-05)", () => {
  it("publishes the counts of a load", async () => {
    const views: QueueCountsView[] = [];
    const loader = new QueueCountsLoader((view) => views.push(view));
    await loader.load(() => Promise.resolve(counts(4)));
    expect(views).toEqual([{ counts: counts(4), unavailable: false }]);
  });

  it("applies only the newest answer and aborts the older request", async () => {
    const views: QueueCountsView[] = [];
    const loader = new QueueCountsLoader((view) => views.push(view));
    const first = deferred<OperationsQueueCounts>();
    let firstSignal: AbortSignal | undefined;
    const older = loader.load((signal) => {
      firstSignal = signal;
      return first.promise;
    });
    const newer = loader.load(() => Promise.resolve(counts(2)));
    await newer;
    expect(firstSignal?.aborted).toBe(true);
    first.resolve(counts(9));
    await older;
    expect(views).toEqual([{ counts: counts(2), unavailable: false }]);
  });

  it("clears the counts when the newest load fails", async () => {
    const views: QueueCountsView[] = [];
    const loader = new QueueCountsLoader((view) => views.push(view));
    await loader.load(() => Promise.resolve(counts(3)));
    await loader.load(() => Promise.reject(new Error("503")));
    expect(views.at(-1)).toEqual({ counts: null, unavailable: true });
  });

  it("ignores a failure of a superseded load", async () => {
    const views: QueueCountsView[] = [];
    const loader = new QueueCountsLoader((view) => views.push(view));
    const first = deferred<OperationsQueueCounts>();
    const older = loader.load(() => first.promise);
    await loader.load(() => Promise.resolve(counts(1)));
    first.reject(new Error("aborted"));
    await older;
    expect(views).toEqual([{ counts: counts(1), unavailable: false }]);
  });

  it("reset drops a pending answer and clears the counts", async () => {
    const views: QueueCountsView[] = [];
    const loader = new QueueCountsLoader((view) => views.push(view));
    const pending = deferred<OperationsQueueCounts>();
    let signal: AbortSignal | undefined;
    const load = loader.load((value) => {
      signal = value;
      return pending.promise;
    });
    loader.reset();
    expect(signal?.aborted).toBe(true);
    pending.resolve(counts(5));
    await load;
    expect(views).toEqual([{ counts: null, unavailable: false }]);
  });
});
