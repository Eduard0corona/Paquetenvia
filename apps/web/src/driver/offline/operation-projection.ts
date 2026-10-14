import type { DriverStop } from "../contracts/driver-stop";
import {
  driverOperationDefinitions,
  type DriverOfflineOperation,
  type DriverOperationStatus,
  type DriverOperationalStatus,
} from "./operation-contract";

/**
 * Where the cash of a DELIVERY stop stands for the driver: nothing to collect,
 * still to collect, saved on the phone and waiting to sync, held for the
 * driver's attention, or recorded on the server.
 */
export type DriverCodCollectionState =
  | "none"
  | "pending"
  | "queued"
  | "attention"
  | "recorded";

/**
 * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: whether the stop has a
 * "No se pudo entregar" report on the phone, waiting to sync ("queued") or
 * held for the driver's attention. A report that REST confirmed has left the
 * queue; the stop then reads FAILED_ATTEMPT from the server.
 */
export type DriverFailedReportState = "none" | "queued" | "attention";

export interface ProjectedDriverStop
  extends Omit<DriverStop, "status" | "aggregate_version"> {
  readonly confirmedStatus: DriverStop["status"];
  readonly confirmedVersion: number;
  readonly projectedStatus: DriverOperationalStatus;
  readonly projectedVersion: number;
  readonly pendingCount: number;
  readonly attentionCount: number;
  readonly codCollection: DriverCodCollectionState;
  /**
   * A delivery is saved on the phone but waits, unsent, for the cash: the stop
   * still has cash to collect and no collection is saved for it (see
   * driverDeliveryAwaitsCash). The stop stays DELIVERING, so "Recibí" is
   * offered again.
   */
  readonly deliveryAwaitingCash: boolean;
  /** The "No se pudo entregar" report saved on the phone, if any. */
  readonly failedReport: DriverFailedReportState;
}

export interface DriverOperationProjection {
  readonly stops: readonly ProjectedDriverStop[];
  readonly operations: readonly DriverOfflineOperation[];
}

/**
 * Whether a queued operation still applies on top of the projected status and
 * version of its order. A transition needs its source status and the exact
 * version it was built on; a cash collection only needs the stop to be
 * DELIVERING, because recording cash changes neither status nor version.
 */
export function driverOperationApplies(
  operation: DriverOfflineOperation,
  status: DriverOperationalStatus,
  version: number,
): boolean {
  if (operation.sourceStatus !== status) return false;
  return (
    !driverOperationDefinitions[operation.kind].transition ||
    operation.expectedVersion === version
  );
}

/** The projected status and version once the operation applied. */
export function applyDriverOperation(
  operation: DriverOfflineOperation,
  status: DriverOperationalStatus,
  version: number,
): { readonly status: DriverOperationalStatus; readonly version: number } {
  return driverOperationDefinitions[operation.kind].transition
    ? { status: operation.targetStatus, version: version + 1 }
    : { status, version };
}

const driverOperationalStatusOrder: readonly DriverOperationalStatus[] = [
  "ASSIGNED",
  "AT_PICKUP",
  "PICKED_UP",
  "IN_TRANSIT",
  "DELIVERING",
  "DELIVERED",
];

/**
 * UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: the place of a queued action in
 * its order's chain. Actions of one order run in this dependency order, not in
 * the order they were saved: each step before the next one (check-in, pickup,
 * transit, start of the delivery) and, at the door, the cash collection before
 * the delivery, even when the collection was saved again after a discard.
 *
 * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: a "No se pudo entregar" report
 * is the first action at the door. The screen never saves it next to a
 * collection or a delivery of the same order, so this only matters when both
 * reach the queue anyway (for example from a second tab): nothing else of the
 * order runs while the report is pending, held or backing off, and the
 * scheduler holds the report itself while a collection exists, so the cash and
 * the report are never both sent.
 */
function dependencyRank(operation: DriverOfflineOperation): number {
  const definition = driverOperationDefinitions[operation.kind];
  const step =
    driverOperationalStatusOrder.indexOf(definition.sourceStatus) * 3;
  if (operation.kind === "FAILED_DELIVERY") return step;
  return step + (definition.transition ? 2 : 1);
}

/**
 * Orders two queued actions of the same order by dependency; actions at the
 * same place of the chain keep the order they were saved in.
 */
export function compareDriverOperationDependency(
  left: DriverOfflineOperation,
  right: DriverOfflineOperation,
): number {
  return (
    dependencyRank(left) - dependencyRank(right) ||
    compareOperations(left, right)
  );
}

/**
 * The queue in sync order: orders by their oldest saved action, and the
 * actions of each order by dependency (compareDriverOperationDependency).
 */
