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
 */
function dependencyRank(operation: DriverOfflineOperation): number {
  const definition = driverOperationDefinitions[operation.kind];
  return (
    driverOperationalStatusOrder.indexOf(definition.sourceStatus) * 2 +
    (definition.transition ? 1 : 0)
  );
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
 * waiting for its cash is not applied, so the stop stays DELIVERING.
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
  const stops = confirmedStops.map((stop): ProjectedDriverStop => {
    let status = stop.status as DriverOperationalStatus;
    let version = stop.aggregate_version;
    let chainBlocked = false;
    let pendingCount = 0;
    let attentionCount = 0;
    let deliveryAwaitingCash = false;
    let codOperationStatus: DriverOperationStatus | null = null;
    for (const operation of operationsByOrder.get(stop.order_id) ?? []) {
      let derivedStatus: DriverOperationStatus = operation.status;
      if (chainBlocked) {
        derivedStatus = "BLOCKED";
      } else if (
        operation.status === "NEEDS_ATTENTION" ||
        operation.status === "BLOCKED"
      ) {
        derivedStatus = operation.status;
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
                  ? "VERSION_CONFLICT"
                  : operation.safeError,
            });
      projectedOperations.set(operation.id, projected);
      pendingCount += 1;
      if (derivedStatus === "NEEDS_ATTENTION") attentionCount += 1;
      if (operation.kind === "COD_COLLECTION") codOperationStatus = derivedStatus;
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
    });
  });

  // Operations for a stop that REST no longer exposes are not attached to
  // another partition or guessed resource.
  for (const operation of operations) {
    if (!projectedOperations.has(operation.id)) {
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
