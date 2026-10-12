import { describe, expect, it } from "vitest";
import {
  buildCodReference,
  codIdempotencyKey,
  createDriverOfflineOperation,
  formatLocalTimestamp,
  migrateDriverOfflineOperationRecord,
  parseDriverOfflineOperation,
  transitionIdempotencyKey,
} from "./operation-contract";

/** UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: the cash collection in the offline queue (schema 2). */
const partitionKey = "F".repeat(43);
const operationId = "11111111-1111-4111-8111-111111111111";
const orderId = "22222222-2222-4222-8222-222222222222";
const reference = "ORD_abcdefghijklmnopqrstuv 2026-10-11T13:05:42-07:00";

function codOperation() {
  return createDriverOfflineOperation({
    partitionKey,
    orderId,
    kind: "COD_COLLECTION",
    expectedVersion: 9,
    cod: { amountCents: 125_000, reference },
    now: () => new Date("2026-10-11T20:05:42.000Z"),
    randomUuid: () => operationId,
  });
}

describe("cash collection operation", () => {
  it("keeps the exact amount, the app's reference and one stable key", () => {
    const operation = codOperation();
    expect(operation).toMatchObject({
      schemaVersion: 2,
      kind: "COD_COLLECTION",
      sourceStatus: "DELIVERING",
      targetStatus: "DELIVERING",
      reason: "DRIVER_COD_COLLECTED",
      proofType: null,
      sessionIdempotencyKey: null,
      finalizeIdempotencyKey: null,
      rejectionCode: null,
      cod: {
        amountCents: 125_000,
        reference,
        idempotencyKey: `drv2-${operationId}-cod`,
      },
    });
    expect(codIdempotencyKey(operationId)).toBe(`drv2-${operationId}-cod`);
    expect(parseDriverOfflineOperation(operation, partitionKey)).toEqual(operation);
  });

  it("requires the cash members exactly on the cash collection", () => {
    expect(() =>
      createDriverOfflineOperation({
        partitionKey,
        orderId,
        kind: "COD_COLLECTION",
        expectedVersion: 1,
        randomUuid: () => operationId,
      }),
    ).toThrow();
    expect(() =>
      createDriverOfflineOperation({
        partitionKey,
        orderId,
        kind: "START_DELIVERY",
        expectedVersion: 1,
        cod: { amountCents: 1, reference },
        randomUuid: () => operationId,
      }),
    ).toThrow();
    const transition = createDriverOfflineOperation({
      partitionKey,
      orderId,
      kind: "START_DELIVERY",
      expectedVersion: 1,
      randomUuid: () => operationId,
    });
    expect(() =>
      parseDriverOfflineOperation({ ...transition, cod: codOperation().cod }),
    ).toThrow();
    expect(() => parseDriverOfflineOperation({ ...codOperation(), cod: null })).toThrow();
  });

  it.each([
    ["zero amount", { amountCents: 0 }],
    ["fractional amount", { amountCents: 12.5 }],
    ["unsafe amount", { amountCents: Number.MAX_SAFE_INTEGER + 1 }],
    ["blank reference", { reference: " " }],
    ["reference with surrounding space", { reference: ` ${reference}` }],
    ["reference over 200 characters", { reference: "R".repeat(201) }],
    ["foreign key", { idempotencyKey: `drv2-${orderId}-cod` }],
  ])("fails closed on a persisted %s", (_name, patch) => {
    const operation = codOperation();
    expect(() =>
      parseDriverOfflineOperation({ ...operation, cod: { ...operation.cod, ...patch } }),
    ).toThrow();
  });

  it("keeps only known rule codes", () => {
    const operation = codOperation();
    for (const code of ["COD_AMOUNT_MISMATCH", "COD_NOT_RECORDED", "VERSION_CONFLICT"]) {
      expect(
        parseDriverOfflineOperation({ ...operation, rejectionCode: code }).rejectionCode,
      ).toBe(code);
    }
    for (const code of ["", "cod_amount_mismatch", "SOMETHING_ELSE", 7]) {
      expect(() => parseDriverOfflineOperation({ ...operation, rejectionCode: code })).toThrow();
    }
  });
});

describe("IndexedDB v3 operation migration", () => {
  it("turns a queued schema 1 operation into schema 2 without losing anything", () => {
    const current = createDriverOfflineOperation({
      partitionKey,
      orderId,
      kind: "DELIVERY_PROOF",
      expectedVersion: 4,
      proof: { contentType: "image/jpeg", sizeBytes: 9, sha256: "b".repeat(64) },
      now: () => new Date("2026-10-11T19:00:00.000Z"),
      randomUuid: () => operationId,
    });
    const legacy: Record<string, unknown> = {
      ...current,
      schemaVersion: 1,
      status: "RETRY_WAIT",
      attemptCount: 2,
    };
    delete legacy.cod;
    delete legacy.rejectionCode;

    const migrated = migrateDriverOfflineOperationRecord(legacy);

    expect(migrated).toEqual({ ...legacy, schemaVersion: 2, cod: null, rejectionCode: null });
    expect(parseDriverOfflineOperation(migrated, partitionKey)).toMatchObject({
      id: operationId,
      kind: "DELIVERY_PROOF",
      status: "RETRY_WAIT",
      attemptCount: 2,
      transitionIdempotencyKey: transitionIdempotencyKey(operationId),
      sha256: "b".repeat(64),
    });
  });

  it("leaves schema 2 records and anything unknown untouched", () => {
    const current = codOperation();
    expect(migrateDriverOfflineOperationRecord(current)).toBe(current);
    for (const value of [null, 7, "x", [], { schemaVersion: 3 }]) {
      expect(migrateDriverOfflineOperationRecord(value)).toBe(value);
    }
  });
});

describe("cash reference (guía y hora)", () => {
  it("writes the tracking number and the phone's local time with its offset", () => {
    const at = new Date("2026-10-11T20:05:42.789Z");
    expect(formatLocalTimestamp(at.getTime(), -420)).toBe("2026-10-11T13:05:42-07:00");
    expect(formatLocalTimestamp(at.getTime(), 330)).toBe("2026-10-12T01:35:42+05:30");
    expect(formatLocalTimestamp(at.getTime(), 0)).toBe("2026-10-11T20:05:42+00:00");
    const built = buildCodReference(" ORD_abcdefghijklmnopqrstuv ", at);
    expect(built).toMatch(
      /^ORD_abcdefghijklmnopqrstuv \d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2}$/,
    );
    expect(built.length).toBeLessThanOrEqual(200);
    expect(() => formatLocalTimestamp(at.getTime(), 15 * 60)).toThrow();
  });
});
