export type RefreshRequirement = "normal" | "mandatory-reconnect";

export class RefreshScopeChangedError extends Error {
  public constructor() {
    super("The authoritative refresh scope changed.");
    this.name = "RefreshScopeChangedError";
  }
}

export interface AuthoritativeRefreshOperation<TResult> {
  readonly requirement: RefreshRequirement;
  readonly isCurrent: () => boolean;
  readonly execute: (signal: AbortSignal) => Promise<TResult>;
  readonly apply: (result: TResult) => void;
}

interface WaitingRefresh<TResult> {
  operation: AuthoritativeRefreshOperation<TResult>;
  readonly waiters: Array<{
    resolve(result: TResult): void;
    reject(error: unknown): void;
  }>;
}

export class AuthoritativeRefreshCoordinator<TResult> {
  private activeController: AbortController | null = null;
  private normalPending: WaitingRefresh<TResult> | null = null;
  private readonly mandatoryPending: WaitingRefresh<TResult>[] = [];

  public request(
    operation: AuthoritativeRefreshOperation<TResult>,
  ): Promise<TResult> {
    const promise = new Promise<TResult>((resolve, reject) => {
      const waiter = { resolve, reject };
      if (operation.requirement === "mandatory-reconnect") {
        this.mandatoryPending.push({ operation, waiters: [waiter] });
      } else if (this.activeController === null && this.normalPending === null) {
        this.normalPending = { operation, waiters: [waiter] };
      } else if (this.normalPending === null) {
        this.normalPending = { operation, waiters: [waiter] };
      } else {
        this.normalPending.operation = operation;
        this.normalPending.waiters.push(waiter);
      }
    });

    void this.drain();
    return promise;
  }

  public cancel(): void {
    const error = new RefreshScopeChangedError();
    this.activeController?.abort(error);
    this.rejectPending(this.normalPending, error);
    this.normalPending = null;
    for (const pending of this.mandatoryPending.splice(0)) {
      this.rejectPending(pending, error);
    }
  }

  private async drain(): Promise<void> {
    if (this.activeController !== null) return;
    const pending =
      this.mandatoryPending.shift() ?? this.takeNormalPending();
    if (pending === null || pending === undefined) return;

    const controller = new AbortController();
    this.activeController = controller;
    try {
      if (!pending.operation.isCurrent()) throw new RefreshScopeChangedError();
      const result = await pending.operation.execute(controller.signal);
      if (!pending.operation.isCurrent()) throw new RefreshScopeChangedError();
      pending.operation.apply(result);
      for (const waiter of pending.waiters) waiter.resolve(result);
    } catch (error: unknown) {
      for (const waiter of pending.waiters) waiter.reject(error);
    } finally {
      if (this.activeController === controller) this.activeController = null;
      void this.drain();
    }
  }

  private takeNormalPending(): WaitingRefresh<TResult> | null {
    const pending = this.normalPending;
    this.normalPending = null;
    return pending;
  }

  private rejectPending(
    pending: WaitingRefresh<TResult> | null,
    error: unknown,
  ): void {
    if (pending === null) return;
    for (const waiter of pending.waiters) waiter.reject(error);
  }
}
