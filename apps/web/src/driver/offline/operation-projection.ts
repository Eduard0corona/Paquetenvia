import type { DriverStop } from "../contracts/driver-stop";
import type {
  DriverOfflineOperation,
  DriverOperationStatus,
  DriverOperationalStatus,
} from "./operation-contract";

export interface ProjectedDriverStop
  extends Omit<DriverStop, "status" | "aggregate_version"> {
  readonly confirmedStatus: DriverStop["status"];
  readonly confirmedVersion: number;
  readonly projectedStatus: DriverOperationalStatus;
  readonly projectedVersion: number;
  readonly pendingCount: number;
  readonly attentionCount: number;
}

export interface DriverOperationProjection {
  readonly stops: readonly ProjectedDriverStop[];
  readonly operations: readonly DriverOfflineOperation[];
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
      } else if (
        operation.sourceStatus !== status ||
        operation.expectedVersion !== version
      ) {
        derivedStatus = "NEEDS_ATTENTION";
        chainBlocked = true;
      } else {
        status = operation.targetStatus;
        version += 1;
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
    }

    return Object.freeze({
      order_id: stop.order_id,
      order_public_id: stop.order_public_id,
      stop_type: stop.stop_type,
      address_summary: stop.address_summary,
      confirmedStatus: stop.status,
      confirmedVersion: stop.aggregate_version,
      projectedStatus: status,
      projectedVersion: version,
      pendingCount,
      attentionCount,
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