export function orderDriverOperations(
  operations: readonly DriverOfflineOperation[],
): readonly DriverOfflineOperation[] {
  const byOrder = new Map<string, DriverOfflineOperation[]>();
  for (const operation of [...operations].sort(compareOperations)) {
    const existing = byOrder.get(operation.orderId) ?? [];
    existing.push(operation);
    byOrder.set(operation.orderId, existing);
  }
  return Object.freeze(
    [...byOrder.values()].flatMap((orderOperations) =>
      orderOperations.sort(compareDriverOperationDependency),
    ),
  );
}

/**
 * Whether REST shows a queued action applied, so the queue may remove it.
 *
 * - A cash collection: the stop reports exactly its cash as recorded (the
 *   DELIVERED guard condition); the status never changes.
 * - A "No se pudo entregar" report (UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12):
 *   once the server accepted its transition (AWAITING_REST_CONFIRMATION), any
 *   later version confirms it, whatever the status: dispatch may already have
 *   decided ("Reintentar hoy" or "Regresar") before the phone read REST. Before
 *   that, a FAILED_ATTEMPT confirms it only when this phone's incident exists:
 *   a failed attempt that dispatch registered on its own never confirms a
 *   report that was not sent (it is held instead; see the scheduler).
 * - Any other transition: REST shows its target status at a later version.
 */
export function driverOperationConfirmedByStop(
  operation: DriverOfflineOperation,
  stop: Pick<DriverStop, "status" | "aggregate_version" | "cod">,
): boolean {
  if (operation.kind === "COD_COLLECTION") {
    return (
      stop.cod?.recorded === true &&
      stop.cod.expected.amount_cents === operation.cod?.amountCents
    );
  }
  const advanced = stop.aggregate_version >= operation.expectedVersion + 1;
  if (operation.kind === "FAILED_DELIVERY") {
    if (operation.status === "AWAITING_REST_CONFIRMATION") return advanced;
    return (
      advanced &&
      stop.status === "FAILED_ATTEMPT" &&
      operation.failedDelivery?.incidentId != null
    );
  }
  return stop.status === operation.targetStatus && advanced;
}

/**
 * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: a report REST already confirms
 * (its transition was accepted and the stop is at a later version, or its own
 * incident exists and the stop shows the failed attempt) is settled: it is not
 * projected again, never shown as a conflict, and leaves the queue on the next
 * pass. The stop then shows what REST says, for example DELIVERING again after
 * dispatch chose "Reintentar hoy".
 *
 * Any other transition the driver has not been asked to resolve is settled
 * the same way once REST shows it applied (its target status at a later
 * version): the scheduler removes it without sending it again (DRV-002, "Si
 * REST ya confirma el estado y la versión esperados, elimina la operación sin
 * repetir la mutación"). The screen takes the confirmation read before the
 * scheduler removes the action and publishes the queue, so without this the
 * stop showed, in between, the action it had just confirmed as a conflict: the
 * next step disappeared for a moment and a photo the driver was previewing for
 * it was lost (docs/development/driver-photo-selection-refresh.md). An action
 * held for the driver (NEEDS_ATTENTION or BLOCKED) stays held, and REST beyond
 * the action's target is still a conflict, as before.
 */
function settledByStop(
  operation: DriverOfflineOperation,
  stop: DriverStop,
): boolean {
  if (operation.kind === "FAILED_DELIVERY") {
    return driverOperationConfirmedByStop(operation, stop);
  }
  return (
    driverOperationDefinitions[operation.kind].transition &&
    operation.status !== "NEEDS_ATTENTION" &&
    operation.status !== "BLOCKED" &&
    driverOperationConfirmedByStop(operation, stop)
  );
}

/**
 * UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: a delivery whose stop still has
 * cash to collect waits, unsent, while no cash collection is saved for its
 * order, because the server refuses DELIVERED without the recorded cash
 * (COD_NOT_RECORDED). It happens after the driver discards a held collection
 * or with a delivery saved before collections existed. "Recibí" is offered
 * again; the new collection goes first and the delivery right after it.
 */
export function driverDeliveryAwaitsCash(
  operation: DriverOfflineOperation,
  stop: Pick<DriverStop, "cod"> | null,
  operations: readonly DriverOfflineOperation[],
): boolean {
  return (
    operation.kind === "DELIVERY_PROOF" &&
    stop !== null &&
    stop.cod !== null &&
    !stop.cod.recorded &&
    !operations.some(
      (candidate) =>
        candidate.orderId === operation.orderId &&
        candidate.kind === "COD_COLLECTION",
    )
  );
}

/**
 * Produces the optimistic view without mutating persisted data. An invalid
 * source/version chain marks the first operation for attention and blocks only
 * later operations for that order; other orders keep projecting. A delivery
 * waiting for its cash is not applied, so the stop stays DELIVERING. A blocked
 * action with nothing ahead of it held for attention (for example after the
 * action that held it was discarded on a stop dispatch moved on) is shown as
 * held itself, so the driver can always discard it.
 */
