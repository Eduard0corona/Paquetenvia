import { describe, expect, it } from "vitest";
import { acceptanceVersionsUnavailableMessage } from "./acceptance-versions";
import {
  buildCreateOrderBody,
  buildCreateQuoteBody,
  codAmountAboveCapMessage,
  codExpectedCents,
  confirmationBlockerLabels,
  CreateOrderContractError,
  evaluateConfirmation,
  lowPriceAuthorizationReasonMaximum,
  lowPriceGuardTotalCents,
  normalizeMexicanPhone,
  parseCreatedOrder,
  parseQuote,
  restrictedGoodsRequiredMessage,
} from "./create-order";
import { maximumCodExpectedCents } from "./money";
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

  // ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02: 10 Mexican digits; only spaces and hyphens are removed.
  it.each([
    ["6671234567", "6671234567"],
    ["667 123 4567", "6671234567"],
    ["667-123-4567", "6671234567"],
    [" 667 - 123 - 4567 ", "6671234567"],
  ])("sends the phone %s normalized as %s", (typed, normalized) => {
    expect(normalizeMexicanPhone(typed)).toBe(normalized);
    const result = buildCreateQuoteBody(draft({ origin: { ...draft().origin, phone: typed } }));
    expect(result.ok && result.body.origin.phone).toBe(normalized);
  });

  it.each([
    "+526671234567",
    "+52 667 123 4567",
    "526671234567",
    "667123456",
    "66712345678",
    "(667) 123 4567",
    "667.123.4567",
    "667\t123\t4567",
    "６６７１２３４５６７",
    "667123456a",
    "-".repeat(23) + "6671234567",
  ])("refuses the phone %j without echoing it", (typed) => {
    expect(normalizeMexicanPhone(typed)).toBeNull();
    const result = buildCreateQuoteBody(draft({ destination: { ...draft().destination, phone: typed } }));
    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(result.errors).toEqual([
      "El teléfono de destino debe tener 10 dígitos de México, sin +52; puedes separarlos con espacios o guiones.",
    ]);
    expect(result.errors.join(" ")).not.toContain(typed);
  });

  it.each([["91", "0"], ["0", "-181"], ["1e1", "0"], ["", "0"]])(
    "refuses out-of-range coordinates %s,%s",
    (lat, lng) => {
      expect(buildCreateQuoteBody(draft({ origin: { ...draft().origin, lat, lng } })).ok).toBe(false);
    },
  );
});

