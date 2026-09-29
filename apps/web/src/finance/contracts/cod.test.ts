import { describe, expect, it } from "vitest";
import { ContractViolationError } from "../../operations/contracts/strict-json";
import {
  codId,
  codTransactionResponse,
  financialsResponse,
  orderId,
  pendingOrderResponse,
  syntheticUuid,
} from "../../operations/contracts/ui-001-screens.fixtures";
import {
  buildRecordCodBody,
  canRecordCollection,
  formatBasisPoints,
  maximumOrderPageSize,
  parseCodTransaction,
  parseOrderFinancials,
  parsePendingCodPage,
  reconcilableRecord,
} from "./cod";

describe("OrderFinancials parser", () => {
  it("accepts the AI-05 shape with integer cents", () => {
    const financials = parseOrderFinancials(financialsResponse());
    expect(financials.margin_cents).toBe(5_500);
    expect(financials.cod).toMatchObject({ expected_cents: 25_050, status: null, amount_cents: null });
    expect(canRecordCollection(financials)).toBe(true);
  });

  it("accepts a recorded collection with its amount", () => {
    const financials = parseOrderFinancials(
      financialsResponse({}, { status: "RECORDED", amount_cents: 25_050, recorded: true, satisfies_delivery_requirement: true }),
    );
    expect(financials.cod.amount_cents).toBe(25_050);
    expect(canRecordCollection(financials)).toBe(false);
  });

  it("accepts a zero-revenue order with null basis points", () => {
    const financials = parseOrderFinancials(
      financialsResponse({ revenue_cents: 0, margin_cents: -4_500, margin_basis_points: null }),
    );
    expect(financials.margin_basis_points).toBeNull();
  });

  it("does not offer recording when no COD is expected", () => {
    expect(canRecordCollection(parseOrderFinancials(financialsResponse({}, { expected_cents: 0 })))).toBe(false);
  });

  it.each([
    ["an extra key", financialsResponse({ extra: 1 })],
    ["another currency", financialsResponse({ currency: "USD" })],
    ["a fractional amount", financialsResponse({ revenue_cents: 100.5 })],
    ["a numeric string amount", financialsResponse({ cost_cents: "4500" })],
    ["a cost that is not the sum of modalities", financialsResponse({ cost_cents: 4_000, margin_cents: 6_000 })],
    ["a margin that is not revenue minus cost", financialsResponse({ margin_cents: 5_000 })],
    ["null basis points with revenue", financialsResponse({ margin_basis_points: null })],
    [
      "modalities out of order",
      financialsResponse({
        cost_by_modality: [
          { modality: "EXTERNAL", cost_cents: 0, assignment_count: 0 },
          { modality: "OWN", cost_cents: 4_500, assignment_count: 1 },
          { modality: "ALLY_CAPACITY", cost_cents: 0, assignment_count: 0 },
        ],
      }),
    ],
    ["a status without amount", financialsResponse({}, { status: "RECORDED", recorded: true })],
    ["an amount without status", financialsResponse({}, { amount_cents: 25_050 })],
    ["reconciled but not recorded", financialsResponse({}, { status: "RECONCILED", amount_cents: 1, reconciled: true })],
    ["an unknown order status", financialsResponse({ order_status: "LOST" })],
  ])("fails closed on %s", (_label, body) => {
    expect(() => parseOrderFinancials(body)).toThrow(ContractViolationError);
  });
});

