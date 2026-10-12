import { describe, expect, it } from "vitest";
import type { DriverStop } from "../contracts/driver-stop";
import { withStopDetails } from "../contracts/driver-stop.fixtures";
import {
  createDriverOfflineOperation,
  type DriverOfflineOperation,
} from "./operation-contract";
import {
  driverOperationApplies,
  projectDriverOperations,
} from "./operation-projection";

const partitionKey = "G".repeat(43);
const orderId = "22222222-2222-4222-8222-222222222222";

function stop(status: DriverStop["status"], recorded = false, version = 9): DriverStop {
  return withStopDetails({
    order_id: orderId,
    aggregate_version: version,
    order_public_id: "ORD_SYNTHETIC",
    stop_type: "DELIVERY",
    status,
    address_summary: "Zona sintética",
    cod: { expected: { currency: "MXN", amount_cents: 125_000 }, recorded },
  });
}

function op(
  kind: DriverOfflineOperation["kind"],
  expectedVersion: number,
  second: number,
  id: string,
): DriverOfflineOperation {
  return createDriverOfflineOperation({
    partitionKey,
    orderId,
    kind,
    expectedVersion,
    ...(kind === "COD_COLLECTION"
      ? { cod: { amountCents: 125_000, reference: "ORD_SYNTHETIC 2026-10-11T13:05:00-07:00" } }
      : {}),
    ...(kind === "DELIVERY_PROOF"
      ? { proof: { contentType: "image/png" as const, sizeBytes: 1, sha256: "a".repeat(64) } }
      : {}),
    now: () => new Date(Date.UTC(2026, 9, 11, 20, 0, second)),
    randomUuid: () => id,
  });
}

describe("cash collection projection", () => {
  it("changes neither status nor version and lets the delivery follow it", () => {
    const cod = op("COD_COLLECTION", 9, 1, "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    const delivery = op("DELIVERY_PROOF", 9, 2, "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    const [projected] = projectDriverOperations([stop("DELIVERING")], [cod, delivery]).stops;
    expect(projected).toMatchObject({
      confirmedStatus: "DELIVERING",
      projectedStatus: "DELIVERED",
      projectedVersion: 10,
      pendingCount: 2,
      attentionCount: 0,
      codCollection: "queued",
    });
  });

  it("keeps a delivery with no collection saved waiting for the cash, so Recibí is offered again", () => {
    const delivery = op("DELIVERY_PROOF", 9, 2, "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    const projection = projectDriverOperations([stop("DELIVERING")], [delivery]);
    expect(projection.stops[0]).toMatchObject({
      projectedStatus: "DELIVERING",
      projectedVersion: 9,
      pendingCount: 1,
      attentionCount: 0,
      codCollection: "pending",
      deliveryAwaitingCash: true,
    });
    expect(projection.operations).toEqual([delivery]);
  });

  it("runs a collection saved after the delivery first and then applies the delivery", () => {
    const delivery = op("DELIVERY_PROOF", 9, 1, "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    const cod = op("COD_COLLECTION", 9, 2, "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    const projection = projectDriverOperations([stop("DELIVERING")], [delivery, cod]);
    expect(projection.stops[0]).toMatchObject({
      projectedStatus: "DELIVERED",
      projectedVersion: 10,
      codCollection: "queued",
      deliveryAwaitingCash: false,
    });
    expect(projection.operations.map((value) => value.kind)).toEqual([
      "COD_COLLECTION",
      "DELIVERY_PROOF",
    ]);
  });

  it("lets a delivery apply when the cash is already recorded or there is none", () => {
    const delivery = op("DELIVERY_PROOF", 9, 1, "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    for (const value of [
      stop("DELIVERING", true),
      withStopDetails({ ...stop("DELIVERING"), cod: null }),
    ]) {
      expect(projectDriverOperations([value], [delivery]).stops[0]).toMatchObject({
        projectedStatus: "DELIVERED",
        deliveryAwaitingCash: false,
      });
    }
  });

  it("applies a queued collection on top of a pending arrival at the door", () => {
    const arrival = op("START_DELIVERY", 8, 1, "cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    const cod = op("COD_COLLECTION", 9, 2, "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    const [projected] = projectDriverOperations([stop("IN_TRANSIT", false, 8)], [arrival, cod]).stops;
    expect(projected).toMatchObject({
      projectedStatus: "DELIVERING",
      projectedVersion: 9,
      attentionCount: 0,
      codCollection: "queued",
    });
  });

  it("needs the stop to be DELIVERING but not the version it was built on", () => {
    const cod = op("COD_COLLECTION", 9, 1, "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    expect(driverOperationApplies(cod, "DELIVERING", 12)).toBe(true);
    expect(driverOperationApplies(cod, "IN_TRANSIT", 9)).toBe(false);
    const transition = op("START_DELIVERY", 8, 1, "cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    expect(driverOperationApplies(transition, "IN_TRANSIT", 8)).toBe(true);
    expect(driverOperationApplies(transition, "IN_TRANSIT", 9)).toBe(false);
    const [projected] = projectDriverOperations([stop("IN_TRANSIT")], [cod]).stops;
    expect(projected).toMatchObject({ attentionCount: 1, codCollection: "attention" });
  });

  it.each([
    [stop("DELIVERING"), [], "pending"],
    [stop("DELIVERING", true), [], "recorded"],
    [withStopDetails({ ...stop("DELIVERING"), cod: null }), [], "none"],
  ] as const)("reports the cash state of the stop", (value, operations, expected) => {
    expect(projectDriverOperations([value], operations).stops[0].codCollection).toBe(expected);
  });

  it("keeps every detail of the stop in the projection", () => {
    const value = withStopDetails({
      ...stop("DELIVERING"),
      contact_short_name: "M. López",
      zone_name: "Tres Ríos",
      service_window: { from: "2026-10-11T20:00:00.000Z", to: "2026-10-11T22:00:00.000Z" },
    });
    const [projected] = projectDriverOperations([value], []).stops;
    expect(projected).toMatchObject({
      contact_short_name: "M. López",
      zone_name: "Tres Ríos",
      service_window: value.service_window,
      cod: value.cod,
      navigation: value.navigation,
    });
    expect(projected).not.toHaveProperty("status");
    expect(projected).not.toHaveProperty("aggregate_version");
  });
});
