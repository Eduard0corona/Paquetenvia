import { describe, expect, it } from "vitest";
import {
  actionsForStatus,
  buildSettlementSearch,
  isValidReason,
  parseSettlement,
  parseSettlementPage,
  SettlementContractError,
  validateCreateSettlement,
} from "./settlement";
import {
  adjustmentLine,
  driverId,
  settlementLine,
  settlementResponse,
} from "./settlement.fixtures";

describe("settlement parser", () => {
  it("keeps the persisted header and immutable lines exactly", () => {
    const settlement = parseSettlement(settlementResponse());
    expect(settlement.total_cents).toBe(4_000);
    expect(settlement.lines.map((line) => line.amount_cents)).toEqual([4_500, -500]);
    expect(settlement.lines[1]).toMatchObject({ line_type: "ADJUSTMENT", order_id: null });
  });

  it("accepts an empty settlement with total 0", () => {
    expect(parseSettlement(settlementResponse({ lines: [], total_cents: 0 })).lines).toEqual([]);
  });

  it.each([
    ["a total that is not the exact sum of its lines", { total_cents: 4_001 }],
    ["a fractional total", { total_cents: 4_000.5, lines: [settlementLine({ amount_cents: 4_000.5 })] }],
    ["an unsafe integer", { total_cents: 2 ** 60, lines: [settlementLine({ amount_cents: 2 ** 60 })] }],
    ["another payee type", { payee_type: "ALLY" }],
    ["an unknown status", { status: "SETTLED" }],
    ["an extra property", { payee_name: "x" }],
    ["an impossible date", { period_to: "2026-02-30" }],
    ["a period that ends before it starts", { period_from: "2026-09-27", period_to: "2026-09-26" }],
    ["an ADJUSTMENT with an order", { lines: [adjustmentLine(4_000)].map((line) => ({ ...line, order_id: "0d0e0000-0000-4000-8000-000000000003" })) }],
    ["a DELIVERY without an order", { lines: [settlementLine({ order_id: null, amount_cents: 4_000 })] }],
    ["an unexpected source reference", { lines: [settlementLine({ amount_cents: 4_000, source_reference: "orders/1" })] }],
  ])("fails closed on %s", (_label, change) => {
    expect(() => parseSettlement(settlementResponse(change))).toThrow(SettlementContractError);
  });

  it("parses a cursor page and its end", () => {
    expect(parseSettlementPage({ items: [settlementResponse()], next_cursor: "opaque" }).next_cursor).toBe("opaque");
    expect(parseSettlementPage({ items: [], next_cursor: null })).toEqual({ items: [], next_cursor: null });
    expect(() => parseSettlementPage({ items: [] })).toThrow(SettlementContractError);
    expect(() => parseSettlementPage({ items: [], next_cursor: "x".repeat(129) })).toThrow(SettlementContractError);
  });
});

describe("listSettlements query", () => {
  it("sends only the AI-05 filters, each once", () => {
    const search = buildSettlementSearch({
      payeeId: driverId,
      status: "APPROVED",
      periodFrom: "2026-09-01",
      periodTo: "2026-09-30",
      cursor: "opaque",
    });
    expect(search?.toString()).toBe(
      `payee_id=${driverId}&status=APPROVED&period_from=2026-09-01&period_to=2026-09-30&cursor=opaque`,
    );
    expect(buildSettlementSearch({})?.toString()).toBe("");
  });

  it.each([
    { payeeId: "not-a-uuid" },
    { status: "SETTLED" },
    { periodFrom: "2026-13-01" },
    { periodFrom: "2026-09-30", periodTo: "2026-09-01" },
    { cursor: "x".repeat(129) },
  ])("refuses %o before any request", (filters) => {
    expect(buildSettlementSearch(filters)).toBeNull();
  });
});

describe("settlement requests", () => {
  it("validates createSettlement periods", () => {
    const valid = { driver_id: driverId, period_from: "2026-01-01", period_to: "2026-12-31" };
    expect(validateCreateSettlement(valid)).toEqual([]);
    expect(validateCreateSettlement({ ...valid, period_to: "2027-01-01" })).toHaveLength(0);
    expect(validateCreateSettlement({ ...valid, period_to: "2027-01-02" })).toHaveLength(1);
    expect(validateCreateSettlement({ ...valid, period_to: "2025-12-31" })).toHaveLength(1);
    expect(validateCreateSettlement({ ...valid, driver_id: "x" })).toHaveLength(1);
  });

  it.each([
    ["Bono por lluvia", true],
    ["x".repeat(500), true],
    ["", false],
    [" leading", false],
    ["trailing ", false],
    ["line\nbreak", false],
    ["x".repeat(501), false],
  ])("reason %j is valid: %s", (reason, expected) => {
    expect(isValidReason(reason)).toBe(expected);
  });

  it("follows the SET-001 state machine", () => {
    expect(actionsForStatus("DRAFT")).toEqual(["void", "export"]);
    expect(actionsForStatus("CALCULATED")).toEqual(["adjust", "approve", "void", "export"]);
    expect(actionsForStatus("APPROVED")).toEqual(["pay", "void", "export"]);
    expect(actionsForStatus("PAID")).toEqual(["export"]);
    expect(actionsForStatus("VOID")).toEqual(["export"]);
  });
});