describe("OrderFinancials cod_record (API-FIN-COD-VISIBILITY-2026-09-29)", () => {
  const recordedPosition = { status: "RECORDED", amount_cents: 25_050, recorded: true, satisfies_delivery_requirement: true };

  it("is null while nothing is collected", () => {
    const financials = parseOrderFinancials(financialsResponse());
    expect(financials.cod_record).toBeNull();
    expect(reconcilableRecord(financials)).toBeNull();
  });

  it("carries the record id so a RECORDED collection is reconcilable without typing it", () => {
    const financials = parseOrderFinancials(financialsResponse({}, recordedPosition));
    expect(financials.cod_record).toMatchObject({ id: codId, order_id: orderId, amount_cents: 25_050, status: "RECORDED" });
    expect(reconcilableRecord(financials)?.id).toBe(codId);
  });

  it("does not offer a RECONCILED record for reconciliation", () => {
    const financials = parseOrderFinancials(
      financialsResponse({}, { status: "RECONCILED", amount_cents: 25_050, recorded: true, reconciled: true }),
    );
    expect(financials.cod_record?.status).toBe("RECONCILED");
    expect(reconcilableRecord(financials)).toBeNull();
  });

  it.each([
    ["a missing cod_record", (() => { const body = financialsResponse(); delete body.cod_record; return body; })()],
    ["a record without a COD status", financialsResponse({ cod_record: codTransactionResponse() })],
    ["a COD status without a record", financialsResponse({ cod_record: null }, recordedPosition)],
    [
      "a record of another order",
      financialsResponse({ cod_record: codTransactionResponse({ order_id: syntheticUuid(0x999) }) }, recordedPosition),
    ],
    [
      "a record whose status differs from the position",
      financialsResponse(
        { cod_record: codTransactionResponse({ status: "RECONCILED", reconciled_at: "2026-09-28T18:00:00Z" }) },
        recordedPosition,
      ),
    ],
    [
      "a record whose amount differs from the position",
      financialsResponse({ cod_record: codTransactionResponse({ amount_cents: 25_049 }) }, recordedPosition),
    ],
    [
      "a record carrying the reference",
      financialsResponse({ cod_record: codTransactionResponse({ reference: "Recibo 17" }) }, recordedPosition),
    ],
    [
      "a record carrying the collecting driver",
      financialsResponse(
        { cod_record: codTransactionResponse({ collected_by_driver_id: syntheticUuid(0x888) }) },
        recordedPosition,
      ),
    ],
  ])("fails closed on %s", (_label, body) => {
    expect(() => parseOrderFinancials(body)).toThrow(ContractViolationError);
  });
});

describe("COD pending list parser", () => {
  it("keeps only the id, public id and status of each AI-05 Order", () => {
    const page = parsePendingCodPage({
      items: [pendingOrderResponse(orderId, "PQ-000123")],
      next_cursor: "cursor-2",
    });
    expect(page).toEqual({
      items: [{ id: orderId, public_id: "PQ-000123", status: "DELIVERED" }],
      next_cursor: "cursor-2",
    });
  });

  it.each([
    ["an extra page key", { items: [], next_cursor: null, total: 0 }],
    ["an order with an unknown key", { items: [{ ...pendingOrderResponse(orderId, "PQ-1"), cod: {} }], next_cursor: null }],
    ["an order without an id", { items: [{ ...pendingOrderResponse(orderId, "PQ-1"), id: "x" }], next_cursor: null }],
    ["an empty cursor", { items: [], next_cursor: "" }],
    [
      "more orders than a page holds",
      { items: Array.from({ length: maximumOrderPageSize + 1 }, () => pendingOrderResponse(orderId, "PQ-1")), next_cursor: null },
    ],
  ])("fails closed on %s", (_label, body) => {
    expect(() => parsePendingCodPage(body)).toThrow(ContractViolationError);
  });
});

describe("CodTransaction parser", () => {
  it("accepts a recorded collection", () => {
    expect(parseCodTransaction(codTransactionResponse()).status).toBe("RECORDED");
  });

  it.each([
    ["recorded without timestamp", codTransactionResponse({ recorded_at: null })],
    ["reconciled without timestamp", codTransactionResponse({ status: "RECONCILED" })],
    ["a float amount", codTransactionResponse({ amount_cents: 250.5 })],
    ["an extra key such as the reference", codTransactionResponse({ reference: "R-1" })],
  ])("fails closed on %s", (_label, body) => {
    expect(() => parseCodTransaction(body)).toThrow(ContractViolationError);
  });
});

describe("recordCodCollection request", () => {
  const financials = parseOrderFinancials(financialsResponse());

  it("parses the typed amount to exact integer cents", () => {
    expect(buildRecordCodBody(financials, "250.50", "Recibo 17")).toEqual({
      ok: true,
      body: { amount_cents: 25_050, reference: "Recibo 17" },
    });
    expect(buildRecordCodBody(financials, "250.5", "Recibo 17").ok).toBe(true);
  });

  it.each([
    ["a different amount", "250.49", "Recibo 17"],
    ["three decimals", "250.505", "Recibo 17"],
    ["a negative amount", "-250.50", "Recibo 17"],
    ["an exponent", "2.505e2", "Recibo 17"],
    ["a blank reference", "250.50", ""],
    ["a reference with surrounding space", "250.50", " Recibo"],
    ["a reference over 200 characters", "250.50", "r".repeat(201)],
  ])("refuses %s", (_label, amount, reference) => {
    expect(buildRecordCodBody(financials, amount, reference).ok).toBe(false);
  });
});

describe("basis points", () => {
  it("formats with integer arithmetic", () => {
    expect(formatBasisPoints(5_500)).toBe("55.00 %");
    expect(formatBasisPoints(-1_234)).toBe("-12.34 %");
    expect(formatBasisPoints(5)).toBe("0.05 %");
  });
});