export function projectDriverOperations(
  confirmedStops: readonly DriverStop[],
  operations: readonly DriverOfflineOperation[],
): DriverOperationProjection {
  const operationsByOrder = new Map<string, DriverOfflineOperation[]>();
  for (const operation of orderDriverOperations(operations)) {
    const existing = operationsByOrder.get(operation.orderId) ?? [];
    existing.push(operation);
    operationsByOrder.set(operation.orderId, existing);
  }

  const projectedOperations = new Map<string, DriverOfflineOperation>();
  const settled = new Set<string>();
  const stops = confirmedStops.map((stop): ProjectedDriverStop => {
    let status = stop.status as DriverOperationalStatus;
    let version = stop.aggregate_version;
    let chainBlocked = false;
    let pendingCount = 0;
    let attentionCount = 0;
    let deliveryAwaitingCash = false;
    let codOperationStatus: DriverOperationStatus | null = null;
    let failedReport: DriverFailedReportState = "none";
    for (const operation of operationsByOrder.get(stop.order_id) ?? []) {
      if (!chainBlocked && settledByStop(operation, stop)) {
        settled.add(operation.id);
        continue;
      }
      let derivedStatus: DriverOperationStatus = operation.status;
      if (chainBlocked) {
        derivedStatus = "BLOCKED";
      } else if (
        operation.status === "NEEDS_ATTENTION" ||
        operation.status === "BLOCKED"
      ) {
        // A blocked action with nothing held ahead of it is the one to resolve.
        derivedStatus = "NEEDS_ATTENTION";
        chainBlocked = true;
      } else if (!driverOperationApplies(operation, status, version)) {
        derivedStatus = "NEEDS_ATTENTION";
        chainBlocked = true;
      } else if (driverDeliveryAwaitsCash(operation, stop, operations)) {
        deliveryAwaitingCash = true;
      } else {
        ({ status, version } = applyDriverOperation(operation, status, version));
      }
      const projected =
        derivedStatus === operation.status
          ? operation
          : Object.freeze({
              ...operation,
              status: derivedStatus,
              safeError:
                derivedStatus === "NEEDS_ATTENTION"
                  ? operation.status === "BLOCKED" && operation.safeError !== null
                    ? operation.safeError
                    : "VERSION_CONFLICT"
                  : operation.safeError,
            });
      projectedOperations.set(operation.id, projected);
      pendingCount += 1;
      if (derivedStatus === "NEEDS_ATTENTION") attentionCount += 1;
      if (operation.kind === "COD_COLLECTION") codOperationStatus = derivedStatus;
      if (operation.kind === "FAILED_DELIVERY") {
        failedReport =
          derivedStatus === "NEEDS_ATTENTION" || derivedStatus === "BLOCKED"
            ? "attention"
            : "queued";
      }
    }

    const {
      status: confirmedStatus,
      aggregate_version: confirmedVersion,
      ...details
    } = stop;
    return Object.freeze({
      ...details,
      confirmedStatus,
      confirmedVersion,
      projectedStatus: status,
      projectedVersion: version,
      pendingCount,
      attentionCount,
      codCollection: codCollectionState(stop, codOperationStatus),
      deliveryAwaitingCash,
      failedReport,
    });
  });

  // Operations for a stop that REST no longer exposes are not attached to
  // another partition or guessed resource.
  for (const operation of operations) {
    if (!projectedOperations.has(operation.id) && !settled.has(operation.id)) {
      projectedOperations.set(
        operation.id,
        operation.status === "NEEDS_ATTENTION"
          ? operation
          : Object.freeze({
              ...operation,
              status: "NEEDS_ATTENTION",
              safeError: "RESOURCE_UNAVAILABLE",
            }),
      );
    }
  }

  return Object.freeze({
    stops: Object.freeze(stops),
    operations: orderDriverOperations([...projectedOperations.values()]),
  });
}

function codCollectionState(
  stop: DriverStop,
  codOperationStatus: DriverOperationStatus | null,
): DriverCodCollectionState {
  if (stop.cod === null) return "none";
  if (stop.cod.recorded) return "recorded";
  if (codOperationStatus === null) return "pending";
  return codOperationStatus === "NEEDS_ATTENTION" ||
    codOperationStatus === "BLOCKED"
    ? "attention"
    : "queued";
}

export function nextDriverOperationKind(
  status: DriverOperationalStatus,
): DriverOfflineOperation["kind"] | null {
  switch (status) {
    case "ASSIGNED":
      return "CHECK_IN";
    case "AT_PICKUP":
      return "PICKUP_PROOF";
    case "PICKED_UP":
      return "START_TRANSIT";
    case "IN_TRANSIT":
      return "START_DELIVERY";
    case "DELIVERING":
      return "DELIVERY_PROOF";
    case "DELIVERED":
      return null;
    // INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11: after a failed attempt
    // the driver has no next action; dispatch decides.
    case "FAILED_ATTEMPT":
      return null;
  }
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
