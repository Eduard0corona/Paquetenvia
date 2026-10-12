import { createDriverCachePartition } from "../cache/driver-stops-cache";
import type { DriverStopCod, DriverStopList } from "../contracts/driver-stop";
import {
  driverFailedDeliveryReasonAllowed,
  isDriverFailedDeliveryReason,
  type DriverFailedDeliveryReason,
} from "../contracts/failed-delivery";
import {
  IndexedDbDriverOfflineQueue,
  type DriverOfflineQueue,
} from "../offline/driver-offline-queue";
import {
  buildCodReference,
  createDriverOfflineOperation,
  driverOperationDefinitions,
  finalizeIdempotencyKey,
  sessionIdempotencyKey,
  type DriverOfflineOperation,
  type DriverOperationKind,
  type DriverOperationStatus,
  type DriverOperationalStatus,
} from "../offline/operation-contract";
import {
  applyDriverOperation,
  compareDriverOperationDependency,
  driverOperationApplies,
} from "../offline/operation-projection";
import {
  disabledDriverSyncTelemetry,
  driverProofSizeBucket,
  DriverSyncScheduler,
  type DriverSyncTelemetry,
  type DriverSyncSchedulerOptions,
} from "../offline/driver-sync-scheduler";
import type { DriverSyncApi } from "../offline/driver-sync-api";
import { OfflineOperationMaximumAgeHours } from "../offline/offline-operation-age";
import { driverOperationLabel } from "../contracts/labels";
import {
  validateDriverProof,
  type ValidatedDriverProof,
} from "../proofs/proof-file";
import type { DriverSession } from "../session/driver-session";

export interface DriverOperationsState {
  readonly operations: readonly DriverOfflineOperation[];
  readonly loading: boolean;
  readonly mutating: boolean;
  readonly message: string | null;
}

export interface DriverOperationsControllerOptions {
  readonly session: DriverSession;
  readonly api: DriverSyncApi;
  /** The confirmation read; see DriverSyncSchedulerOptions.refreshStops. */
  readonly refreshStops: (signal?: AbortSignal) => Promise<DriverStopList>;
  readonly onAccessRevoked: (category: "unauthorized" | "forbidden") => void;
  readonly telemetry?: DriverSyncTelemetry;
  readonly queue?: DriverOfflineQueue;
  readonly schedulerFactory?: (
    options: DriverSyncSchedulerOptions,
  ) => DriverSyncScheduler;
  readonly now?: () => Date;
  readonly randomUuid?: () => string;
}

/**
 * OPS-003-OFFLINE-72H: the driver is told, in plain Spanish, that the action
 * was discarded because it waited too long without connection and that it has
 * to be registered again or reported to dispatch.
 */
export function driverOperationExpiredMessage(
  kinds: readonly DriverOperationKind[],
): string {
  const limit = `${OfflineOperationMaximumAgeHours} horas`;
  if (kinds.length === 1) {
    return (
      `La acción «${driverOperationLabel(kinds[0])}» venció: pasaron más de ` +
      `${limit} sin conexión y ya no se enviará. Vuelve a registrarla o ` +
      "repórtala a despacho."
    );
  }
  return (
    `${kinds.length} acciones guardadas vencieron: pasaron más de ${limit} ` +
    "sin conexión y ya no se enviarán. Vuelve a registrarlas o repórtalas a " +
    "despacho."
  );
}

const initialState: DriverOperationsState = Object.freeze({
  operations: [],
  loading: true,
  mutating: false,
  message: null,
});

export class DriverOperationsController {
  private readonly listeners = new Set<
    (state: DriverOperationsState) => void
  >();
  private readonly queue: DriverOfflineQueue;
  private state: DriverOperationsState = initialState;
  private partition: Awaited<
    ReturnType<typeof createDriverCachePartition>
  > | null = null;
  private scheduler: DriverSyncScheduler | null = null;
  private readonly telemetry: DriverSyncTelemetry;
  private disposed = false;
  private expiredKinds: DriverOperationKind[] = [];

  public constructor(private readonly options: DriverOperationsControllerOptions) {
    this.queue = options.queue ?? new IndexedDbDriverOfflineQueue();
    this.telemetry = options.telemetry ?? disabledDriverSyncTelemetry;
  }

  public get current(): DriverOperationsState {
    return this.state;
  }

  public subscribe(
    listener: (state: DriverOperationsState) => void,
  ): () => void {
    this.listeners.add(listener);
    listener(this.state);
    return () => this.listeners.delete(listener);
  }

