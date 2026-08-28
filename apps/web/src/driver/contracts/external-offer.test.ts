import { describe, expect, it } from "vitest";
import { ExternalOfferContractError, parseExternalOfferPage } from "./external-offer";

const offer = {
  id: "11111111-1111-1111-1111-111111111111",
  order_id: "22222222-2222-2222-2222-222222222222",
  status: "OPEN",
  commission: { currency: "MXN", amount_cents: 12_345 },
  expires_at: "2026-08-28T02:00:00Z",
  accepted_by_driver_id: null,
  accepted_at: null,
  version: 1,
};

describe("ExternalOffer contract", () => {
  it("keeps commission and required expiration before acceptance", () => {
    const page = parseExternalOfferPage({ items: [offer], next_cursor: "opaque" });
    expect(page.items[0]?.commission.amount_cents).toBe(12_345);
    expect(page.items[0]?.expires_at).toBe("2026-08-28T02:00:00Z");
  });

  it("fails closed for missing expires_at, PII, or REJECTED", () => {
    const missingExpiration = Object.fromEntries(
      Object.entries(offer).filter(([name]) => name !== "expires_at"),
    );
    for (const value of [
      missingExpiration,
      { ...offer, telephone: "hidden" },
      { ...offer, status: "REJECTED" },
    ]) {
      expect(() => parseExternalOfferPage({ items: [value], next_cursor: null }))
        .toThrow(ExternalOfferContractError);
    }
  });
});
