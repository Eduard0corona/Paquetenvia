import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { externalOfferPublished, prepareExternalOffer, type ExternalOfferSubmission } from "./external-offer-publish";
import { PendingSubmissions } from "./pending-submissions";

const offer: ExternalOfferSubmission = {
  orderId: "0e000000-0000-4000-8000-000000000001",
  commissionCents: 4_550,
  expiresAt: "2026-11-03T06:30:00.000Z",
  vehicleType: "CAR",
};

function keys(): PendingSubmissions {
  let next = 0;
  return new PendingSubmissions(() => `00000000-0000-4000-8000-${String(++next).padStart(12, "0")}`);
}

// Z25 ("Corregirlo"): the key is reused only to resend the identical offer after a publish without a confirmation.
describe("external offer publish key", () => {
  it("resends the identical offer with the same key and the same payload", () => {
    const submissions = keys();
    const first = prepareExternalOffer(submissions, offer);
    const retry = prepareExternalOffer(submissions, { ...offer });
    expect(retry.key).toBe(first.key);
    expect(retry.payload).toEqual(offer);
  });

  it.each<[string, Partial<ExternalOfferSubmission>]>([
    ["commission", { commissionCents: 5_000 }],
    ["expiry", { expiresAt: "2026-11-03T06:45:00.000Z" }],
    ["vehicle", { vehicleType: "VAN" }],
    ["order", { orderId: "0e000000-0000-4000-8000-000000000002" }],
  ])("gives a changed %s a new key with the changed payload", (_, change) => {
    const submissions = keys();
    const first = prepareExternalOffer(submissions, offer);
    const changed = prepareExternalOffer(submissions, { ...offer, ...change });
    expect(changed.key).not.toBe(first.key);
    expect(changed.payload).toEqual({ ...offer, ...change });
    // Going back to the first offer resends it as it was, with its own key.
    const back = prepareExternalOffer(submissions, { ...offer });
    expect(back.key).toBe(first.key);
    expect(back.payload).toEqual(offer);
    expect(prepareExternalOffer(submissions, { ...offer, ...change }).key).toBe(changed.key);
  });

  it("gives the next offer a new key once the server confirmed one, even an identical offer", () => {
    const submissions = keys();
    const first = prepareExternalOffer(submissions, offer);
    const other = prepareExternalOffer(submissions, { ...offer, commissionCents: 5_000 });
    externalOfferPublished(submissions);
    expect(submissions.size).toBe(0);
    const next = prepareExternalOffer(submissions, offer);
    expect([first.key, other.key]).not.toContain(next.key);
    expect(prepareExternalOffer(submissions, { ...offer, commissionCents: 5_000 }).key).not.toBe(other.key);
  });

  it("never sends one key with two bodies", () => {
    const submissions = keys();
    const bodies = new Map<string, string>();
    for (const commissionCents of [4_550, 5_000, 4_550, 5_000, 6_000])
      for (const vehicleType of ["CAR", "VAN", "CAR"] as const) {
        const { key, payload } = prepareExternalOffer(submissions, { ...offer, commissionCents, vehicleType });
        const body = JSON.stringify(payload);
        expect(bodies.get(key) ?? body).toBe(body);
        bodies.set(key, body);
      }
    expect(bodies.size).toBe(6);
  });

  it("keeps a copy of the payload, not the caller's object", () => {
    const submissions = keys();
    const typed = { ...offer };
    const first = prepareExternalOffer(submissions, typed);
    expect(first.payload).not.toBe(typed);
    expect(first.payload).toEqual(offer);
  });

  it("is what the order card sends, with the key it prepared", () => {
    const card = readFileSync("src/operations/components/operations-order-card.tsx", "utf8");
    expect(card).toContain("const [submissions] = useState(() => new PendingSubmissions());");
    expect(card).toContain("prepareExternalOffer(submissions, {");
    expect(card).toContain("externalOfferPublished(submissions);");
    expect(card).not.toMatch(/keyRef|crypto\.randomUUID/);
    // The key and the payload sent are the prepared ones.
    const send = card.slice(card.indexOf("void onPublishExternalOffer("), card.indexOf(").then(() => {"));
    expect(send).toContain("payload.orderId");
    expect(send).toContain("payload.commissionCents");
    expect(send).toContain("payload.expiresAt");
    expect(send).toContain("payload.vehicleType");
    expect(send).toContain("key,");
  });
});
