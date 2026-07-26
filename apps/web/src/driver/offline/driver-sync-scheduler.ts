import type { DriverStop } from "../contracts/driver-stop";
import type { DriverCachePartition } from "../cache/driver-stops-cache";
import type { DriverOfflineQueue } from "./driver-offline-queue";
import {
  DriverSyncApiError,
  uploadDriverProof,
  type DriverSyncApi,
  type DriverUploadGrant,
} from "./driver-sync-api";
import type {
  DriverOfflineOperation,
} from "./operation-contract";

export const DriverSyncBackoffMilliseconds = [
  1_000, 2_000, 5_000, 10_000, 30_000,
] as const;

export interface DriverSyncTelemetry {
  queueChanged(countBucket: string): void;
  syncPhaseChanged(phase: string): void;
  syncCompleted(kind: string): void;
  syncDeferred(category: string): void;
  conflictRaised(category: string): void;
  proofBytesQueued(sizeBucket: string): void;
}

export const disabledDriverSyncTelemetry: DriverSyncTelemetry = Object.freeze({
  queueChanged: () => undefined,
  syncPhaseChanged: () => undefined,
  syncCompleted: () => undefined,
  syncDeferred: () => undefined,
  conflictRaised: () => undefined,
  proofBytesQueued: () => undefined,
});

export interface DriverSyncSchedulerOptions {
  readonly partition: DriverCachePartition;
  readonly queue: DriverOfflineQueue;
  readonly api: DriverSyncApi;
  readonly refreshStops: (
    signal?: AbortSignal,
  ) => Promise<readonly DriverStop[]>;
  readonly onQueueChanged: (
    operations: readonly DriverOfflineOperation[],
  ) => void;
  readonly onAccessRevoked: (category: "unauthorized" | "forbidden") => void;
  readonly telemetry?: DriverSyncTelemetry;
  readonly now?: () => Date;
  readonly randomUuid?: () => string;
  readonly upload?: typeof uploadDriverProof;
  readonly leaseMilliseconds?: number;
  readonly window?: Pick<
    Window,
    "addEventListener" | "removeEventListener" | "navigator" | "document"
  >;
}

export class DriverSyncScheduler {
  private readonly telemetry: DriverSyncTelemetry;
  private readonly now: () => Date;
  private readonly ownerId: string;
  private readonly upload: typeof uploadDriverProof;
  private readonly leaseMilliseconds: number;
  private readonly browser: DriverSyncSchedulerOptions["window"];
  private active: Promise<void> | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private leaseTimer: ReturnType<typeof setTimeout> | null = null;
  private abortController: AbortController | null = null;
  private started = false;
  private leaseLost = false;
  private disposed = false;

  public constructor(private readonly options: DriverSyncSchedulerOptions) {
    this.telemetry = options.telemetry ?? disabledDriverSyncTelemetry;
    this.now = options.now ?? (() => new Date());
    this.ownerId = (options.randomUuid ?? (() => crypto.randomUUID()))();
    this.upload = options.upload ?? uploadDriverProof;
    this.leaseMilliseconds = options.leaseMilliseconds ?? 15_000;
    this.browser =
      options.window ??
      (typeof window === "undefined" ? undefined : window);
  }

  public start(): void {
    if (this.disposed || this.started) return;
    this.started = true;
    this.browser?.addEventListener("online", this.handleOnline);
    this.browser?.document.addEventListener(
      "visibilitychange",
      this.handleVisibility,
    );
    void this.requestSync(false);
  }

  public async requestSync(manual = true): Promise<void> {
    if (
      this.disposed ||
      !this.isOnline() ||
      !this.isVisible()
    ) {
      return;
    }
    if (this.active) return this.active;
    this.active = this.run(manual).finally(() => {
      this.active = null;
    });
    return this.active;
  }