describe("createOrder request", () => {
  const acceptance = { payerType: "SENDER", accepted: true, restrictedGoodsAcknowledged: true };
  const versions = { termsVersion: "terms-2026.09", privacyVersion: "privacy_v3" };

  it("builds the AI-05 CreateOrderRequest with the observed acceptance time", () => {
    const result = buildCreateOrderBody(quoteId, acceptance, versions, new Date("2026-09-28T17:00:00Z"));
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
        restricted_goods_acknowledged: true,
      },
    });
  });

  it.each([{ payerType: "" }, { accepted: false }, { restrictedGoodsAcknowledged: false }])(
    "refuses an incomplete acceptance %o",
    (change) => {
      expect(buildCreateOrderBody(quoteId, { ...acceptance, ...change }, versions, new Date()).ok).toBe(false);
    },
  );

  it("names the missing prohibited-goods confirmation (ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02)", () => {
    expect(
      buildCreateOrderBody(quoteId, { ...acceptance, restrictedGoodsAcknowledged: false }, versions, new Date()),
    ).toEqual({ ok: false, errors: [restrictedGoodsRequiredMessage] });
  });

  it.each([
    ["150.50", 15_050],
    ["150.5", 15_050],
    ["150", 15_000],
    ["0.01", 1],
    [" 19999.99 ", 1_999_999],
    ["20000", 2_000_000],
    ["20000.00", 2_000_000],
    ["0.1", 10],
    ["0.29", 29],
  ])("sends the typed COD %s as exact integer cents (D6-COD-EXPECTED)", (typed, cents) => {
    const result = buildCreateOrderBody(quoteId, { ...acceptance, codAmount: typed }, versions, new Date());
    expect(result.ok && result.body.cod_expected_cents).toBe(cents);
    expect(Number.isSafeInteger(cents)).toBe(true);
  });

  it.each(["20000.01", "20001", "1234567.89", "9999999999999"])(
    "refuses a COD above the 20,000 MXN cap (COD-CAP-20000-2026-10-02): %s",
    (typed) => {
      const result = buildCreateOrderBody(quoteId, { ...acceptance, codAmount: typed }, versions, new Date());
      expect(result.ok).toBe(false);
      expect(!result.ok && result.errors).toContain(codAmountAboveCapMessage);
      expect(codAmountAboveCapMessage).toBe("El cobro contra entrega no puede superar $20,000.00 MXN por pedido.");
      expect(maximumCodExpectedCents).toBe(2_000_000);
    },
  );

  it.each(["", "   ", "0", "0.00", undefined])("sends no COD field for %o", (typed) => {
    const result = buildCreateOrderBody(quoteId, { ...acceptance, codAmount: typed }, versions, new Date());
    expect(result.ok).toBe(true);
    expect(result.ok && "cod_expected_cents" in result.body).toBe(false);
  });

  it.each(["-1", "+1", "1,500", "1 500", "150.505", "$150", "1e3", "150,50", "abc", "150.", ".5", "99999999999999"])(
    "refuses the COD %s instead of reinterpreting it",
    (typed) => {
      const result = buildCreateOrderBody(quoteId, { ...acceptance, codAmount: typed }, versions, new Date());
      expect(result.ok).toBe(false);
      expect(!result.ok && result.errors.join(" ")).toContain("cobro contra entrega");
      expect(codExpectedCents(typed)).toBeNull();
    },
  );

  it("always sends the ASSISTED channel for the operator-assisted flow", () => {
    const tampered = { ...acceptance, acceptanceChannel: "WEB" };
    const result = buildCreateOrderBody(quoteId, tampered, versions, new Date());
    expect(result.ok && result.body.acceptance.acceptance_channel).toBe("ASSISTED");
  });

  it.each([
    null,
    { termsVersion: "", privacyVersion: "privacy_v3" },
    { termsVersion: "v 1", privacyVersion: "privacy_v3" },
    { termsVersion: "terms-2026.09", privacyVersion: "x".repeat(65) },
    { termsVersion: "OWNER_DECISION_REQUIRED", privacyVersion: "privacy_v3" },
  ])("blocks the order without valid configured versions %o", (configured) => {
    expect(buildCreateOrderBody(quoteId, acceptance, configured, new Date())).toEqual({
      ok: false,
      errors: [acceptanceVersionsUnavailableMessage],
    });
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

  // GATE-011-VAT-INCLUDED-2026-09-29: "52 con IVA incluido" — the guard reads the VAT-included total.
  const vatIncluded = (net: number, tax: number) => quoteResponse({
    net: { currency: "MXN", amount_cents: net },
    tax: { currency: "MXN", amount_cents: tax },
    total: { currency: "MXN", amount_cents: net + tax },
  });

  it("blocks at 52 MXN total, IVA included, or less unless the quote is a consolidated route", () => {
    const at52 = vatIncluded(4_483, 717);
    const at45 = vatIncluded(3_879, 621);
    const at5201 = vatIncluded(4_484, 717);
    expect(evaluateConfirmation(parseQuote(at52), now)).toEqual(["low_price"]);
    expect(evaluateConfirmation(parseQuote(at45), now)).toEqual(["low_price"]);
    expect(evaluateConfirmation(parseQuote(at5201), now)).toEqual([]);
    expect(evaluateConfirmation(parseQuote({ ...at52, consolidated_route: true }), now)).toEqual([]);
  });

  it("never applies the guard to the pre-tax net", () => {
    // 60.32 MXN total is 52.00 net + 8.32 IVA: above the guard, because the customer pays 60.32.
    expect(evaluateConfirmation(parseQuote(vatIncluded(5_200, 832)), now)).toEqual([]);
    expect(lowPriceGuardTotalCents).toBe(5_200);
    expect(confirmationBlockerLabels.low_price).toContain("IVA incluido");
  });
});

