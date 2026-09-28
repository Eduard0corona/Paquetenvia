import type { QuoteDraft } from "./create-order";

/** Synthetic AI-05 payloads for tests; no real person, address or phone. */
export const quoteId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const ruleId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
const locationId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
const cityId = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
const orgId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";
const orderId = "ffffffff-ffff-4fff-8fff-ffffffffffff";

export function quoteResponse(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: quoteId,
    net: { currency: "MXN", amount_cents: 8_000 },
    tax: { currency: "MXN", amount_cents: 1_280 },
    total: { currency: "MXN", amount_cents: 9_280 },
    rule_ids: [ruleId],
    breakdown: [
      { line_type: "BASE_TARIFF", rule_id: ruleId, amount_cents: 8_000, pricing_tier: "OCCASIONAL", tax_mode: "PLUS_VAT" },
    ],
    expires_at: "2026-09-28T18:00:00+00:00",
    origin_location_id: locationId,
    destination_location_id: locationId,
    service_type: "SAME_DAY",
    consolidated_route: false,
    package_snapshot: [{ description: "Caja", weight_grams: 500, declared_value_cents: 10_000 }],
    city_id: cityId,
    service_area_id: null,
    pricing_tier: "OCCASIONAL",
    minimum_total_cents_snapshot: 5_200,
    pricing_policy_version: "prc-001-v1",
    status: "ACTIVE",
    request_snapshot_redacted: { service_type: "SAME_DAY" },
    ...overrides,
  };
}

export function orderResponse(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: orderId,
    public_id: "PQ-000123",
    owner_org_id: orgId,
    operator_org_id: null,
    status: "CONFIRMED",
    price_net: { currency: "MXN", amount_cents: 8_000 },
    version: 1,
    origin_location_id: locationId,
    destination_location_id: locationId,
    service_type: "SAME_DAY",
    quote_id: quoteId,
    city_id: cityId,
    service_area_id: null,
    pricing_tier: "OCCASIONAL",
    total: { currency: "MXN", amount_cents: 9_280 },
    claim_window_ends_at: null,
    finalized_at: null,
    ...overrides,
  };
}

export function draft(overrides: Partial<QuoteDraft> = {}): QuoteDraft {
  const address = {
    addressText: "Calle Uno 123, Centro",
    contactName: "Contacto sintético",
    phone: "6670000000",
    lat: "24.8091",
    lng: "-107.3940",
    references: "",
  };
  return {
    clientAccountId: "",
    origin: address,
    destination: { ...address, addressText: "Avenida Dos 456, Norte" },
    serviceType: "SAME_DAY",
    consolidatedRoute: false,
    packages: [
      { description: "Caja", weightGrams: "500", declaredValue: "100.50", lengthMm: "", widthMm: "300", heightMm: "" },
    ],
    ...overrides,
  };
}