  public async dispose(): Promise<void> {
    this.disposed = true;
    this.started = false;
    this.clearTimer();
    this.clearLeaseTimer();
    this.abortController?.abort();
    this.browser?.removeEventListener("online", this.handleOnline);
    this.browser?.document.removeEventListener(
      "visibilitychange",
      this.handleVisibility,
    );
    await this.active?.catch(() => undefined);
    await this.options.queue
      .releaseLease(this.options.partition, this.ownerId)
      .catch(() => undefined);
  }

  private readonly handleOnline = () => {
    void this.requestSync(false);
  };

  private readonly handleVisibility = () => {
    if (this.isVisible()) void this.requestSync(false);
    else this.clearTimer();
  };

  private async run(manual: boolean): Promise<void> {
    const acquired = await this.options.queue.acquireLease(
      this.options.partition,
      this.ownerId,
      this.now(),
      this.leaseMilliseconds,
    );
    if (!acquired) return;

    this.abortController = new AbortController();
    this.leaseLost = false;
    this.scheduleLeaseHeartbeat();
    try {
      while (
        !this.disposed &&
        !this.leaseLost &&
        this.isOnline() &&
        this.isVisible()
      ) {
        const operations = await this.options.queue.listOperations(
          this.options.partition,
        );
        this.publishQueue(operations);
        const operation = selectRunnableOperation(
          operations,
          this.now(),
          manual,
        );
        if (!operation) {
          this.scheduleNext(operations);
          return;
        }
        const renewed = await this.options.queue.renewLease(
          this.options.partition,
          this.ownerId,
          this.now(),
          this.leaseMilliseconds,
        );
        if (!renewed) return;
        await this.process(operation, this.abortController.signal);
        manual = false;
      }
    } finally {
      this.clearLeaseTimer();
      this.abortController = null;
      await this.options.queue
        .releaseLease(this.options.partition, this.ownerId)
        .catch(() => undefined);
    }
  }

  private async process(
    operation: DriverOfflineOperation,
    signal: AbortSignal,
  ): Promise<void> {
    let current = operation;
    let failurePhase: "proof" | "transition" = "transition";
    this.telemetry.syncPhaseChanged(current.status);
    try {
      const initialConfirmation = await this.confirmationByRest(current, signal);
      if (initialConfirmation !== "pending") {
        await this.options.queue.deleteOperation(
          this.options.partition,
          current.id,
        );
        if (initialConfirmation === "confirmed") {
          this.telemetry.syncCompleted(current.kind);
        } else {
          this.telemetry.conflictRaised("resource-unavailable");
        }
        return;
      }
      if (current.status === "AWAITING_REST_CONFIRMATION") {
        await this.deferRestConfirmation(current);
        return;
      }

      current = await this.persist(current, {
        status: "PROCESSING",
        safeError: null,
      });
      if (isProofOperation(current)) {
        try {
          current = await this.processProof(current, signal);
        } catch (error) {
          failurePhase = "proof";
          throw error;
        }
        if (current.status !== "PROCESSING") return;
      }
      const receipt = await this.options.api.transitionOrder(current, signal);
      if (
        receipt.id !== current.orderId ||
        receipt.status !== current.targetStatus ||
        receipt.version < current.expectedVersion + 1
      ) {
        throw new DriverSyncApiError("invalid-contract");
      }
      current = await this.persist(current, {
        status: "AWAITING_REST_CONFIRMATION",
      });
      const finalConfirmation = await this.confirmationByRest(current, signal);
      if (finalConfirmation !== "pending") {
        await this.options.queue.deleteOperation(
          this.options.partition,
          current.id,
        );
        if (finalConfirmation === "confirmed") {
          this.telemetry.syncCompleted(current.kind);
        } else {
          this.telemetry.conflictRaised("resource-unavailable");
        }
      } else {
        await this.deferRestConfirmation(current);
      }
    } catch (error) {
      if (error instanceof DriverOperationSyncError) {
        current = error.operation;
        error = error.inner;
      }
      await this.handleFailure(current, error, signal, failurePhase);
    }
  }

