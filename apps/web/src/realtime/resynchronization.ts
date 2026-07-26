import { asPublicOrderId, asUuid } from "./envelope";

export interface OperationsOrderCursor {
  readonly id: string;
  readonly version: number;
}

export interface DriverStopCursor {
  readonly order_id: string;
  readonly aggregate_version: number;
}

export interface PublicTrackingCursor {
  readonly public_id: string;
  readonly aggregate_version: number;
}

export function mapOperationsAggregateVersions(
  orders: readonly OperationsOrderCursor[],
): Readonly<Record<string, number>> {
  return mapVersions(
    orders,
    (order) => asUuid(order.id),
    (order) => order.version,
  );
}

export function mapDriverAggregateVersions(
  stops: readonly DriverStopCursor[],
): Readonly<Record<string, number>> {
  return mapVersions(
    stops,
    (stop) => asUuid(stop.order_id),
    (stop) => stop.aggregate_version,
  );
}

export function mapTrackingAggregateVersions(
  projection: PublicTrackingCursor,
): Readonly<Record<string, number>> {
  return mapVersions(
    [projection],
    (value) => asPublicOrderId(value.public_id),
    (value) => value.aggregate_version,
  );
}

function mapVersions<T>(
  values: readonly T[],
  aggregateId: (value: T) => string,
  version: (value: T) => number,
): Readonly<Record<string, number>> {
  const result: Record<string, number> = {};
  for (const value of values) {
    const id = aggregateId(value);
    const currentVersion = version(value);
    if (!Number.isSafeInteger(currentVersion) || currentVersion < 1) {
      throw new Error("REST synchronization returned an invalid aggregate version.");
    }

    if (Object.hasOwn(result, id)) {
      throw new Error("REST synchronization returned a duplicate aggregate.");
    }

    result[id] = currentVersion;
  }

  return result;
}
