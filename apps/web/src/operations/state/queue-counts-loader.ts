import type { OperationsQueueCounts } from "../contracts/queue-counts";

/**
 * UI-PHASE2-QUEUE-COUNTS-2026-10-05: what the dashboard shows for the server
 * counts. `counts` is null until the first answer and after a failure;
 * `unavailable` tells a failure apart from a pending first load.
 */
export interface QueueCountsView {
  readonly counts: OperationsQueueCounts | null;
  readonly unavailable: boolean;
}

export const emptyQueueCountsView: QueueCountsView = { counts: null, unavailable: false };

/**
 * Latest-wins loader: each load aborts the previous one and only the newest
 * answer is applied, so a slow response never overwrites a newer one. A
 * failed load clears the counts instead of leaving stale numbers on screen.
 */
export class QueueCountsLoader {
  private generation = 0;
  private controller: AbortController | null = null;

  public constructor(private readonly publish: (view: QueueCountsView) => void) {}

  public async load(fetchCounts: (signal: AbortSignal) => Promise<OperationsQueueCounts>): Promise<void> {
    const generation = ++this.generation;
    this.controller?.abort();
    const controller = new AbortController();
    this.controller = controller;
    try {
      const counts = await fetchCounts(controller.signal);
      if (generation === this.generation) this.publish({ counts, unavailable: false });
    } catch {
      if (generation === this.generation && !controller.signal.aborted)
        this.publish({ counts: null, unavailable: true });
    } finally {
      if (this.controller === controller) this.controller = null;
    }
  }

  /** Session change or lost access: drop any pending answer and clear the counts. */
  public reset(): void {
    this.generation += 1;
    this.controller?.abort();
    this.controller = null;
    this.publish(emptyQueueCountsView);
  }
}