  private async processProof(
    operation: DriverOfflineOperation,
    signal: AbortSignal,
  ): Promise<DriverOfflineOperation> {
    let current = operation;
    try {
      const proof = await this.options.queue.readProof(
        this.options.partition,
        operation.id,
      );
      if (!proof && !operation.proofId) {
        return this.persist(operation, {
          status: "NEEDS_ATTENTION",
          safeError: "EVIDENCE_REJECTED",
        });
      }
      if (operation.proofId) return operation;
      if (!proof) throw new DriverSyncApiError("invalid-contract");
      if (
        proof.contentType !== current.contentType ||
        proof.sizeBytes !== current.sizeBytes ||
        proof.sha256 !== current.sha256 ||
        current.capturedAt !== current.clientOccurredAt
      ) {
        throw new DriverSyncApiError("invalid-contract");
      }

      let grant: DriverUploadGrant | null = null;
      if (current.uploadSessionExpiresAt) {
        if (Date.parse(current.uploadSessionExpiresAt) <= this.now().getTime()) {
          return this.persist(current, {
            status: "NEEDS_ATTENTION",
            safeError: "SESSION_EXPIRED",
          });
        }
      }

      if (!current.uploadSessionId) {
        grant = await this.options.api.createProofUploadSession(
          current,
          proof,
          signal,
        );
        if (Date.parse(grant.expiresAt) <= this.now().getTime()) {
          return this.persist(current, {
            status: "NEEDS_ATTENTION",
            safeError: "SESSION_EXPIRED",
          });
        }
        current = await this.persist(current, {
          uploadSessionId: grant.id,
          uploadSessionExpiresAt: grant.expiresAt,
        });
      }

      if (!current.uploadAccepted) {
        if (!grant) {
          // A signed grant is deliberately memory-only. After reload, replaying
          // the same create-session key safely returns the same grant.
          grant = await this.options.api.createProofUploadSession(
            current,
            proof,
            signal,
          );
          if (grant.id !== current.uploadSessionId) {
            throw new DriverSyncApiError("invalid-contract");
          }
        }
        await this.upload(grant, proof.blob, { signal });
        current = await this.persist(current, { uploadAccepted: true });
      }

      try {
        const receipt = await this.options.api.finalizeProof(
          current,
          current.uploadSessionId!,
          proof.sha256,
          signal,
        );
        if (
          receipt.sha256 !== proof.sha256 ||
          receipt.capturedAt !== current.clientOccurredAt
        ) {
          throw new DriverSyncApiError("invalid-contract");
        }
        const withProof = await this.persist(current, {
          proofId: receipt.id,
          status: "PROCESSING",
        });
        await this.options.queue.replaceOperationAndDeleteProof(
          this.options.partition,
          withProof,
        );
        return withProof;
      } catch (error) {
        if (
          error instanceof DriverSyncApiError &&
          error.category === "conflict" &&
          (error.publicCode === "UPLOAD_SESSION_NOT_READY" ||
            error.publicCode === "PROOF_OBJECT_NOT_READY")
        ) {
          const deferred = await this.defer(current, "WAITING_VALIDATION");
          this.telemetry.syncDeferred("proof-validation");
          return deferred;
        }
        throw error;
      }
    } catch (error) {
      throw new DriverOperationSyncError(current, error);
    }
  }