  public async start(): Promise<void> {
    try {
      this.partition = await createDriverCachePartition(this.options.session);
      const operations = await this.queue.listOperations(this.partition);
      this.setState({ ...initialState, operations, loading: false });
      const createScheduler =
        this.options.schedulerFactory ??
        ((schedulerOptions) => new DriverSyncScheduler(schedulerOptions));
      this.scheduler = createScheduler({
        partition: this.partition,
        queue: this.queue,
        api: this.options.api,
        refreshStops: this.options.refreshStops,
        onQueueChanged: (next) =>
          this.setState({ ...this.state, operations: next, loading: false }),
        onAccessRevoked: this.options.onAccessRevoked,
        onOperationExpired: (operation) => this.notifyExpired(operation.kind),
        // Each pass reports only its own expirations; a notice already shown
        // is never folded into a later one.
        onSyncPassStarted: () => {
          this.expiredKinds = [];
        },
        telemetry: this.telemetry,
        now: this.options.now,
        randomUuid: this.options.randomUuid,
      });
      this.scheduler.start();
    } catch {
      this.setState({
        ...initialState,
        loading: false,
        message: "La cola local no está disponible.",
      });
    }
  }

  /**
   * Resolves `true` only when this call stored the operation (and its proof photo) in the
   * local queue, so the photo preview knows whether to offer the same photo again.
   */
  public async enqueue(
    input: {
      readonly orderId: string;
      readonly kind: DriverOperationKind;
      readonly projectedStatus: DriverOperationalStatus;
      readonly projectedVersion: number;
    },
    blob?: Blob,
  ): Promise<boolean> {
    if (!this.partition || this.state.mutating) return false;
    const definition = driverOperationDefinitions[input.kind];
    if (definition.sourceStatus !== input.projectedStatus) {
      this.setMessage("Actualiza la parada antes de registrar esta acción.");
      return false;
    }
    if (
      this.state.operations.some(
        (candidate) =>
          candidate.orderId === input.orderId &&
          candidate.kind === input.kind &&
          candidate.expectedVersion === input.projectedVersion &&
          candidate.status !== "NEEDS_ATTENTION",
      )
    ) {
      return false;
    }

    this.beginMutation();
    let operation: DriverOfflineOperation;
    let proof: ValidatedDriverProof | undefined;
    try {
      if (definition.proofType) proof = await validateDriverProof(blob);
      operation = createDriverOfflineOperation({
        partitionKey: this.partition.key,
        orderId: input.orderId,
        kind: input.kind,
        expectedVersion: input.projectedVersion,
        proof,
        now: this.options.now,
        randomUuid: this.options.randomUuid,
      });
      await this.queue.enqueue(this.partition, operation, proof);
    } catch {
      this.setState({
        ...this.state,
        mutating: false,
        message:
          definition.proofType
            ? "No fue posible guardar la foto. Verifica tipo, tamaño y espacio disponible."
            : "No fue posible guardar la acción.",
      });
      return false;
    }

    const saved = "Acción guardada. Se sincronizará cuando haya conexión.";
    try {
      if (proof) {
        this.telemetry.proofBytesQueued(
          driverProofSizeBucket(proof.sizeBytes),
        );
      }
      await this.reload(saved);
    } catch {
      // The write already succeeded. If the queue cannot be read back, the action still
      // shows as saved and stays in the local list, so the duplicate check above sees it.
      this.setState({
        operations: [...this.state.operations, operation],
        loading: false,
        mutating: false,
        message: saved,
      });
    }
    void this.scheduler?.requestSync(false);
    return true;
  }

