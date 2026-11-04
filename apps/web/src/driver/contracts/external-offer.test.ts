import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import {
  ExternalOfferContractError,
  externalOfferPublicIdPattern,
  parseExternalOfferPage,
} from "./external-offer";

const offer = {
  id: "11111111-1111-1111-1111-111111111111",
  order_id: "22222222-2222-2222-2222-222222222222",
  public_id: "ORD_SyntheticGuia_000-0001",
  status: "OPEN",
  commission: { currency: "MXN", amount_cents: 12_345 },
  expires_at: "2026-08-28T02:00:00Z",
  accepted_by_driver_id: null,
  accepted_at: null,
  version: 1,
};

function parsesOne(value: unknown) {
  return parseExternalOfferPage({ items: [value], next_cursor: null });
}

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
      expect(() => parsesOne(value)).toThrow(ExternalOfferContractError);
    }
  });

  // EXT-001-OFFER-GUIA-2026-11-04 (Z24 "La guía (Recommended)"): AI-05 DriverExternalOffer.public_id.
  it("keeps the guía of the offer's order exactly as AI-05 sends it", () => {
    const parsed = parsesOne(offer).items[0];
    expect(parsed?.public_id).toBe("ORD_SyntheticGuia_000-0001");
    expect(parsed?.order_id).toBe("22222222-2222-2222-2222-222222222222");
  });

  it("fails closed without the guía or with one outside the AI-05 pattern", () => {
    const missingGuia = Object.fromEntries(
      Object.entries(offer).filter(([name]) => name !== "public_id"),
    );
    for (const value of [
      missingGuia,
      { ...offer, public_id: null },
      { ...offer, public_id: 42 },
      { ...offer, public_id: "" },
      { ...offer, public_id: "ORD_SyntheticGuia_000-001" },
      { ...offer, public_id: "ORD_SyntheticGuia_000-00001" },
      { ...offer, public_id: "ord_SyntheticGuia_000-0001" },
      { ...offer, public_id: "PQ-_SyntheticGuia_000-0001" },
      { ...offer, public_id: "ORD_SyntheticGuia+000/0001" },
      { ...offer, public_id: "ORD_SyntheticGuia=000-0001" },
      { ...offer, public_id: " ORD_SyntheticGuia_000-0001" },
      { ...offer, public_id: "ORD_SyntheticGuia_000-0001\n" },
      { ...offer, public_id: "ORD_SyntheticGuía_000-0001" },
      { ...offer, public_id: offer.order_id },
    ]) {
      expect(() => parsesOne(value)).toThrow(ExternalOfferContractError);
    }
  });

  it("checks the guía with the AI-05 DriverExternalOffer pattern", () => {
    const openApi = readFileSync(
      resolve(process.cwd(), "../../docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml"),
      "utf8",
    );
    const start = openApi.indexOf("\n    DriverExternalOffer:\n");
    const schema = openApi.slice(start, openApi.indexOf("\n    ExternalOfferPage:\n", start));
    expect(start).toBeGreaterThan(0);
    expect(schema).toContain("- $ref: '#/components/schemas/ExternalOffer'");
    expect(schema).toContain("        - public_id\n");
    expect(schema).toContain(`pattern: ${externalOfferPublicIdPattern.source}\n`);
    expect(externalOfferPublicIdPattern.flags).toBe("");
  });
});