  private async handleFailure(
    operation: DriverOfflineOperation,
    error: unknown,
    signal: AbortSignal,
    phase: "proof" | "transition",
  ): Promise<void> {
    if (!(error instanceof DriverSyncApiError)) {
      await this.defer(operation, "RETRY_WAIT");
      return;
    }
    if (error.category === "cancelled" || signal.aborted) return;
    if (error.category === "unauthorized" || error.category === "forbidden") {
      await this.options.queue.clearPartition(this.options.partition);
      this.options.onAccessRevoked(error.category);
      this.abortController?.abort();
      return;
    }
    if (error.category === "not-found") {
      await this.options.queue.deleteOperation(
        this.options.partition,
        operation.id,
      );
      await this.options.refreshStops(signal).catch(() => []);
      this.telemetry.conflictRaised("resource-unavailable");
      return;
    }
    if (error.category === "recoverable") {
      await this.defer(operation, "RETRY_WAIT");
      this.telemetry.syncDeferred("network");
      return;
    }
    if (error.category === "session-expired") {
      await this.persist(operation, {
        status: "NEEDS_ATTENTION",
        safeError: "SESSION_EXPIRED",
      });
      this.telemetry.conflictRaised("session-expired");
      return;
    }
    await this.markAttentionAndBlock(
      operation,
      phase === "proof" ? "EVIDENCE_REJECTED" : "VERSION_CONFLICT",
    );
  }

  private async markAttentionAndBlock(
    operation: DriverOfflineOperation,
    safeError: "VERSION_CONFLICT" | "EVIDENCE_REJECTED",
  ): Promise<void> {
    const operations = await this.options.queue.listOperations(
      this.options.partition,
    );
    let found = false;
    for (const candidate of operations) {
      if (candidate.orderId !== operation.orderId) continue;
      if (candidate.id === operation.id) {
        found = true;
        await this.persist(candidate, {
          status: "NEEDS_ATTENTION",
          safeError,
        });
      } else if (found) {
        await this.persist(candidate, { status: "BLOCKED" });
      }
    }
    this.telemetry.conflictRaised("conflict");
  }

  private async confirmationByRest(
    operation: DriverOfflineOperation,
    signal: AbortSignal,
  ): Promise<"confirmed" | "missing" | "pending"> {
    const stops = await this.options.refreshStops(signal);
    const stop = stops.find((candidate) => candidate.order_id === operation.orderId);
    if (!stop) return "missing";
    return (
      stop.status === operation.targetStatus &&
      stop.aggregate_version >= operation.expectedVersion + 1
    ) ? "confirmed" : "pending";
  }

  private async defer(
    operation: DriverOfflineOperation,
    status: "RETRY_WAIT" | "WAITING_VALIDATION",
  ): Promise<DriverOfflineOperation> {
    const attemptCount = operation.attemptCount + 1;
    const delay =
      DriverSyncBackoffMilliseconds[
        Math.min(attemptCount - 1, DriverSyncBackoffMilliseconds.length - 1)
      ];
    return this.persist(operation, {
      status,
      attemptCount,
      nextAttemptAt: new Date(this.now().getTime() + delay).toISOString(),
      safeError: status === "RETRY_WAIT" ? "NETWORK" : null,
    });
  }

  private async deferRestConfirmation(
    operation: DriverOfflineOperation,
  ): Promise<DriverOfflineOperation> {
    const attemptCount = operation.attemptCount + 1;
    const delay =
      DriverSyncBackoffMilliseconds[
        Math.min(attemptCount - 1, DriverSyncBackoffMilliseconds.length - 1)
      ];
    this.telemetry.syncDeferred("rest-confirmation");
    return this.persist(operation, {
      status: "AWAITING_REST_CONFIRMATION",
      attemptCount,
      nextAttemptAt: new Date(this.now().getTime() + delay).toISOString(),
      safeError: null,
    });
  }

  private async persist(
    operation: DriverOfflineOperation,
    patch: Partial<DriverOfflineOperation>,
  ): Promise<DriverOfflineOperation> {
    const next = Object.freeze({ ...operation, ...patch });
    await this.options.queue.replaceOperation(this.options.partition, next);
    if (next.status !== operation.status) {
      this.telemetry.syncPhaseChanged(next.status);
    }
    return next;
  }

