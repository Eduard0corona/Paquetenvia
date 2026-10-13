import type { DriverStop, DriverStopList } from "../contracts/driver-stop";
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
  DriverOperationSafeError,
} from "./operation-contract";
import { driverFailedReportCashHold } from "./failed-delivery-policy";
import {
  isOfflineOperationExpired,
  isOfflineOperationExpiredCode,
} from "./offline-operation-age";
import {
  compareDriverOperationDependency,
  driverDeliveryAwaitsCash,
  driverOperationConfirmedByStop,
  orderDriverOperations,
} from "./operation-projection";
import {
  codRejectionCodeToKeep,
  incidentRejectionCodeToKeep,
  transitionRejectionCodeToKeep,
  type DriverRejectionCode,
} from "./rejection-codes";

/** The request a queued action was making when it failed. */
type DriverSyncFailurePhase = "proof" | "incident" | "transition" | "cod";

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
  /**
   * The read that confirms queued actions (UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11):
   * the driver's stops without their protected details, so syncing never
   * depends on the key service. When it cannot answer (logout, aborted pass)
   * it throws; it never answers an empty list in place of the real one.
   */
  readonly refreshStops: (signal?: AbortSignal) => Promise<DriverStopList>;
  readonly onQueueChanged: (
    operations: readonly DriverOfflineOperation[],
  ) => void;
  readonly onAccessRevoked: (category: "unauthorized" | "forbidden") => void;
  /**
   * Called once for every operation removed because it is older than the
   * OPS-003 maximum age. The operation is already gone from the queue and
   * will never be sent again; the caller only tells the driver.
   */
  readonly onOperationExpired?: (operation: DriverOfflineOperation) => void;
  /**
   * Called when a sync pass (manual, `online`, visibility, backoff timer or
   * lease retry) holds the lease and starts, so expiry notices can be grouped
   * per pass.
   */
  readonly onSyncPassStarted?: () => void;
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
  /** Pauses in force (see pause); no pass runs while it is above zero. */
  private pauseCount = 0;
  /**
   * SEC-004 (owner answer Q6, "la cola espera lo que indique el servidor y
   * reintenta sin mensaje"): until when, in epoch milliseconds, the server
   * asked the queue to wait after a 429 (its Retry-After). The limits are per
   * driver, so no action is sent before then, not even on "Sincronizar
   * ahora"; the queue then resumes by itself. Memory only: after a reload the
   * limited action still waits its own nextAttemptAt.
   */
  private rateLimitedUntil = 0;
  /**
   * Orders whose delivery waits for its cash in the current pass
   * (driverDeliveryAwaitsCash): it is not tried again until a collection for
   * the order runs or the next pass starts.
   */
  private readonly waitingForCash = new Set<string>();

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
      this.pauseCount > 0 ||
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

  /**
   * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: stops syncing until resume.
   * The running pass is aborted and awaited; an action in flight stays as it
   * was last persisted and is resumed later with the same keys. The controller
   * pauses around a discard, so an action the driver discards (for example a
   * saved collection, which may still be pending) is neither sent nor written
   * back by a pass that was already running.
   */
  public async pause(): Promise<void> {
    this.pauseCount += 1;
    this.clearTimer();
    this.abortController?.abort();
    await this.active?.catch(() => undefined);
  }

  /** Ends one pause; the last one lets the next sync request run. */
  public resume(): void {
    if (this.pauseCount > 0) this.pauseCount -= 1;
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
    if (!acquired) {
      this.scheduleLeaseRetry();
      return;
    }
    this.options.onSyncPassStarted?.();

    // Logout, a lost lease or a confirmation read that could not answer
    // aborts the pass; nothing destructive runs after that.
    const pass = new AbortController();
    this.abortController = pass;
    this.leaseLost = false;
    this.waitingForCash.clear();
    this.scheduleLeaseHeartbeat();
    try {
      while (
        !this.disposed &&
        this.pauseCount === 0 &&
        !this.leaseLost &&
        !pass.signal.aborted &&
        this.isOnline() &&
        this.isVisible()
      ) {
        const operations = await this.options.queue.listOperations(
          this.options.partition,
        );
        if (pass.signal.aborted) return;
        this.publishQueue(operations);
        // An action held for the driver's attention is never sent on its own,
        // but it still may not outlive the OPS-003 window: once it could no
        // longer be retried it is discarded and the driver is told.
        const stale = operations.find(
          (candidate) =>
            (candidate.status === "NEEDS_ATTENTION" ||
              candidate.status === "BLOCKED") &&
            isOfflineOperationExpired(candidate.clientOccurredAt, this.now()),
        );
        if (stale) {
          await this.expire(stale, pass.signal);
          continue;
        }
        // UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: an action the server
        // already accepted is only waiting for REST to show it. When REST
        // cannot show its stop (for example a stop left out of the answer for
        // breaking the contract) it would be polled forever, so past the same
        // window it leaves the queue. Nothing is lost: the server has it.
        const unconfirmed = operations.find(
          (candidate) =>
            candidate.status === "AWAITING_REST_CONFIRMATION" &&
            isOfflineOperationExpired(candidate.clientOccurredAt, this.now()),
        );
        if (unconfirmed) {
          await this.options.queue.deleteOperation(
            this.options.partition,
            unconfirmed.id,
          );
          this.telemetry.conflictRaised("rest-confirmation-expired");
          continue;
        }
        // SEC-004: while the server's wait lasts nothing is sent; the timer
        // resumes the queue when it ends.
        if (this.now().getTime() < this.rateLimitedUntil) {
          this.scheduleNext(operations);
          return;
        }
        const operation = selectRunnableOperation(
          operations,
          this.now(),
          manual,
          this.waitingForCash,
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
        await this.process(operation, operations, pass.signal);
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
    operations: readonly DriverOfflineOperation[],
    signal: AbortSignal,
  ): Promise<void> {
    let current = operation;
    let failurePhase: DriverSyncFailurePhase =
      operation.kind === "COD_COLLECTION" ? "cod" : "transition";
    // A collection of an order lets its waiting delivery be tried again.
    if (current.kind === "COD_COLLECTION") {
      this.waitingForCash.delete(current.orderId);
    }
    this.telemetry.syncPhaseChanged(current.status);
    try {
      const initial = await this.confirmationByRest(current, signal);
      if (initial.outcome !== "pending") {
        if (signal.aborted) return;
        await this.options.queue.deleteOperation(
          this.options.partition,
          current.id,
        );
        if (initial.outcome === "confirmed") {
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
      // REST has just shown the action was not applied. Past the OPS-003
      // window the server would only answer OFFLINE_OPERATION_EXPIRED, so the
      // queue discards it now instead of sending it.
      if (isOfflineOperationExpired(current.clientOccurredAt, this.now())) {
        await this.expire(current, signal);
        return;
      }
      // UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: a delivery whose cash is
      // still to collect, with no collection saved for it, is not sent; it
      // waits for "Recibí" and then goes right after the collection.
      if (driverDeliveryAwaitsCash(current, initial.stop, operations)) {
        this.waitingForCash.add(current.orderId);
        this.telemetry.syncDeferred("cod-required");
        return;
      }
      // UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: the cash rules need the
      // stop. When REST answered without it (left out for breaking the
      // contract), a collection or a report is not sent: it waits and is read
      // again; it cannot outlive the OPS-003 window.
      if (
        (current.kind === "COD_COLLECTION" ||
          current.kind === "FAILED_DELIVERY") &&
        initial.stop === null
      ) {
        if (signal.aborted) return;
        await this.deferForMissingStop(current);
        return;
      }
      // Cash is recorded only at the door. A collection whose stop REST shows
      // outside DELIVERING (for example after a failed attempt) is held, never
      // sent: the server would still accept it in FAILED_ATTEMPT.
      if (
        current.kind === "COD_COLLECTION" &&
        initial.stop !== null &&
        initial.stop.status !== "DELIVERING"
      ) {
        if (signal.aborted) return;
        await this.markAttentionAndBlock(current, "VERSION_CONFLICT");
        return;
      }
      // A report goes only to the attempt it describes, and (owner answer "No,
      // llama a despacho (Recommended)") never while its order has a
      // collection saved on the phone or recorded on the server.
      if (
        current.kind === "FAILED_DELIVERY" &&
        initial.stop !== null &&
        (await this.holdFailedReport(current, initial.stop, operations, signal))
      ) {
        return;
      }

      current = await this.persist(current, {
        status: "PROCESSING",
        safeError: null,
        rejectionCode: null,
      });
      if (current.kind === "COD_COLLECTION") {
        // UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: the cash is recorded with
        // its own key; FIFO per order keeps it ahead of the delivery.
        const receipt = await this.options.api.recordCodCollection(current, signal);
        if (
          receipt.orderId !== current.orderId ||
          receipt.amountCents !== current.cod?.amountCents ||
          (receipt.status !== "RECORDED" && receipt.status !== "RECONCILED")
        ) {
          throw new DriverSyncApiError("invalid-contract");
        }
      } else {
        // Whether a request went out since the cash was last checked.
        let sentSinceCashCheck = false;
        if (isProofOperation(current)) {
          sentSinceCashCheck = current.proofId === null;
          try {
            current = await this.processProof(current, signal);
          } catch (error) {
            failurePhase = "proof";
            throw error;
          }
          if (current.status !== "PROCESSING") return;
        }
        if (current.kind === "FAILED_DELIVERY") {
          // UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: steps 4 and 5 of the
          // AI-05 sequence. The stop is read again right before each step that
          // follows a request, so a collection recorded meanwhile (for example
          // by dispatch) or an attempt dispatch already moved on still holds
          // the report before its incident is opened.
          if (
            sentSinceCashCheck &&
            (await this.recheckFailedReport(current, signal))
          ) {
            return;
          }
          let report = current.failedDelivery;
          if (report === null) throw new DriverSyncApiError("invalid-contract");
          if (report.incidentId === null) {
            failurePhase = "incident";
            if (!report.incidentRequested) {
              // From here on the incident may exist even if its answer is
              // lost; a held report then says so instead of "not sent".
              report = Object.freeze({ ...report, incidentRequested: true });
              current = await this.persist(current, { failedDelivery: report });
            }
            const incident = await this.options.api.openFailedDeliveryIncident(
              current,
              signal,
            );
            if (
              incident.orderId !== current.orderId ||
              incident.reasonCode !== report.reasonCode ||
              incident.nextAction !== null ||
              incident.occurredAt !== current.clientOccurredAt ||
              incident.evidenceProofIds.length !== 1 ||
              incident.evidenceProofIds[0] !== current.proofId
            ) {
              throw new DriverSyncApiError("invalid-contract");
            }
            current = await this.persist(current, {
              failedDelivery: Object.freeze({
                ...report,
                incidentId: incident.id,
              }),
            });
            failurePhase = "transition";
            if (await this.recheckFailedReport(current, signal)) return;
          }
        }
        const receipt = await this.options.api.transitionOrder(current, signal);
        if (
          receipt.id !== current.orderId ||
          receipt.status !== current.targetStatus ||
          receipt.version < current.expectedVersion + 1
        ) {
          throw new DriverSyncApiError("invalid-contract");
        }
      }
      current = await this.persist(current, {
        status: "AWAITING_REST_CONFIRMATION",
      });
      const final = await this.confirmationByRest(current, signal);
      if (final.outcome !== "pending") {
        if (signal.aborted) return;
        await this.options.queue.deleteOperation(
          this.options.partition,
          current.id,
        );
        if (final.outcome === "confirmed") {
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

  /**
   * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: holds a failed-delivery
   * report for the driver's attention, unsent, and resolves whether it did.
   *
   * - Until its incident exists, a report goes only to the attempt it was made
   *   at: REST must show the stop DELIVERING at exactly the version the report
   *   was built on. Otherwise (dispatch registered the failed attempt itself,
   *   or moved the order on, for example "Reintentar hoy") nothing is sent, no
   *   photo is uploaded and no incident is opened (VERSION_CONFLICT). After
   *   the incident exists the transition is replayed as it is: its stored
   *   answer, if the server applied it, is what tells the phone.
   * - Owner answer "No, llama a despacho (Recommended)": never while REST
   *   reports the order's cash recorded or the queue holds a collection of the
   *   same order (COD_COLLECTED).
   */
  private async holdFailedReport(
    report: DriverOfflineOperation,
    stop: DriverStop,
    operations: readonly DriverOfflineOperation[],
    signal: AbortSignal,
  ): Promise<boolean> {
    const atItsAttempt =
      report.failedDelivery?.incidentId != null ||
      (stop.status === "DELIVERING" &&
        stop.aggregate_version === report.expectedVersion);
    const held = !atItsAttempt
      ? "VERSION_CONFLICT"
      : driverFailedReportCashHold(report, stop, operations) !== null
        ? "COD_COLLECTED"
        : null;
    if (held === null) return false;
    if (signal.aborted) return true;
    await this.markAttentionAndBlock(report, held);
    return true;
  }

  /**
   * The same checks with a fresh REST read and the queue as it is now. A stop
   * REST no longer shows sends nothing; the report waits for the next read.
   */
  private async recheckFailedReport(
    report: DriverOfflineOperation,
    signal: AbortSignal,
  ): Promise<boolean> {
    const { outcome, stop } = await this.confirmationByRest(report, signal);
    if (signal.aborted) return true;
    // REST already shows the failed attempt behind this phone's incident: the
    // next look at the operation removes it, nothing more is sent.
    if (outcome === "confirmed") return true;
    if (stop === null) {
      await this.deferForMissingStop(report);
      return true;
    }
    const operations = await this.options.queue.listOperations(
      this.options.partition,
    );
    return this.holdFailedReport(report, stop, operations, signal);
  }

  /**
   * A collection or a report whose stop REST did not show: it is read again
   * after the backoff and is never sent meanwhile.
   */
  private async deferForMissingStop(
    operation: DriverOfflineOperation,
  ): Promise<void> {
    const attemptCount = operation.attemptCount + 1;
    const delay =
      DriverSyncBackoffMilliseconds[
        Math.min(attemptCount - 1, DriverSyncBackoffMilliseconds.length - 1)
      ];
    await this.persist(operation, {
      status: "RETRY_WAIT",
      attemptCount,
      nextAttemptAt: new Date(this.now().getTime() + delay).toISOString(),
      safeError: "RESOURCE_UNAVAILABLE",
    });
    this.telemetry.syncDeferred("stop-unavailable");
  }

  private async handleFailure(
    operation: DriverOfflineOperation,
    error: unknown,
    signal: AbortSignal,
    phase: DriverSyncFailurePhase,
  ): Promise<void> {
    if (signal.aborted) return;
    // The server already accepted this action; only REST has not shown it yet
    // (for example the read right after the 2xx failed). It keeps waiting for
    // REST with backoff: it is never sent again, held as a conflict or expired
    // as unsent (OPS-003 would tell the driver to register it again).
    const accepted = operation.status === "AWAITING_REST_CONFIRMATION";
    if (!(error instanceof DriverSyncApiError)) {
      if (accepted) await this.deferRestConfirmation(operation);
      else await this.defer(operation, "RETRY_WAIT");
      return;
    }
    if (error.category === "cancelled") {
      // The confirmation read could not answer (for example the stops were
      // closed by a logout): the action stays as it is and the pass ends.
      this.abortController?.abort();
      return;
    }
    if (error.category === "unauthorized" || error.category === "forbidden") {
      await this.options.queue.clearPartition(this.options.partition);
      this.options.onAccessRevoked(error.category);
      this.abortController?.abort();
      return;
    }
    if (accepted) {
      await this.deferRestConfirmation(operation);
      return;
    }
    if (error.category === "not-found") {
      await this.options.queue.deleteOperation(
        this.options.partition,
        operation.id,
      );
      await this.options.refreshStops(signal).catch(() => undefined);
      this.telemetry.conflictRaised("resource-unavailable");
      return;
    }
    if (error.category === "rate-limited") {
      await this.waitForRateLimit(operation, error.retryAfterMilliseconds);
      return;
    }
    if (error.category === "recoverable") {
      await this.defer(operation, "RETRY_WAIT");
      this.telemetry.syncDeferred("network");
      return;
    }
    if (
      error.category === "conflict" &&
      isOfflineOperationExpiredCode(error.publicCode)
    ) {
      await this.expire(operation, signal);
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
    if (
      phase === "cod" &&
      error.category === "conflict" &&
      error.publicCode === "COD_ALREADY_RECORDED" &&
      (await this.confirmationByRest(operation, signal).catch(() => null))
        ?.outcome === "confirmed"
    ) {
      // Someone (for example dispatch) already recorded exactly this cash:
      // the collection is satisfied, so it leaves the queue and the delivery
      // behind it may proceed.
      if (signal.aborted) return;
      await this.options.queue.deleteOperation(
        this.options.partition,
        operation.id,
      );
      this.telemetry.syncCompleted(operation.kind);
      return;
    }
    if (
      phase === "transition" &&
      error.category === "conflict" &&
      error.publicCode === "COD_NOT_RECORDED" &&
      (await this.awaitsCash(operation, signal))
    ) {
      // The server refused the delivery because the cash is not recorded and
      // no collection is saved for it: the delivery waits for "Recibí"
      // instead of asking the driver to resolve a conflict.
      if (signal.aborted) return;
      await this.persist(operation, {
        status: "PENDING",
        safeError: null,
        rejectionCode: null,
      });
      this.waitingForCash.add(operation.orderId);
      this.telemetry.syncDeferred("cod-required");
      return;
    }
    const rejectionCode =
      error.category !== "conflict"
        ? null
        : phase === "cod"
          ? codRejectionCodeToKeep(error.publicCode)
          : phase === "transition"
            ? transitionRejectionCodeToKeep(error.publicCode)
            : phase === "incident"
              ? incidentRejectionCodeToKeep(error.publicCode)
              : null;
    await this.markAttentionAndBlock(
      operation,
      phase === "proof" || rejectionCode === "EVIDENCE_NOT_AVAILABLE"
        ? "EVIDENCE_REJECTED"
        : "VERSION_CONFLICT",
      rejectionCode,
    );
  }

  /**
   * OPS-003-OFFLINE-72H: an expired operation is removed permanently, with its
   * local evidence, and is never retried. Later operations for the same order
   * were built on top of it, so they can no longer apply: each one that is
   * itself expired is removed the same way, and the first remaining one is
   * held for the driver's attention with the rest blocked behind it.
   */
  private async expire(
    operation: DriverOfflineOperation,
    signal: AbortSignal,
  ): Promise<void> {
    if (signal.aborted) return;
    await this.options.queue.deleteOperation(
      this.options.partition,
      operation.id,
    );
    this.telemetry.conflictRaised("offline-operation-expired");
    this.options.onOperationExpired?.(operation);

    const dependents = (
      await this.options.queue.listOperations(this.options.partition)
    )
      .filter(
        (candidate) =>
          candidate.orderId === operation.orderId &&
          compareDriverOperationDependency(candidate, operation) > 0,
      )
      .sort(compareDriverOperationDependency);
    let held = false;
    for (const candidate of dependents) {
      if (signal.aborted) return;
      // Accepted by the server: it only waits for REST (see run).
      if (candidate.status === "AWAITING_REST_CONFIRMATION") continue;
      if (isOfflineOperationExpired(candidate.clientOccurredAt, this.now())) {
        await this.options.queue.deleteOperation(
          this.options.partition,
          candidate.id,
        );
        this.telemetry.conflictRaised("offline-operation-expired");
        this.options.onOperationExpired?.(candidate);
      } else if (!held) {
        held = true;
        await this.persist(candidate, {
          status: "NEEDS_ATTENTION",
          safeError: "VERSION_CONFLICT",
        });
      } else {
        await this.persist(candidate, { status: "BLOCKED" });
      }
    }
  }

  private async markAttentionAndBlock(
    operation: DriverOfflineOperation,
    safeError: Extract<
      DriverOperationSafeError,
      "VERSION_CONFLICT" | "EVIDENCE_REJECTED" | "COD_COLLECTED"
    >,
    rejectionCode: DriverRejectionCode | null = null,
  ): Promise<void> {
    const operations = orderDriverOperations(
      await this.options.queue.listOperations(this.options.partition),
    );
    let found = false;
    for (const candidate of operations) {
      if (candidate.orderId !== operation.orderId) continue;
      if (candidate.id === operation.id) {
        found = true;
        await this.persist(candidate, {
          status: "NEEDS_ATTENTION",
          safeError,
          rejectionCode,
        });
      } else if (
        found &&
        // An action the server already accepted only waits for REST; it is
        // never turned back into one to send.
        candidate.status !== "AWAITING_REST_CONFIRMATION"
      ) {
        await this.persist(candidate, { status: "BLOCKED" });
      }
    }
    this.telemetry.conflictRaised("conflict");
  }

  /**
   * What REST says about a queued action, with the stop it read. A stop the
   * answer does not hold is "missing" only when the app accepted every stop
   * of the answer; when it left one out for breaking the contract, the stop
   * may be that one, so the action stays pending and is never discarded.
   */
  private async confirmationByRest(
    operation: DriverOfflineOperation,
    signal: AbortSignal,
  ): Promise<DriverSyncConfirmation> {
    const list = await this.options.refreshStops(signal);
    const stop =
      list.stops.find((candidate) => candidate.order_id === operation.orderId) ??
      null;
    if (!stop) {
      return {
        outcome: list.droppedCount === 0 ? "missing" : "pending",
        stop: null,
      };
    }
    // See driverOperationConfirmedByStop: a collection by its recorded cash,
    // a failed-delivery report only by this phone's own incident or accepted
    // transition, every other action by its target status and version.
    return {
      outcome: driverOperationConfirmedByStop(operation, stop)
        ? "confirmed"
        : "pending",
      stop,
    };
  }

  /** After COD_NOT_RECORDED: whether the delivery now waits for its cash. */
  private async awaitsCash(
    operation: DriverOfflineOperation,
    signal: AbortSignal,
  ): Promise<boolean> {
    try {
      const { stop } = await this.confirmationByRest(operation, signal);
      const operations = await this.options.queue.listOperations(
        this.options.partition,
      );
      return driverDeliveryAwaitsCash(operation, stop, operations);
    } catch {
      return false;
    }
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

  /**
   * SEC-004 (owner answer Q6): a 429. Nothing was applied, so the action keeps
   * its keys and its progress (an upload session already granted, a photo
   * already uploaded) and is sent again once the server's wait ends; the whole
   * queue waits that long, and the driver sees no message. Without a usable
   * Retry-After it waits the bounded backoff instead.
   */
  private async waitForRateLimit(
    operation: DriverOfflineOperation,
    retryAfterMilliseconds: number | null,
  ): Promise<void> {
    const attemptCount = operation.attemptCount + 1;
    const wait =
      retryAfterMilliseconds ??
      DriverSyncBackoffMilliseconds[
        Math.min(attemptCount - 1, DriverSyncBackoffMilliseconds.length - 1)
      ];
    const until = this.now().getTime() + wait;
    this.rateLimitedUntil = Math.max(this.rateLimitedUntil, until);
    await this.persist(operation, {
      status: "RETRY_WAIT",
      attemptCount,
      nextAttemptAt: new Date(until).toISOString(),
      safeError: null,
    });
    this.telemetry.syncDeferred("rate-limited");
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
    const rateLimited = this.rateLimitedUntil > this.now().getTime();
    // SEC-004: while the server's wait lasts, any queued action resumes when it
    // ends, and no timer fires before it.
    if (rateLimited && operations.length > 0) timestamps.push(this.rateLimitedUntil);
    if (timestamps.length === 0) return;
    const next = Math.max(
      Math.min(...timestamps),
      rateLimited ? this.rateLimitedUntil : 0,
    );
    const delay = Math.max(0, next - this.now().getTime());
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.requestSync(false);
    }, delay);
  }

  /**
   * Another page holds the partition's lease. It may be a page that went away
   * mid-pass (a reload, a closed or discarded tab), whose lease stays until it
   * expires, so the sync asked for (for example by `online`) is not dropped:
   * it is asked again after one lease duration. Every page writes its lease
   * for that duration, so by then the lease seen here has expired, or a page
   * still at work renewed it and this one looks again later. Like the
   * backoff, it waits while the page is hidden or offline; `visibilitychange`
   * and `online` ask again (docs/development/driver-offline-replay-flake.md).
   */
  private scheduleLeaseRetry(): void {
    this.clearTimer();
    if (
      this.disposed ||
      this.pauseCount > 0 ||
      !this.isVisible() ||
      !this.isOnline()
    ) {
      return;
    }
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.requestSync(false);
    }, this.leaseMilliseconds);
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

/**
 * The next action to send: actions run in sync order (orderDriverOperations),
 * and an order whose next action is held, backing off or a delivery waiting
 * for its cash sends nothing else until it moves.
 */
export function selectRunnableOperation(
  operations: readonly DriverOfflineOperation[],
  now: Date,
  manual: boolean,
  waitingForCash: ReadonlySet<string> = new Set(),
): DriverOfflineOperation | null {
  const blockedOrders = new Set<string>();
  for (const operation of orderDriverOperations(operations)) {
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
    if (
      waitingForCash.has(operation.orderId) &&
      operation.kind !== "COD_COLLECTION"
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
    operation.kind === "DELIVERY_PROOF" ||
    // UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: "Foto del lugar".
    operation.kind === "FAILED_DELIVERY"
  );
}

interface DriverSyncConfirmation {
  readonly outcome: "confirmed" | "missing" | "pending";
  readonly stop: DriverStop | null;
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
