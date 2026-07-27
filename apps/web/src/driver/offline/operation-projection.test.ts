import { describe, expect, it } from "vitest";
import type { DriverStop } from "../contracts/driver-stop";
import {
  createDriverOfflineOperation,
  type DriverOfflineOperation,
} from "./operation-contract";
import { projectDriverOperations } from "./operation-projection";

const partitionKey = "B".repeat(43);
const orderA = "11111111-1111-4111-8111-111111111111";
const orderB = "22222222-2222-4222-8222-222222222222";
const stops: readonly DriverStop[] = [
  {
    order_id: orderA,
    aggregate_version: 3,
    order_public_id: "ORD_SYNTHETIC_A",
    stop_type: "PICKUP",
    status: "ASSIGNED",
    address_summary: "Zona sintética A",
  },
  {
    order_id: orderB,
    aggregate_version: 8,
    order_public_id: "ORD_SYNTHETIC_B",
    stop_type: "DELIVERY",
    status: "IN_TRANSIT",
    address_summary: "Zona sintética B",
  },
];

describe("offline operation projection", () => {
  it("projects FIFO chains while keeping confirmed state separate", () => {
    const operations = [
      operation("CHECK_IN", orderA, 3, "2026-07-26T10:00:00.000Z", 1),
      operation("PICKUP_PROOF", orderA, 4, "2026-07-26T10:00:01.000Z", 2),
      operation("START_TRANSIT", orderA, 5, "2026-07-26T10:00:02.000Z", 3),
    ];
    const projection = projectDriverOperations(stops, operations);
    expect(projection.stops[0]).toMatchObject({
      confirmedStatus: "ASSIGNED",
      confirmedVersion: 3,
      projectedStatus: "IN_TRANSIT",
      projectedVersion: 6,
      pendingCount: 3,
    });
    expect(projection.stops[0].confirmedStatus).toBe("ASSIGNED");
    expect(stops[0].status).toBe("ASSIGNED");
  });

  it("projects the complete canonical chain to delivered with consecutive versions", () => {
    const operations = [
      operation("CHECK_IN", orderA, 3, "2026-07-26T10:00:00.000Z", 1),
      operation("PICKUP_PROOF", orderA, 4, "2026-07-26T10:00:01.000Z", 2),
      operation("START_TRANSIT", orderA, 5, "2026-07-26T10:00:02.000Z", 3),
      operation("START_DELIVERY", orderA, 6, "2026-07-26T10:00:03.000Z", 4),
      operation("DELIVERY_PROOF", orderA, 7, "2026-07-26T10:00:04.000Z", 5),
    ];

    expect(projectDriverOperations(stops, operations).stops[0]).toMatchObject({
      confirmedStatus: "ASSIGNED",
      confirmedVersion: 3,
      projectedStatus: "DELIVERED",
      projectedVersion: 8,
      pendingCount: 5,
    });
  });

  it("marks only the invalid order chain and continues another order", () => {
    const operations = [
      operation("CHECK_IN", orderA, 99, "2026-07-26T10:00:00.000Z", 1),
      operation("PICKUP_PROOF", orderA, 100, "2026-07-26T10:00:01.000Z", 2),
      operation("START_DELIVERY", orderB, 8, "2026-07-26T10:00:02.000Z", 3),
    ];
    const projection = projectDriverOperations(stops, operations);
    expect(projection.operations.map((candidate) => candidate.status)).toEqual([
      "NEEDS_ATTENTION",
      "BLOCKED",
      "PENDING",
    ]);
    expect(projection.stops[1].projectedStatus).toBe("DELIVERING");
  });
});

function operation(
  kind: Parameters<typeof createDriverOfflineOperation>[0]["kind"],
  orderId: string,
  expectedVersion: number,
  timestamp: string,
  idTail: number,
): DriverOfflineOperation {
  return createDriverOfflineOperation({
    partitionKey,
    orderId,
    kind,
    expectedVersion,
    proof: kind.endsWith("_PROOF")
      ? {
          contentType: "image/png",
          sizeBytes: 1,
          sha256: "a".repeat(64),
        }
      : undefined,
    now: () => new Date(timestamp),
    randomUuid: () =>
      `00000000-0000-4000-8000-${idTail.toString().padStart(12, "0")}`,
  });
}