  private scheduleNext(operations: readonly DriverOfflineOperation[]): void {
    this.clearTimer();
    if (!this.isVisible() || !this.isOnline()) return;
    const timestamps = operations
      .filter(
        (operation) =>
          operation.status === "RETRY_WAIT" ||
          operation.status === "WAITING_VALIDATION" ||
          operation.status === "AWAITING_REST_CONFIRMATION",
      )
      .map((operation) => Date.parse(operation.nextAttemptAt))
      .filter(Number.isFinite);
    if (timestamps.length === 0) return;
    const delay = Math.max(0, Math.min(...timestamps) - this.now().getTime());
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.requestSync(false);
    }, delay);
  }

  private publishQueue(
    operations: readonly DriverOfflineOperation[],
  ): void {
    this.options.onQueueChanged(operations);
    this.telemetry.queueChanged(countBucket(operations.length));
  }

  private clearTimer(): void {
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }

  private scheduleLeaseHeartbeat(): void {
    this.clearLeaseTimer();
    const delay = Math.max(1_000, Math.floor(this.leaseMilliseconds / 3));
    this.leaseTimer = setTimeout(() => {
      this.leaseTimer = null;
      void this.options.queue
        .renewLease(
          this.options.partition,
          this.ownerId,
          this.now(),
          this.leaseMilliseconds,
        )
        .then((renewed) => {
          if (!renewed) {
            this.leaseLost = true;
            this.abortController?.abort();
            return;
          }
          if (!this.disposed && this.active) this.scheduleLeaseHeartbeat();
        })
        .catch(() => {
          this.leaseLost = true;
          this.abortController?.abort();
        });
    }, delay);
  }

  private clearLeaseTimer(): void {
    if (this.leaseTimer !== null) {
      clearTimeout(this.leaseTimer);
      this.leaseTimer = null;
    }
  }

  private isOnline(): boolean {
    return this.browser?.navigator.onLine ?? true;
  }

  private isVisible(): boolean {
    return this.browser?.document.visibilityState !== "hidden";
  }
}

export function selectRunnableOperation(
  operations: readonly DriverOfflineOperation[],
  now: Date,
  manual: boolean,
): DriverOfflineOperation | null {
  const blockedOrders = new Set<string>();
  for (const operation of [...operations].sort(compareOperations)) {
    if (blockedOrders.has(operation.orderId)) continue;
    if (
      operation.status === "NEEDS_ATTENTION" ||
      operation.status === "BLOCKED"
    ) {
      blockedOrders.add(operation.orderId);
      continue;
    }
    if (
      !manual &&
      (operation.status === "RETRY_WAIT" ||
        operation.status === "WAITING_VALIDATION" ||
        operation.status === "AWAITING_REST_CONFIRMATION") &&
      Date.parse(operation.nextAttemptAt) > now.getTime()
    ) {
      blockedOrders.add(operation.orderId);
      continue;
    }
    return operation;
  }
  return null;
}

function isProofOperation(operation: DriverOfflineOperation): boolean {
  return (
    operation.kind === "PICKUP_PROOF" ||
    operation.kind === "DELIVERY_PROOF"
  );
}

function compareOperations(
  left: DriverOfflineOperation,
  right: DriverOfflineOperation,
): number {
  return (
    left.createdAt.localeCompare(right.createdAt) ||
    left.id.localeCompare(right.id)
  );
}

function countBucket(count: number): string {
  if (count <= 0) return "0";
  if (count === 1) return "1";
  if (count <= 5) return "2-5";
  if (count <= 20) return "6-20";
  return "21+";
}

export function driverProofSizeBucket(sizeBytes: number): string {
  if (sizeBytes <= 256 * 1024) return "0-256KB";
  if (sizeBytes <= 1024 * 1024) return "256KB-1MB";
  if (sizeBytes <= 5 * 1024 * 1024) return "1-5MB";
  return "5-10MB";
}

class DriverOperationSyncError extends Error {
  public constructor(
    public readonly operation: DriverOfflineOperation,
    public readonly inner: unknown,
  ) {
    super("driver_operation_sync_failed");
    this.name = "DriverOperationSyncError";
  }
}
