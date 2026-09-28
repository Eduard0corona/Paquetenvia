import { describe, expect, it } from "vitest";
import {
  buildCreateOrderBody,
  buildCreateQuoteBody,
  CreateOrderContractError,
  evaluateConfirmation,
  parseCreatedOrder,
  parseQuote,
} from "./create-order";
import { draft, orderResponse, quoteId, quoteResponse } from "./create-order.fixtures";

const orderId = "ffffffff-ffff-4fff-8fff-ffffffffffff";

describe("createQuote request", () => {
  it("builds the AI-05 CreateQuoteRequest with integer cents", () => {
    const result = buildCreateQuoteBody(draft());
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.body.packages).toEqual([
      { description: "Caja", weight_grams: 500, declared_value_cents: 10_050, width_mm: 300 },
    ]);
    expect(result.body.origin.lat).toBe(24.8091);
    expect(result.body.origin).not.toHaveProperty("references");
    expect(result.body).not.toHaveProperty("client_account_id");
    expect(result.body.service_type).toBe("SAME_DAY");
  });

  it("enforces AI-05 package limits (1 to 20)", () => {
    const one = draft().packages[0];
    expect(buildCreateQuoteBody(draft({ packages: [] })).ok).toBe(false);
    expect(buildCreateQuoteBody(draft({ packages: Array(20).fill(one) })).ok).toBe(true);
    expect(buildCreateQuoteBody(draft({ packages: Array(21).fill(one) })).ok).toBe(false);
  });

  it("reports field errors without echoing personal data", () => {
    const result = buildCreateQuoteBody(
      draft({
        origin: { ...draft().origin, addressText: "corta", phone: " ", lat: "abc" },
        packages: [{ ...draft().packages[0], weightGrams: "0", declaredValue: "1.234" }],
        serviceType: "TELEPORT",
        clientAccountId: "not-a-uuid",
      }),
    );
    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(result.errors.length).toBeGreaterThanOrEqual(6);
    expect(result.errors.join(" ")).not.toContain("6670000000");
    expect(result.errors.join(" ")).not.toContain("corta");
  });

  it.each([["91", "0"], ["0", "-181"], ["1e1", "0"], ["", "0"]])(
    "refuses out-of-range coordinates %s,%s",
    (lat, lng) => {
      expect(buildCreateQuoteBody(draft({ origin: { ...draft().origin, lat, lng } })).ok).toBe(false);
    },
  );
});

describe("createOrder request", () => {
  const acceptance = {
    payerType: "SENDER",
    termsVersion: "terms-2026.09",
    privacyVersion: "privacy_v3",
    acceptanceChannel: "ASSISTED",
    accepted: true,
  };

  it("builds the AI-05 CreateOrderRequest with the observed acceptance time", () => {
    const result = buildCreateOrderBody(quoteId, acceptance, new Date("2026-09-28T17:00:00Z"));
    expect(result).toEqual({
      ok: true,
      body: {
        quote_id: quoteId,
        payer_type: "SENDER",
        acceptance: {
          terms_version: "terms-2026.09",
          privacy_version: "privacy_v3",
          accepted_at: "2026-09-28T17:00:00.000Z",
          acceptance_channel: "ASSISTED",
        },
      },
    });
  });

  it.each([
    { payerType: "" },
    { termsVersion: "" },
    { termsVersion: "v 1" },
    { privacyVersion: "x".repeat(65) },
    { acceptanceChannel: "PHONE" },
    { accepted: false },
  ])("refuses an incomplete acceptance %o", (change) => {
    expect(buildCreateOrderBody(quoteId, { ...acceptance, ...change }, new Date()).ok).toBe(false);
  });
});

describe("confirmation guard (AI-07 create_order)", () => {
  const now = new Date("2026-09-28T17:00:00Z");

  it("allows an active, unexpired quote above the low price guard", () => {
    expect(evaluateConfirmation(parseQuote(quoteResponse()), now)).toEqual([]);
  });

  it("blocks an expired or used quote", () => {
    expect(evaluateConfirmation(parseQuote(quoteResponse({ expires_at: "2026-09-28T17:00:00Z" })), now)).toEqual(["expired"]);
    expect(evaluateConfirmation(parseQuote(quoteResponse({ status: "USED" })), now)).toEqual(["inactive"]);
  });

  it("blocks at 52 MXN net or less unless the quote is a consolidated route", () => {
    const at52 = quoteResponse({ net: { currency: "MXN", amount_cents: 5_200 } });
    const at45 = quoteResponse({ net: { currency: "MXN", amount_cents: 4_500 } });
    const at5201 = quoteResponse({ net: { currency: "MXN", amount_cents: 5_201 } });
    expect(evaluateConfirmation(parseQuote(at52), now)).toEqual(["low_price"]);
    expect(evaluateConfirmation(parseQuote(at45), now)).toEqual(["low_price"]);
    expect(evaluateConfirmation(parseQuote(at5201), now)).toEqual([]);
    expect(evaluateConfirmation(parseQuote({ ...at52, consolidated_route: true }), now)).toEqual([]);
  });
});

describe("quote and order parsers", () => {
  it("keeps the displayed fields and drops the redacted request snapshot", () => {
    const quote = parseQuote(quoteResponse());
    expect(quote.net.amount_cents).toBe(8_000);
    expect(quote.breakdown).toEqual([{ line_type: "BASE_TARIFF", amount_cents: 8_000 }]);
    expect(quote.package_count).toBe(1);
    expect(quote).not.toHaveProperty("request_snapshot_redacted");
    expect(quote).not.toHaveProperty("package_snapshot");
  });

  it("accepts the optional AI-05 quote fields being absent", () => {
    const minimal = quoteResponse();
    delete minimal.breakdown;
    delete minimal.service_area_id;
    delete minimal.request_snapshot_redacted;
    expect(parseQuote(minimal).breakdown).toEqual([]);
  });

  it.each([
    ["an unknown property", { internal_cost_cents: 1 }],
    ["floating money", { net: { currency: "MXN", amount_cents: 80.5 } }],
    ["negative money", { total: { currency: "MXN", amount_cents: -1 } }],
    ["another currency", { tax: { currency: "USD", amount_cents: 1 } }],
    ["an unknown status", { status: "PENDING" }],
    ["an unsafe breakdown amount", { breakdown: [{ line_type: "BASE_TARIFF", amount_cents: 2 ** 60 }] }],
    ["an empty package snapshot", { package_snapshot: [] }],
    ["a local timestamp", { expires_at: "2026-09-28T18:00:00" }],
  ])("fails closed on %s", (_label, change) => {
    expect(() => parseQuote(quoteResponse(change))).toThrow(CreateOrderContractError);
  });

  it("parses the created order and fails closed on drift", () => {
    expect(parseCreatedOrder(orderResponse())).toMatchObject({
      id: orderId,
      public_id: "PQ-000123",
      total: { currency: "MXN", amount_cents: 9_280 },
    });
    expect(() => parseCreatedOrder(orderResponse({ version: 0 }))).toThrow(CreateOrderContractError);
    expect(() => parseCreatedOrder(orderResponse({ driver_phone: "x" }))).toThrow(CreateOrderContractError);
  });
});
