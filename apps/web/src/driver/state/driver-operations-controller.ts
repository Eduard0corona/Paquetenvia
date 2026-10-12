import {
  createDriverCachePartition,
  DriverDatabaseBlockedError,
} from "../cache/driver-stops-cache";
import type {
  DriverStop,
  DriverStopCod,
  DriverStopList,
} from "../contracts/driver-stop";
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
  driverOperationConfirmedByStop,
} from "../offline/operation-projection";
import {
  disabledDriverSyncTelemetry,
  driverProofSizeBucket,
  DriverSyncScheduler,
  type DriverSyncTelemetry,
  type DriverSyncSchedulerOptions,
} from "../offline/driver-sync-scheduler";
import type { DriverSyncApi } from "../offline/driver-sync-api";
import {
  driverDispatchRoleReportMessage,
  driverFailedReportIncident,
  driverRoleLoadingReportMessage,
  driverRoleUnknownReportMessage,
} from "../offline/failed-delivery-policy";
import { OfflineOperationMaximumAgeHours } from "../offline/offline-operation-age";
import { driverOperationLabel } from "../contracts/labels";
import {
  validateDriverProof,
  type ValidatedDriverProof,
} from "../proofs/proof-file";
import type { DriverReportRole } from "../contracts/organization-roles";
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
  /**
   * ORG-MY-ROLES-2026-10-12: who reports, from the person's own roles in the
   * session's organization as DriverRolesController knows them now
   * (driverReportRoleOf); absent means `unknown`, and "No se pudo entregar" is
   * then refused (fails closed).
   */
  readonly reportRole?: () => DriverReportRole;
  readonly telemetry?: DriverSyncTelemetry;
  readonly queue?: DriverOfflineQueue;
  readonly schedulerFactory?: (
    options: DriverSyncSchedulerOptions,
  ) => DriverSyncScheduler;
  readonly now?: () => Date;
  readonly randomUuid?: () => string;
}

/**
 * What the expiry notice needs from an expired action: its kind and, for a
 * "No se pudo entregar" report, whether its incident is (or may be) open on
 * the server already (driverFailedReportIncident).
 */
export interface DriverExpiredOperation {
  readonly kind: DriverOperationKind;
  readonly incident: "open" | "maybe" | "none";
}

/**
 * OPS-003-OFFLINE-72H: the driver is told, in plain Spanish, that the action
 * was discarded because it waited too long without connection and that it has
 * to be registered again or reported to dispatch.
 * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: a report whose incident is (or
 * may be) open is never presented as unsent and never to register again: the
 * incident stays for dispatch, who decides.
 */
export function driverOperationExpiredMessage(
  expired: readonly (DriverOperationKind | DriverExpiredOperation)[],
): string {
  const limit = `${OfflineOperationMaximumAgeHours} horas`;
  const actions = expired.map((value) =>
    typeof value === "string" ? { kind: value, incident: "none" as const } : value,
  );
  const reported = actions.filter((action) => action.incident !== "none");
  const unsent = actions.filter((action) => action.incident === "none");
  const parts: string[] = [];
  if (unsent.length === 1) {
    parts.push(
      `La acción «${driverOperationLabel(unsent[0].kind)}» venció: pasaron más de ` +
        `${limit} sin conexión y ya no se enviará. Vuelve a registrarla o ` +
        "repórtala a despacho.",
    );
  } else if (unsent.length > 1) {
    parts.push(
      `${unsent.length} acciones guardadas vencieron: pasaron más de ${limit} ` +
        "sin conexión y ya no se enviarán. Vuelve a registrarlas o repórtalas a " +
        "despacho.",
    );
  }
  if (reported.length > 0) {
    parts.push(
      reported.some((action) => action.incident === "open")
        ? `Tu reporte «${driverOperationLabel("FAILED_DELIVERY")}» ya quedó abierto como incidencia y ` +
            `despacho decide qué sigue; el teléfono dejó de seguirlo porque pasaron más de ${limit} ` +
            "sin conexión. No lo vuelvas a reportar."
        : `Tu reporte «${driverOperationLabel("FAILED_DELIVERY")}» venció sin confirmarse: puede que ya ` +
            "haya quedado abierto como incidencia. Avisa a despacho y no lo vuelvas a reportar.",
    );
  }
  return parts.join(" ");
}

/** Shown when another tab of an older version keeps the phone's database open. */
export const driverDatabaseBlockedMessage =
  "Hay otra pestaña de Paquetenvia abierta con una versión anterior. Ciérrala y vuelve a cargar esta página " +
  "para usar las acciones guardadas en el teléfono.";

