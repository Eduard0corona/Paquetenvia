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

/**
 * Produces the optimistic view without mutating persisted data. An invalid
 * source/version chain marks the first operation for attention and blocks only
 * later operations for that order; other orders keep projecting.
 */
export function projectDriverOperations(
  confirmedStops: readonly DriverStop[],
  operations: readonly DriverOfflineOperation[],
): DriverOperationProjection {
  const operationsByOrder = new Map<string, DriverOfflineOperation[]>();
  for (const operation of [...operations].sort(compareOperations)) {
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
    operations: Object.freeze(
      [...projectedOperations.values()].sort(compareOperations),
    ),
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