  /**
   * UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11 ("Recibí $1,250.00"): queues the
   * cash collection of a DELIVERING delivery stop for exactly the amount the
   * stop expects, with a reference made of the tracking number and the device
   * time of this confirmation. It always syncs before the delivery of the same
   * order (dependency order), also when it is saved again after a discard and
   * the delivery was already waiting for it. At most one collection per order
   * is ever queued.
   */
  public async enqueueCodCollection(input: {
    readonly orderId: string;
    readonly orderPublicId: string;
    readonly amountCents: number;
    readonly projectedStatus: DriverOperationalStatus;
    readonly projectedVersion: number;
  }): Promise<boolean> {
    if (!this.partition || this.state.mutating) return false;
    if (input.projectedStatus !== driverOperationDefinitions.COD_COLLECTION.sourceStatus) {
      this.setMessage("Actualiza la parada antes de registrar el cobro.");
      return false;
    }
    if (
      this.state.operations.some(
        (candidate) =>
          candidate.orderId === input.orderId &&
          candidate.kind === "COD_COLLECTION",
      )
    ) {
      return false;
    }

    this.beginMutation();
    let operation: DriverOfflineOperation;
    try {
      const now = (this.options.now ?? (() => new Date()))();
      operation = createDriverOfflineOperation({
        partitionKey: this.partition.key,
        orderId: input.orderId,
        kind: "COD_COLLECTION",
        expectedVersion: input.projectedVersion,
        cod: {
          amountCents: input.amountCents,
          reference: buildCodReference(input.orderPublicId, now),
        },
        now: () => now,
        randomUuid: this.options.randomUuid,
      });
      await this.queue.enqueue(this.partition, operation);
    } catch {
      this.setState({
        ...this.state,
        mutating: false,
        message: "No fue posible guardar el cobro. Intenta de nuevo.",
      });
      return false;
    }

    const saved = "Cobro guardado. Se registrará cuando haya conexión.";
    try {
      await this.reload(saved);
    } catch {
      this.setState({
        operations: [...this.state.operations, operation],
        loading: false,
        mutating: false,
        message: saved,
      });
    }
    void this.scheduler?.requestSync(false);
    return true;
  }

  /**
   * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12 ("Enviar reporte"): queues
   * "No se pudo entregar" for a DELIVERY stop at the door with the reason from
   * the closed list and exactly one photo ("Foto del lugar"). The operation
   * keeps the photo, the four Idempotency-Keys (upload session, proof,
   * incident, transition) and its progress, so a retry resumes at the right
   * step. Refused, and nothing is stored, when the stop is not DELIVERING,
   * when "No pagó el cobro" is chosen on a stop without cash on delivery, and
   * (owner answer "No, llama a despacho (Recommended)") when the order's cash
   * is saved on the phone or recorded on the server. At most one report per
   * order is ever queued. Resolves `true` only when this call stored it.
   */
  public async enqueueFailedDelivery(
    input: {
      readonly orderId: string;
      readonly reasonCode: DriverFailedDeliveryReason;
      readonly projectedStatus: DriverOperationalStatus;
      readonly projectedVersion: number;
      /** DriverStop.cod of the stop: the only source of whether it collects cash. */
      readonly cod: DriverStopCod | null;
    },
    blob?: Blob,
  ): Promise<boolean> {
    if (!this.partition || this.state.mutating) return false;
    if (
      input.projectedStatus !==
      driverOperationDefinitions.FAILED_DELIVERY.sourceStatus
    ) {
      this.setMessage("Actualiza la parada antes de reportar.");
      return false;
    }
    if (
      !isDriverFailedDeliveryReason(input.reasonCode) ||
      !driverFailedDeliveryReasonAllowed(input.reasonCode, input.cod !== null)
    ) {
      this.setMessage("Elige un motivo de la lista.");
      return false;
    }
    const sameOrder = this.state.operations.filter(
      (candidate) => candidate.orderId === input.orderId,
    );
    if (
      input.cod?.recorded === true ||
      sameOrder.some(
        (candidate) =>
          candidate.kind === "COD_COLLECTION" &&
          candidate.status === "AWAITING_REST_CONFIRMATION",
      )
    ) {
      this.setMessage(
        "El cobro ya está registrado. Llama a despacho: ellos deciden y coordinan la devolución del cobro.",
      );
      return false;
    }
    if (sameOrder.some((candidate) => candidate.kind === "COD_COLLECTION")) {
      this.setMessage(
        "Hay un cobro guardado en el teléfono. Si fue un error, descártalo antes de reportar.",
      );
      return false;
    }
    if (
      sameOrder.some(
        (candidate) =>
          candidate.kind === "FAILED_DELIVERY" ||
          candidate.kind === "DELIVERY_PROOF",
      )
    ) {
      return false;
    }

    this.beginMutation();
    let operation: DriverOfflineOperation;
    let proof: ValidatedDriverProof;
    try {
      proof = await validateDriverProof(blob);
      operation = createDriverOfflineOperation({
        partitionKey: this.partition.key,
        orderId: input.orderId,
        kind: "FAILED_DELIVERY",
        expectedVersion: input.projectedVersion,
        proof,
        failedDelivery: { reasonCode: input.reasonCode },
        now: this.options.now,
        randomUuid: this.options.randomUuid,
      });
      await this.queue.enqueue(this.partition, operation, proof);
    } catch {
      this.setState({
        ...this.state,
        mutating: false,
        message:
          "No fue posible guardar el reporte. Verifica tipo, tamaño y espacio disponible.",
      });
      return false;
    }

    const saved = "Reporte guardado. Se enviará cuando haya conexión.";
    try {
      this.telemetry.proofBytesQueued(driverProofSizeBucket(proof.sizeBytes));
      await this.reload(saved);
    } catch {
      this.setState({
        operations: [...this.state.operations, operation],
        loading: false,
        mutating: false,
        message: saved,
      });
    }
    void this.scheduler?.requestSync(false);
    return true;
  }