describe("quote and order parsers", () => {
  it("keeps the displayed fields and drops the redacted request snapshot", () => {
    const quote = parseQuote(quoteResponse());
    expect(quote.net.amount_cents).toBe(8_000);
    expect(quote.breakdown).toEqual([{ line_type: "BASE_TARIFF", amount_cents: 9_280 }]);
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

describe("low price authorization (LOW-PRICE-MANUAL-AUTH-2026-10-02)", () => {
  const now = new Date("2026-09-28T17:00:00Z");
  const at52 = quoteResponse({
    net: { currency: "MXN", amount_cents: 4_483 },
    tax: { currency: "MXN", amount_cents: 717 },
    total: { currency: "MXN", amount_cents: 5_200 },
    pricing_tier: "BUSINESS_200_499",
  });
  const actorId = "99999999-9999-4999-8999-999999999999";

  it("sends the trimmed reason only when the option is checked", () => {
    const authorized = buildCreateQuoteBody(
      draft({ authorizeLowPrice: true, lowPriceReason: "  Cliente ancla, ruta en consolidación  " }),
    );
    expect(authorized.ok).toBe(true);
    if (!authorized.ok) return;
    expect(authorized.body.low_price_authorization).toEqual({ reason: "Cliente ancla, ruta en consolidación" });

    const unchecked = buildCreateQuoteBody(draft({ authorizeLowPrice: false, lowPriceReason: "Motivo" }));
    expect(unchecked.ok).toBe(true);
    if (!unchecked.ok) return;
    expect(unchecked.body).not.toHaveProperty("low_price_authorization");
    const plain = buildCreateQuoteBody(draft());
    expect(plain.ok && plain.body).not.toHaveProperty("low_price_authorization");
  });

  it.each([
    ["an empty reason", "   "],
    ["a reason above 200 characters", "a".repeat(201)],
    ["a multi-line reason", "línea uno\nlínea dos"],
  ])("refuses %s before calling the API", (_label, reason) => {
    const result = buildCreateQuoteBody(draft({ authorizeLowPrice: true, lowPriceReason: reason }));
    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(result.errors.join(" ")).toContain("autorización");
    expect(result.errors.join(" ")).not.toContain(reason.trim() === "" ? "\u0000" : reason);
  });

  it("accepts exactly 200 characters after trimming", () => {
    expect(lowPriceAuthorizationReasonMaximum).toBe(200);
    expect(buildCreateQuoteBody(draft({ authorizeLowPrice: true, lowPriceReason: ` ${"a".repeat(200)} ` })).ok).toBe(true);
  });

  it("lets an authorized quote of 52 MXN or less pass the guard and keeps blocking one without it", () => {
    expect(evaluateConfirmation(parseQuote(at52), now)).toEqual(["low_price"]);
    const authorized = parseQuote({
      ...at52,
      low_price_authorization: {
        valid_until: "2026-09-28T18:00:00+00:00",
        actor_id: actorId,
        reason: "Cliente ancla",
      },
    });
    expect(authorized.low_price_authorization).toEqual({
      valid_until: "2026-09-28T18:00:00+00:00",
      actor_id: actorId,
      reason: "Cliente ancla",
    });
    expect(evaluateConfirmation(authorized, now)).toEqual([]);
    // The authorization never outlives the quote: an expired authorized quote is still blocked.
    expect(evaluateConfirmation(parseQuote({ ...at52, expires_at: "2026-09-28T17:00:00Z", low_price_authorization: { valid_until: "2026-09-28T17:00:00Z" } }), now)).toEqual(["expired"]);
  });

  it("reads the withheld actor and reason as null and a missing or null field as no authorization", () => {
    const withheld = parseQuote({ ...at52, low_price_authorization: { valid_until: "2026-09-28T18:00:00Z" } });
    expect(withheld.low_price_authorization).toEqual({
      valid_until: "2026-09-28T18:00:00Z",
      actor_id: null,
      reason: null,
    });
    expect(evaluateConfirmation(withheld, now)).toEqual([]);
    expect(parseQuote({ ...at52, low_price_authorization: null }).low_price_authorization).toBeNull();
    expect(parseQuote(quoteResponse()).low_price_authorization).toBeNull();
  });

  it.each([
    ["an unknown property", { valid_until: "2026-09-28T18:00:00Z", financial_override: {} }],
    ["a missing validity", { reason: "Cliente ancla" }],
    ["a local validity", { valid_until: "2026-09-28T18:00:00" }],
    ["a malformed actor", { valid_until: "2026-09-28T18:00:00Z", actor_id: "not-a-uuid" }],
    ["an oversized reason", { valid_until: "2026-09-28T18:00:00Z", reason: "a".repeat(201) }],
    ["a non-object", "AUTHORIZED"],
  ])("fails closed on %s", (_label, value) => {
    expect(() => parseQuote({ ...at52, low_price_authorization: value })).toThrow(CreateOrderContractError);
  });
});
