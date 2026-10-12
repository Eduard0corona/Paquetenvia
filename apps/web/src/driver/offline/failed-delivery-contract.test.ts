import { describe, expect, it } from "vitest";
import {
  createDriverOfflineOperation,
  driverOperationKinds,
  incidentIdempotencyKey,
  migrateDriverOfflineOperationRecord,
  parseDriverOfflineOperation,
  type DriverOfflineOperation,
} from "./operation-contract";
import {
  failedDeliveryOperation,
  orderId,
  partition,
  photoSha256,
  reportId,
  reportedAt,
  uuid,
} from "./failed-delivery.fixtures";

/**
 * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: "No se pudo entregar" is one
 * closed offline operation (schema 3) that keeps its photo metadata, its four
 * Idempotency-Keys and its progress, and nothing a person could be identified by.
 */
describe("failed-delivery operation contract", () => {
  it("creates the report with its photo, the four stable keys and no progress", () => {
    const operation = failedDeliveryOperation({}, "PACKAGE_DAMAGED");
    expect(operation).toEqual({
      schemaVersion: 3,
      partitionKey: partition.key,
      id: reportId,
      orderId,
      kind: "FAILED_DELIVERY",
      sourceStatus: "DELIVERING",
      targetStatus: "FAILED_ATTEMPT",
      expectedVersion: 8,
      reason: "DRIVER_DELIVERY_FAILED_REPORTED",
      clientOccurredAt: reportedAt.toISOString(),
      createdAt: reportedAt.toISOString(),
      transitionIdempotencyKey: `drv2-${reportId}-transition`,
      sessionIdempotencyKey: `drv2-${reportId}-session-1`,
      finalizeIdempotencyKey: `drv2-${reportId}-finalize-1`,
      proofType: "DELIVERY_PHOTO",
      contentType: "image/png",
      sizeBytes: 3,
      sha256: photoSha256,
      capturedAt: reportedAt.toISOString(),
      status: "PENDING",
      attemptCount: 0,
      nextAttemptAt: reportedAt.toISOString(),
      sessionAttempt: 1,
      uploadSessionId: null,
      uploadSessionExpiresAt: null,
      uploadAccepted: false,
      proofId: null,
      safeError: null,
      cod: null,
      rejectionCode: null,
      failedDelivery: {
        reasonCode: "PACKAGE_DAMAGED",
        incidentIdempotencyKey: `drv2-${reportId}-incident`,
        incidentRequested: false,
        incidentId: null,
      },
    });
    expect(incidentIdempotencyKey(reportId)).toBe(`drv2-${reportId}-incident`);
    expect(parseDriverOfflineOperation(structuredClone(operation), partition.key)).toEqual(
      operation,
    );
    expect(driverOperationKinds).toContain("FAILED_DELIVERY");
  });

  it("keeps no personal data, text, token, signed URL or object key", () => {
    const serialized = JSON.stringify(
      failedDeliveryOperation({
        uploadSessionId: uuid(0x501),
        proofId: uuid(0x601),
        failedDelivery: {
          reasonCode: "RECIPIENT_ABSENT",
          incidentIdempotencyKey: `drv2-${reportId}-incident`,
          incidentRequested: true,
          incidentId: uuid(0x701),
        },
      }),
    );
    for (const forbidden of [
      "Sintética",
      "http",
      "quarantine",
      "Bearer",
      "token",
      "description",
      "severity",
      "next_action",
      "Intento fallido reportado",
    ]) {
      expect(serialized).not.toContain(forbidden);
    }
  });

  it("parses every step of saved progress", () => {
    const steps: Partial<DriverOfflineOperation>[] = [
      { status: "PROCESSING", uploadSessionId: uuid(0x501), uploadSessionExpiresAt: "2026-10-12T19:30:00.000Z" },
      { uploadSessionId: uuid(0x501), uploadSessionExpiresAt: "2026-10-12T19:30:00.000Z", uploadAccepted: true },
      { uploadSessionId: uuid(0x501), uploadAccepted: true, proofId: uuid(0x601) },
      {
        // openIncident went out; its answer may have been lost.
        proofId: uuid(0x601),
        failedDelivery: {
          reasonCode: "RECIPIENT_ABSENT",
          incidentIdempotencyKey: `drv2-${reportId}-incident`,
          incidentRequested: true,
          incidentId: null,
        },
        status: "RETRY_WAIT",
      },
      {
        proofId: uuid(0x601),
        failedDelivery: {
          reasonCode: "RECIPIENT_ABSENT",
          incidentIdempotencyKey: `drv2-${reportId}-incident`,
          incidentRequested: true,
          incidentId: uuid(0x701),
        },
        status: "AWAITING_REST_CONFIRMATION",
      },
    ];
    for (const patch of steps) {
      const operation = failedDeliveryOperation(patch);
      expect(parseDriverOfflineOperation(operation, partition.key)).toEqual(operation);
    }
  });

  it("requires the report exactly on FAILED_DELIVERY and the photo with it", () => {
    expect(() =>
      createDriverOfflineOperation({
        partitionKey: partition.key,
        orderId,
        kind: "FAILED_DELIVERY",
        expectedVersion: 8,
        proof: { contentType: "image/png", sizeBytes: 3, sha256: photoSha256 },
        randomUuid: () => reportId,
      }),
    ).toThrow();
    expect(() =>
      createDriverOfflineOperation({
        partitionKey: partition.key,
        orderId,
        kind: "FAILED_DELIVERY",
        expectedVersion: 8,
        failedDelivery: { reasonCode: "RECIPIENT_ABSENT" },
        randomUuid: () => reportId,
      }),
    ).toThrow();
    expect(() =>
      createDriverOfflineOperation({
        partitionKey: partition.key,
        orderId,
        kind: "DELIVERY_PROOF",
        expectedVersion: 8,
        proof: { contentType: "image/png", sizeBytes: 3, sha256: photoSha256 },
        failedDelivery: { reasonCode: "RECIPIENT_ABSENT" },
        randomUuid: () => reportId,
      }),
    ).toThrow();
    expect(() =>
      createDriverOfflineOperation({
        partitionKey: partition.key,
        orderId,
        kind: "FAILED_DELIVERY",
        expectedVersion: 8,
        proof: { contentType: "image/png", sizeBytes: 3, sha256: photoSha256 },
        failedDelivery: { reasonCode: "SOMETHING_ELSE" as never },
        randomUuid: () => reportId,
      }),
    ).toThrow();
  });

  it.each<[string, (operation: Record<string, unknown>) => unknown]>([
    ["a reason outside the closed list", (value) => ({ ...value, failedDelivery: { ...(value.failedDelivery as object), reasonCode: "OTHER" } })],
    ["free text in the report", (value) => ({ ...value, failedDelivery: { ...(value.failedDelivery as object), description: "Nadie abrió" } })],
    ["a severity in the report", (value) => ({ ...value, failedDelivery: { ...(value.failedDelivery as object), severity: "HIGH" } })],
    ["a malformed incident key", (value) => ({ ...value, failedDelivery: { ...(value.failedDelivery as object), incidentIdempotencyKey: "drv2-x-incident" } })],
    ["an upper-case incident key", (value) => ({ ...value, failedDelivery: { ...(value.failedDelivery as object), incidentIdempotencyKey: `drv2-${reportId.toUpperCase()}-incident` } })],
    ["the incident key of another operation", (value) => ({ ...value, failedDelivery: { ...(value.failedDelivery as object), incidentIdempotencyKey: `drv2-${uuid(0x999)}-incident` } })],
    ["a non-canonical incident id", (value) => ({ ...value, proofId: uuid(0x601), failedDelivery: { ...(value.failedDelivery as object), incidentRequested: true, incidentId: "not-a-uuid" } })],
    ["an incident without the finalized photo", (value) => ({ ...value, failedDelivery: { ...(value.failedDelivery as object), incidentRequested: true, incidentId: uuid(0x701) } })],
    ["an incident requested without the finalized photo", (value) => ({ ...value, failedDelivery: { ...(value.failedDelivery as object), incidentRequested: true } })],
    ["an incident id that was never requested", (value) => ({ ...value, proofId: uuid(0x601), failedDelivery: { ...(value.failedDelivery as object), incidentId: uuid(0x701) } })],
    ["a non-boolean incident request", (value) => ({ ...value, proofId: uuid(0x601), failedDelivery: { ...(value.failedDelivery as object), incidentRequested: "yes" } })],
    ["a missing incident request", (value) => { const report = { ...(value.failedDelivery as Record<string, unknown>) }; delete report.incidentRequested; return { ...value, failedDelivery: report }; }],
    ["no report member", (value) => ({ ...value, failedDelivery: null })],
    ["a missing report member", (value) => { const copy = { ...value }; delete copy.failedDelivery; return copy; }],
    ["no photo", (value) => ({ ...value, proofType: null, contentType: null, sizeBytes: null, sha256: null, capturedAt: null, sessionIdempotencyKey: null, finalizeIdempotencyKey: null })],
    ["a pickup photo", (value) => ({ ...value, proofType: "PICKUP_PHOTO" })],
    ["another target", (value) => ({ ...value, targetStatus: "DELIVERED" })],
    ["another reason", (value) => ({ ...value, reason: "DRIVER_DELIVERY_CONFIRMED" })],
    ["a cash member", (value) => ({ ...value, cod: { amountCents: 1, reference: "R", idempotencyKey: `drv2-${reportId}-cod` } })],
    ["an unknown safe error", (value) => ({ ...value, safeError: "SOMETHING" })],
    ["an unknown kind", (value) => ({ ...value, kind: "FAILED_PICKUP" })],
    ["an older schema", (value) => ({ ...value, schemaVersion: 2 })],
    ["a newer schema", (value) => ({ ...value, schemaVersion: 4 })],
  ])("fails closed on %s", (_name, mutate) => {
    const operation = failedDeliveryOperation() as unknown as Record<string, unknown>;
    expect(() => parseDriverOfflineOperation(mutate(operation), partition.key)).toThrow();
  });

  it("keeps a held report's reason and the cash hold", () => {
    const held = failedDeliveryOperation({
      status: "NEEDS_ATTENTION",
      safeError: "COD_COLLECTED",
    });
    expect(parseDriverOfflineOperation(held, partition.key)).toMatchObject({
      status: "NEEDS_ATTENTION",
      safeError: "COD_COLLECTED",
    });
    for (const code of ["EVIDENCE_NOT_AVAILABLE", "INVALID_REQUEST", "DISPATCH_DECISION_REQUIRED"]) {
      expect(
        parseDriverOfflineOperation({ ...held, safeError: "VERSION_CONFLICT", rejectionCode: code }).rejectionCode,
      ).toBe(code);
    }
  });

  it("never accepts the incident key of another report: a report is never rebuilt", () => {
    const other = {
      ...createDriverOfflineOperation({
        partitionKey: partition.key,
        orderId,
        kind: "FAILED_DELIVERY",
        expectedVersion: 10,
        proof: { contentType: "image/png", sizeBytes: 3, sha256: photoSha256 },
        failedDelivery: { reasonCode: "RECIPIENT_ABSENT" },
        randomUuid: () => uuid(0x999),
      }),
      failedDelivery: {
        reasonCode: "RECIPIENT_ABSENT" as const,
        incidentIdempotencyKey: `drv2-${reportId}-incident`,
        incidentRequested: false,
        incidentId: null,
      },
    };
    expect(() => parseDriverOfflineOperation(other, partition.key)).toThrow();
  });

  it("is never produced by the migration: older records get no report", () => {
    const report = failedDeliveryOperation();
    expect(migrateDriverOfflineOperationRecord(report)).toBe(report);
    const schema2 = { ...report, schemaVersion: 2 } as Record<string, unknown>;
    delete schema2.failedDelivery;
    // A schema 2 record cannot be a report: the migration adds a null member,
    // so a FAILED_DELIVERY kind there fails closed instead of being guessed.
    expect(() =>
      parseDriverOfflineOperation(migrateDriverOfflineOperationRecord(schema2), partition.key),
    ).toThrow();
  });
});