  public async syncNow(): Promise<void> {
    this.expiredKinds = [];
    this.setMessage("Sincronizando acciones pendientes.");
    await this.scheduler?.requestSync(true);
    await this.reload(
      this.expiredKinds.length > 0
        ? driverOperationExpiredMessage(this.expiredKinds)
        : null,
    );
  }

  public async discard(
    operationId: string,
    confirmedStatus: DriverOperationalStatus | null = null,
    confirmedVersion?: number,
  ): Promise<void> {
    if (!this.partition || this.state.mutating) return;
    const discarded = this.state.operations.find(
      (candidate) => candidate.id === operationId,
    );
    this.beginMutation();
    // UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: a saved collection may be
    // discarded while it is still pending, so the sync stops first; a pass
    // that was already sending it can neither send it again nor write it back.
    const scheduler = this.scheduler;
    await scheduler?.pause();
    try {
      await this.queue.deleteOperation(this.partition, operationId);
      if (
        discarded &&
        confirmedStatus &&
        confirmedVersion !== undefined
      ) {
        await this.recalculateOrderChain(
          discarded.orderId,
          confirmedStatus,
          confirmedVersion,
        );
      }
    } finally {
      scheduler?.resume();
    }
    await this.reload("La acción y su evidencia local se descartaron.");
    // What the discard unblocked (for example a report held for the cash it
    // had saved) is sent without waiting for the next trigger.
    void scheduler?.requestSync(false);
  }

  public async createNewSession(operationId: string): Promise<void> {
    if (!this.partition || this.state.mutating) return;
    const operation = this.state.operations.find(
      (candidate) => candidate.id === operationId,
    );
    if (
      !operation ||
      operation.status !== "NEEDS_ATTENTION" ||
      operation.safeError !== "SESSION_EXPIRED"
    ) {
      return;
    }
    this.beginMutation();
    await this.queue.replaceOperation(this.partition, {
      ...operation,
      status: "PENDING",
      attemptCount: 0,
      nextAttemptAt: (this.options.now ?? (() => new Date()))().toISOString(),
      sessionAttempt: operation.sessionAttempt + 1,
      sessionIdempotencyKey: sessionIdempotencyKey(
        operation.id,
        operation.sessionAttempt + 1,
      ),
      finalizeIdempotencyKey: finalizeIdempotencyKey(
        operation.id,
        operation.sessionAttempt + 1,
      ),
      uploadSessionId: null,
      uploadSessionExpiresAt: null,
      uploadAccepted: false,
      proofId: null,
      safeError: null,
    });
    await this.reload("Se creó un nuevo intento de carga.");
    void this.scheduler?.requestSync(false);
  }

  public async retrySame(
    operationId: string,
    confirmedStatus: DriverOperationalStatus,
    confirmedVersion: number,
  ): Promise<void> {
    if (!this.partition || this.state.mutating) return;
    const operation = this.state.operations.find(
      (candidate) => candidate.id === operationId,
    );
    if (
      !operation ||
      !driverOperationApplies(operation, confirmedStatus, confirmedVersion)
    ) {
      this.setMessage("La versión confirmada cambió; crea una acción nueva.");
      return;
    }
    this.beginMutation();
    await this.queue.replaceOperation(this.partition, {
      ...operation,
      status: "PENDING",
      nextAttemptAt: (this.options.now ?? (() => new Date()))().toISOString(),
      safeError: null,
      rejectionCode: null,
    });
    await this.reload("La misma acción quedó lista para reintentar.");
    void this.scheduler?.requestSync(false);
  }

