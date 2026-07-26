import { asUuid } from "../../realtime/envelope";

export const MaximumStopsPerResponse = 500;
export const MaximumOrderPublicIdLength = 80;
export const MaximumAddressSummaryLength = 240;

export const driverStopTypes = ["PICKUP", "DELIVERY", "RETURN"] as const;
export const driverStopStatuses = [
  "ASSIGNED",
  "AT_PICKUP",
  "PICKED_UP",
  "IN_TRANSIT",
  "DELIVERING",
  "FAILED_ATTEMPT",
  "RESCHEDULED",
  "RETURNING",
] as const;

export interface DriverStop {
  readonly order_id: string;
  readonly aggregate_version: number;
  readonly order_public_id: string;
  readonly stop_type: (typeof driverStopTypes)[number];
  readonly status: (typeof driverStopStatuses)[number];
  readonly address_summary: string;
}

const exactProperties = new Set([
  "order_id",
  "aggregate_version",
  "order_public_id",
  "stop_type",
  "status",
  "address_summary",
]);

export class DriverStopsContractError extends Error {
  public constructor() {
    super("La respuesta de paradas no cumple el contrato esperado.");
    this.name = "DriverStopsContractError";
  }
}

export function parseDriverStops(value: unknown): readonly DriverStop[] {
  if (!Array.isArray(value) || value.length > MaximumStopsPerResponse) {
    throw new DriverStopsContractError();
  }

  const orderIds = new Set<string>();
  return value.map((candidate) => {
    if (
      candidate === null ||
      typeof candidate !== "object" ||
      Array.isArray(candidate)
    ) {
      throw new DriverStopsContractError();
    }

    const record = candidate as Record<string, unknown>;
    const keys = Object.keys(record);
    if (
      keys.length !== exactProperties.size ||
      keys.some((key) => !exactProperties.has(key))
    ) {
      throw new DriverStopsContractError();
    }

    const orderId = readUuid(record.order_id);
    if (orderIds.has(orderId)) {
      throw new DriverStopsContractError();
    }
    orderIds.add(orderId);

    const aggregateVersion = record.aggregate_version;
    if (
      !Number.isSafeInteger(aggregateVersion) ||
      (aggregateVersion as number) < 1
    ) {
      throw new DriverStopsContractError();
    }

    const publicId = readBoundedText(
      record.order_public_id,
      MaximumOrderPublicIdLength,
    );
    const address = readBoundedText(
      record.address_summary,
      MaximumAddressSummaryLength,
    );
    if (!driverStopTypes.includes(record.stop_type as DriverStop["stop_type"])) {
      throw new DriverStopsContractError();
    }
    if (!driverStopStatuses.includes(record.status as DriverStop["status"])) {
      throw new DriverStopsContractError();
    }

    return Object.freeze({
      order_id: orderId,
      aggregate_version: aggregateVersion as number,
      order_public_id: publicId,
      stop_type: record.stop_type as DriverStop["stop_type"],
      status: record.status as DriverStop["status"],
      address_summary: address,
    });
  });
}

function readUuid(value: unknown): string {
  if (typeof value !== "string") {
    throw new DriverStopsContractError();
  }

  try {
    return asUuid(value);
  } catch {
    throw new DriverStopsContractError();
  }
}

function readBoundedText(value: unknown, maximumLength: number): string {
  if (
    typeof value !== "string" ||
    value.length < 1 ||
    value.length > maximumLength ||
    value.trim().length < 1
  ) {
    throw new DriverStopsContractError();
  }

  return value;
}