/**
 * Why "Enviar reporte" stored nothing, in the words the form shows
 * (UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12).
 */
export type DriverFailedDeliveryEnqueueResult =
  | { readonly stored: true }
  | { readonly stored: false; readonly message: string };

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
  private expired: DriverExpiredOperation[] = [];

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
        onOperationExpired: (operation) => this.notifyExpired(operation),
        // Each pass reports only its own expirations; a notice already shown
        // is never folded into a later one.
        onSyncPassStarted: () => {
          this.expired = [];
        },
        telemetry: this.telemetry,
        now: this.options.now,
        randomUuid: this.options.randomUuid,
      });
      this.scheduler.start();
    } catch (error) {
      this.setState({
        ...initialState,
        loading: false,
        message:
          error instanceof DriverDatabaseBlockedError
            ? driverDatabaseBlockedMessage
            : "La cola local no está disponible.",
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
   * when "No pagó el cobro" is chosen on a stop without cash on delivery,
   * (owner answer "No, llama a despacho (Recommended)") when the order's cash
   * is saved on the phone or recorded on the server, (owner answer "Registrar
   * desde Despacho (Recommended)") when the person is also dispatch in the
   * organization, and (ORG-MY-ROLES-2026-10-12) when the person's roles there
   * are not known. At most one unsettled report per order is
   * ever queued: a report REST already confirms only waits to leave the queue.
   * Resolves `{ stored: true }` only when this call stored it; otherwise the
   * result carries the reason the form shows.
   */
  public async enqueueFailedDelivery(
    input: {
      readonly orderId: string;
      readonly reasonCode: DriverFailedDeliveryReason;
      readonly projectedStatus: DriverOperationalStatus;
      readonly projectedVersion: number;
      /** The stop as REST confirms it (ProjectedDriverStop.confirmedStatus and confirmedVersion). */
      readonly confirmedStatus: DriverStop["status"];
      readonly confirmedVersion: number;
      /** DriverStop.cod of the stop: the only source of whether it collects cash. */
      readonly cod: DriverStopCod | null;
    },
    blob?: Blob,
  ): Promise<DriverFailedDeliveryEnqueueResult> {
    const refuse = (message: string): DriverFailedDeliveryEnqueueResult => {
      this.setMessage(message);
      return Object.freeze({ stored: false, message });
    };
    if (!this.partition) return refuse("La cola local no está disponible.");
    if (this.state.mutating) {
      return refuse("Espera a que termine la acción en curso y vuelve a enviar el reporte.");
    }
    // Only a person whose roles here are known and include neither DISPATCHER
    // nor PLATFORM_ADMIN files the report from the phone: the server treats a
    // person who is also dispatch as dispatch and refuses the short report
    // (owner answer "Registrar desde Despacho (Recommended)"), and unknown
    // roles fail closed (ORG-MY-ROLES-2026-10-12), also while they load.
    switch (this.options.reportRole?.() ?? "unknown") {
      case "dispatch":
        return refuse(driverDispatchRoleReportMessage);
      case "loading":
        return refuse(driverRoleLoadingReportMessage);
      case "unknown":
        return refuse(driverRoleUnknownReportMessage);
      case "driver":
        break;
    }
    if (
      input.projectedStatus !==
      driverOperationDefinitions.FAILED_DELIVERY.sourceStatus
    ) {
      return refuse("Actualiza la parada antes de reportar.");
    }
    if (
      !isDriverFailedDeliveryReason(input.reasonCode) ||
      !driverFailedDeliveryReasonAllowed(input.reasonCode, input.cod !== null)
    ) {
      return refuse("Elige un motivo de la lista.");
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
      return refuse(
        "El cobro ya está registrado. Llama a despacho: ellos deciden y coordinan la devolución del cobro.",
      );
    }
    if (sameOrder.some((candidate) => candidate.kind === "COD_COLLECTION")) {
      return refuse(
        "Hay un cobro guardado en el teléfono. Si fue un error, descártalo antes de reportar.",
      );
    }
    // A report REST already confirms (for example its transition was accepted
    // and dispatch then chose "Reintentar hoy") is settled: the stop no longer
    // shows it and it leaves the queue on the next pass, so it never holds a
    // new report back (see driverOperationConfirmedByStop).
    const confirmedStop = {
      status: input.confirmedStatus,
      aggregate_version: input.confirmedVersion,
      cod: input.cod,
    };
    if (
      sameOrder.some(
        (candidate) =>
          candidate.kind === "FAILED_DELIVERY" &&
          !driverOperationConfirmedByStop(candidate, confirmedStop),
      )
    ) {
      return refuse("Ya hay un reporte guardado en el teléfono para esta entrega.");
    }
    if (sameOrder.some((candidate) => candidate.kind === "DELIVERY_PROOF")) {
      return refuse(
        "La entrega ya está guardada en el teléfono, así que no se puede reportar que no se pudo entregar.",
      );
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
      const message =
        "No fue posible guardar el reporte. Verifica tipo, tamaño y espacio disponible.";
      this.setState({ ...this.state, mutating: false, message });
      return Object.freeze({ stored: false, message });
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
    return Object.freeze({ stored: true });
  }

  public async syncNow(): Promise<void> {
    this.expired = [];
    this.setMessage("Sincronizando acciones pendientes.");
    await this.scheduler?.requestSync(true);
    await this.reload(
      this.expired.length > 0
        ? driverOperationExpiredMessage(this.expired)
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
      if (discarded && confirmedVersion !== undefined) {
        if (confirmedStatus) {
          await this.recalculateOrderChain(
            discarded.orderId,
            confirmedStatus,
            confirmedVersion,
          );
        } else {
          // The stop is no longer at any step the phone acts on (for example
          // FAILED_ATTEMPT): nothing left of the order can apply, so the first
          // remaining action is held for the driver, who can discard it.
          await this.holdOrderForDriver(discarded.orderId);
        }
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
    const nextAttemptAt = (this.options.now ?? (() => new Date()))().toISOString();
    await this.queue.replaceOperation(this.partition, {
      ...operation,
      status: "PENDING",
      nextAttemptAt,
      safeError: null,
      rejectionCode: null,
    });
    // The actions held behind it go again after it, in dependency order; if
    // it is refused again the sync holds them again.
    for (const blocked of this.state.operations) {
      if (
        blocked.orderId === operation.orderId &&
        blocked.status === "BLOCKED" &&
        compareDriverOperationDependency(blocked, operation) > 0
      ) {
        await this.queue.replaceOperation(this.partition, {
          ...blocked,
          status: "PENDING",
          nextAttemptAt,
          safeError: null,
          rejectionCode: null,
        });
      }
    }
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
    if (old.kind === "FAILED_DELIVERY") {
      // UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: a report is never rebuilt
      // for another version. Before its incident it describes an attempt that
      // moved on, so the driver reports again from the stop with a new reason
      // and photo; after it, the incident belongs to that attempt (and may
      // already justify a failed attempt), so dispatch decides.
      this.setMessage("Este reporte no se puede rehacer para otra versión.");
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
      if (operation.status === "AWAITING_REST_CONFIRMATION") {
        // Accepted by the server: it only waits for REST and is never turned
        // back into an action to send.
        if (!blocked && driverOperationApplies(operation, status, version)) {
          ({ status, version } = applyDriverOperation(operation, status, version));
        }
        continue;
      }
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

  /**
   * After a discard on a stop whose confirmed status is not one the phone acts
   * on (FAILED_ATTEMPT, RESCHEDULED, RETURNING): no remaining action of the
   * order can apply, so unless one already waits for the driver the first of
   * them is held for attention (VERSION_CONFLICT) and the rest stay blocked
   * behind it. The driver can always discard it; nothing is ever left blocked
   * with no action shown.
   */
  private async holdOrderForDriver(orderId: string): Promise<void> {
    if (!this.partition) return;
    const operations = (await this.queue.listOperations(this.partition))
      .filter(
        (candidate) =>
          candidate.orderId === orderId &&
          candidate.status !== "AWAITING_REST_CONFIRMATION",
      )
      .sort(compareDriverOperationDependency);
    if (operations.some((candidate) => candidate.status === "NEEDS_ATTENTION")) {
      return;
    }
    let held = false;
    for (const operation of operations) {
      await this.queue.replaceOperation(this.partition, {
        ...operation,
        status: held ? "BLOCKED" : "NEEDS_ATTENTION",
        safeError: held ? operation.safeError : "VERSION_CONFLICT",
        rejectionCode: held ? operation.rejectionCode : null,
      });
      held = true;
    }
  }

  private beginMutation(): void {
    this.expired = [];
    this.setState({ ...this.state, mutating: true, message: null });
  }

  private notifyExpired(operation: DriverOfflineOperation): void {
    this.expired = [
      ...this.expired,
      {
        kind: operation.kind,
        incident: driverFailedReportIncident(operation),
      },
    ];
    this.setMessage(driverOperationExpiredMessage(this.expired));
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