  public async rebuildForCurrentVersion(
    operationId: string,
    confirmedStatus: DriverOperationalStatus,
    confirmedVersion: number,
  ): Promise<void> {
    if (!this.partition || this.state.mutating) return;
    const old = this.state.operations.find(
      (candidate) => candidate.id === operationId,
    );
    if (!old || driverOperationDefinitions[old.kind].sourceStatus !== confirmedStatus) {
      this.setMessage("La acción ya no corresponde al estado confirmado.");
      return;
    }
    if (!driverOperationDefinitions[old.kind].transition) {
      // A cash collection is never rebuilt with a new key: it is retried as it
      // is, or discarded and recorded again from the stop.
      this.setMessage("Reintenta el cobro o descártalo y vuelve a registrarlo.");
      return;
    }
    this.beginMutation();
    const replacement = createDriverOfflineOperation({
      partitionKey: this.partition.key,
      orderId: old.orderId,
      kind: old.kind,
      expectedVersion: confirmedVersion,
      proof:
        old.proofType &&
        old.contentType &&
        old.sizeBytes &&
        old.sha256
          ? {
              contentType: old.contentType,
              sizeBytes: old.sizeBytes,
              sha256: old.sha256,
            }
          : undefined,
      failedDelivery: old.failedDelivery
        ? { reasonCode: old.failedDelivery.reasonCode }
        : undefined,
      now: this.options.now,
      randomUuid: this.options.randomUuid,
    });
    const replacementWithFinalizedProof = old.proofId
      ? {
          ...replacement,
          clientOccurredAt: old.clientOccurredAt,
          capturedAt: old.capturedAt,
          proofId: old.proofId,
        }
      : replacement;
    // UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: a rebuilt report keeps its
    // incident (and the key that opened it, in case only its answer was lost),
    // so it never opens a second incident; only the transition is new.
    const rebuilt = old.failedDelivery
      ? { ...replacementWithFinalizedProof, failedDelivery: old.failedDelivery }
      : replacementWithFinalizedProof;
    await this.queue.rebuildOperation(this.partition, old.id, rebuilt);
    await this.reload("Se creó una acción nueva para la versión confirmada.");
    void this.scheduler?.requestSync(false);
  }

  public async dispose(): Promise<void> {
    this.disposed = true;
    await this.scheduler?.dispose();
    this.scheduler = null;
    this.listeners.clear();
  }

  private async reload(message: string | null): Promise<void> {
    if (!this.partition) return;
    const operations = await this.queue.listOperations(this.partition);
    this.setState({
      operations,
      loading: false,
      mutating: false,
      message,
    });
  }

  /**
   * After a discard, the rest of the order's chain is checked again in
   * dependency order. A delivery left without its cash collection becomes
   * PENDING here and then waits for the cash (driverDeliveryAwaitsCash), so
   * "Recibí" is offered again instead of a conflict.
   */
  private async recalculateOrderChain(
    orderId: string,
    confirmedStatus: DriverOperationalStatus,
    confirmedVersion: number,
  ): Promise<void> {
    if (!this.partition) return;
    const operations = (await this.queue.listOperations(this.partition))
      .filter((candidate) => candidate.orderId === orderId)
      .sort(compareDriverOperationDependency);
    let status = confirmedStatus;
    let version = confirmedVersion;
    let blocked = false;
    for (const operation of operations) {
      const nextStatus: DriverOperationStatus =
        blocked
          ? "BLOCKED"
          : driverOperationApplies(operation, status, version)
            ? "PENDING"
            : "NEEDS_ATTENTION";
      blocked = nextStatus !== "PENDING";
      if (!blocked) {
        ({ status, version } = applyDriverOperation(operation, status, version));
      }
      await this.queue.replaceOperation(this.partition, {
        ...operation,
        status: nextStatus,
        safeError:
          nextStatus === "NEEDS_ATTENTION"
            ? "VERSION_CONFLICT"
            : nextStatus === "PENDING"
              ? null
              : operation.safeError,
        rejectionCode: nextStatus === "PENDING" ? null : operation.rejectionCode,
      });
    }
  }

  private beginMutation(): void {
    this.expiredKinds = [];
    this.setState({ ...this.state, mutating: true, message: null });
  }

  private notifyExpired(kind: DriverOperationKind): void {
    this.expiredKinds = [...this.expiredKinds, kind];
    this.setMessage(driverOperationExpiredMessage(this.expiredKinds));
  }

  private setMessage(message: string): void {
    this.setState({ ...this.state, message });
  }

  private setState(state: DriverOperationsState): void {
    if (this.disposed) return;
    this.state = Object.freeze(state);
    for (const listener of this.listeners) listener(this.state);
  }
}
