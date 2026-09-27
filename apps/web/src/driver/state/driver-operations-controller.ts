import { createDriverCachePartition } from "../cache/driver-stops-cache";
import type { DriverStop } from "../contracts/driver-stop";
import {
  IndexedDbDriverOfflineQueue,
  type DriverOfflineQueue,
} from "../offline/driver-offline-queue";
import {
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
  readonly refreshStops: (
    signal?: AbortSignal,
  ) => Promise<readonly DriverStop[]>;
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

  public async enqueue(
    input: {
      readonly orderId: string;
      readonly kind: DriverOperationKind;
      readonly projectedStatus: DriverOperationalStatus;
      readonly projectedVersion: number;
    },
    blob?: Blob,
  ): Promise<void> {
    if (!this.partition || this.state.mutating) return;
    const definition = driverOperationDefinitions[input.kind];
    if (definition.sourceStatus !== input.projectedStatus) {
      this.setMessage("Actualiza la parada antes de registrar esta acción.");
      return;
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
      return;
    }

    this.beginMutation();
    try {
      let proof: ValidatedDriverProof | undefined;
      if (definition.proofType) proof = await validateDriverProof(blob);
      const operation = createDriverOfflineOperation({
        partitionKey: this.partition.key,
        orderId: input.orderId,
        kind: input.kind,
        expectedVersion: input.projectedVersion,
        proof,
        now: this.options.now,
        randomUuid: this.options.randomUuid,
      });
      await this.queue.enqueue(this.partition, operation, proof);
      if (proof) {
        this.telemetry.proofBytesQueued(
          driverProofSizeBucket(proof.sizeBytes),
        );
      }
      await this.reload("Acción guardada. Se sincronizará cuando haya conexión.");
      void this.scheduler?.requestSync(false);
    } catch {
      this.setState({
        ...this.state,
        mutating: false,
        message:
          definition.proofType
            ? "No fue posible guardar la foto. Verifica tipo, tamaño y espacio disponible."
            : "No fue posible guardar la acción.",
      });
    }
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
    await this.reload("La acción y su evidencia local se descartaron.");
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
      operation.sourceStatus !== confirmedStatus ||
      operation.expectedVersion !== confirmedVersion
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
    await this.queue.rebuildOperation(
      this.partition,
      old.id,
      replacementWithFinalizedProof,
    );
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

  private async recalculateOrderChain(
    orderId: string,
    confirmedStatus: DriverOperationalStatus,
    confirmedVersion: number,
  ): Promise<void> {
    if (!this.partition) return;
    const operations = (await this.queue.listOperations(this.partition))
      .filter((candidate) => candidate.orderId === orderId)
      .sort(
        (left, right) =>
          left.createdAt.localeCompare(right.createdAt) ||
          left.id.localeCompare(right.id),
      );
    let status = confirmedStatus;
    let version = confirmedVersion;
    let blocked = false;
    for (const operation of operations) {
      const nextStatus: DriverOperationStatus =
        blocked
          ? "BLOCKED"
          : operation.sourceStatus === status &&
              operation.expectedVersion === version
            ? "PENDING"
            : "NEEDS_ATTENTION";
      blocked = nextStatus !== "PENDING";
      if (!blocked) {
        status = operation.targetStatus;
        version += 1;
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
