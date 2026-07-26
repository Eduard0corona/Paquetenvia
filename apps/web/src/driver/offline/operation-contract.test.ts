import { describe, expect, it } from "vitest";
import {
  createDriverOfflineOperation,
  driverOperationDefinitions,
  finalizeIdempotencyKey,
  parseDriverOfflineOperation,
  sessionIdempotencyKey,
  transitionIdempotencyKey,
} from "./operation-contract";

const partitionKey = "A".repeat(43);
const operationId = "11111111-1111-4111-8111-111111111111";
const orderId = "22222222-2222-4222-8222-222222222222";

describe("driver offline operation contract", () => {
  it("defines exactly the five canonical transitions", () => {
    expect(driverOperationDefinitions).toEqual({
      CHECK_IN: {
        sourceStatus: "ASSIGNED",
        targetStatus: "AT_PICKUP",
        reason: "DRIVER_CHECK_IN",
        proofType: null,
      },
      PICKUP_PROOF: {
        sourceStatus: "AT_PICKUP",
        targetStatus: "PICKED_UP",
        reason: "DRIVER_PICKUP_CONFIRMED",
        proofType: "PICKUP_PHOTO",
      },
      START_TRANSIT: {
        sourceStatus: "PICKED_UP",
        targetStatus: "IN_TRANSIT",
        reason: "DRIVER_TRANSIT_STARTED",
        proofType: null,
      },
      START_DELIVERY: {
        sourceStatus: "IN_TRANSIT",
        targetStatus: "DELIVERING",
        reason: "DRIVER_DELIVERY_STARTED",
        proofType: null,
      },
      DELIVERY_PROOF: {
        sourceStatus: "DELIVERING",
        targetStatus: "DELIVERED",
        reason: "DRIVER_DELIVERY_CONFIRMED",
        proofType: "DELIVERY_PHOTO",
      },
    });
  });

  it("captures one immutable UTC timestamp and stable idempotency keys", () => {
    const operation = createDriverOfflineOperation({
      partitionKey,
      orderId,
      kind: "PICKUP_PROOF",
      expectedVersion: 7,
      proof: {
        contentType: "image/png",
        sizeBytes: 1,
        sha256: "a".repeat(64),
      },
      now: () => new Date("2026-07-26T12:34:56.789Z"),
      randomUuid: () => operationId,
    });
    expect(operation.clientOccurredAt).toBe("2026-07-26T12:34:56.789Z");
    expect(operation.createdAt).toBe(operation.clientOccurredAt);
    expect(operation.capturedAt).toBe(operation.clientOccurredAt);
    expect(operation).toMatchObject({
      proofType: "PICKUP_PHOTO",
      contentType: "image/png",
      sizeBytes: 1,
      sha256: "a".repeat(64),
      transitionIdempotencyKey: `drv2-${operationId}-transition`,
      sessionIdempotencyKey: `drv2-${operationId}-session-1`,
      finalizeIdempotencyKey: `drv2-${operationId}-finalize-1`,
    });
    expect(transitionIdempotencyKey(operation.id)).toBe(
      `drv2-${operationId}-transition`,
    );
    expect(sessionIdempotencyKey(operation.id, 1)).toBe(
      `drv2-${operationId}-session-1`,
    );
    expect(finalizeIdempotencyKey(operation.id, 1)).toBe(
      `drv2-${operationId}-finalize-1`,
    );
    expect(sessionIdempotencyKey(operation.id, 2)).not.toBe(
      sessionIdempotencyKey(operation.id, 1),
    );
    expect(finalizeIdempotencyKey(operation.id, 2)).not.toBe(
      finalizeIdempotencyKey(operation.id, 1),
    );
    expect(transitionIdempotencyKey(operation.id)).toBe(
      transitionIdempotencyKey(operation.id),
    );
    for (const key of [
      transitionIdempotencyKey(operation.id),
      sessionIdempotencyKey(operation.id, 1),
      finalizeIdempotencyKey(operation.id, 1),
    ]) {
      expect(key).not.toContain(orderId);
      expect(key).not.toContain(partitionKey);
      expect(key).not.toContain("token");
    }
    expect(parseDriverOfflineOperation(structuredClone(operation))).toEqual(
      operation,
    );
  });

  it("fails closed on extra persisted secret fields and mapping drift", () => {
    const operation = createDriverOfflineOperation({
      partitionKey,
      orderId,
      kind: "CHECK_IN",
      expectedVersion: 1,
      randomUuid: () => operationId,
    });
    expect(() =>
      parseDriverOfflineOperation({
        ...operation,
        authorization: "Bearer secret",
      }),
    ).toThrow();
    expect(() =>
      parseDriverOfflineOperation({
        ...operation,
        reason: "client timestamp in reason",
      }),
    ).toThrow();
    expect(() =>
      parseDriverOfflineOperation({
        ...operation,
        schemaVersion: 2,
      }),
    ).toThrow();
    expect(() =>
      parseDriverOfflineOperation({
        ...operation,
        kind: "UNKNOWN",
      }),
    ).toThrow();
    expect(() =>
      parseDriverOfflineOperation({
        ...operation,
        transitionIdempotencyKey: "drv2-untrusted-transition-key",
      }),
    ).toThrow();
  });

  it("rejects proof metadata on simple operations and missing proof metadata", () => {
    expect(() =>
      createDriverOfflineOperation({
        partitionKey,
        orderId,
        kind: "CHECK_IN",
        expectedVersion: 1,
        proof: {
          contentType: "image/png",
          sizeBytes: 1,
          sha256: "a".repeat(64),
        },
        randomUuid: () => operationId,
      }),
    ).toThrow();
    expect(() =>
      createDriverOfflineOperation({
        partitionKey,
        orderId,
        kind: "DELIVERY_PROOF",
        expectedVersion: 1,
        randomUuid: () => operationId,
      }),
    ).toThrow();
  });
});
