import { describe, expect, it } from "vitest";
import {
  buildCreateQuoteBody,
  invalidRecipientEmailMessage,
  maximumRecipientEmailLength,
  normalizeRecipientEmail,
  quoteDraftErrors,
} from "./create-order";
import { draft } from "./create-order.fixtures";
import { stepOfField } from "./order-wizard";

/**
 * NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-002; AI-07 create_order.recipient_notices.email; AI-05
 * DestinationAddressInput.email): "Correo del destinatario (opcional)" is trimmed and checked like createQuote, sent
 * as destination.email only when typed, never on the origin, and its one message never repeats the value.
 */
describe("recipient e-mail on createQuote", () => {
  it.each([
    ["destinatario@example.invalid", "destinatario@example.invalid"],
    ["  Destinatario@Example.INVALID\t", "Destinatario@example.invalid"],
    ["\r\n a.b+c@sub.example.mx \n", "a.b+c@sub.example.mx"],
    ["x@a.b", "x@a.b"],
    ["a@b..c", "a@b..c"],
    ["o'brien!#$%&*+-/=?^_`{|}~@example.com", "o'brien!#$%&*+-/=?^_`{|}~@example.com"],
  ])("normalizes %j as the server does", (typed, expected) => {
    expect(normalizeRecipientEmail(typed)).toBe(expected);
  });

  it.each([
    "",
    "   ",
    "destinatario",
    "@example.com",
    "destinatario@",
    "destinatario@example",
    "destinatario@.com",
    "destinatario@example.",
    "destinatario@@example.com",
    "des@tinatario@example.com",
    "desti natario@example.com",
    "destinatario@exam ple.com",
    "destinatário@example.com",
    "destinatario@exámple.com",
    " destinatario@example.com",
    "destinatario@example.com ",
    "destinatario@example.com\u0000",
    "destinatario\u007f@example.com",
    "destinatario@example.com ",
  ])("refuses %j", (typed) => {
    expect(normalizeRecipientEmail(typed)).toBeNull();
  });

  it("bounds the typed text to 254 characters", () => {
    const atLimit = `${"a".repeat(maximumRecipientEmailLength - "@example.com".length)}@example.com`;
    expect(atLimit).toHaveLength(254);
    expect(normalizeRecipientEmail(atLimit)).toBe(atLimit);
    expect(normalizeRecipientEmail(`a${atLimit}`)).toBeNull();
    expect(normalizeRecipientEmail(` ${atLimit}`)).toBeNull();
  });

  it("sends the normalized e-mail on the destination only when typed", () => {
    const withEmail = buildCreateQuoteBody(
      draft({ destination: { ...draft().destination, email: "  Destinatario@EXAMPLE.invalid " } }),
    );
    expect(withEmail.ok).toBe(true);
    if (!withEmail.ok) return;
    expect(withEmail.body.destination.email).toBe("Destinatario@example.invalid");
    expect(withEmail.body.origin).not.toHaveProperty("email");

    for (const email of [undefined, "", "   "]) {
      const without = buildCreateQuoteBody(draft({ destination: { ...draft().destination, email } }));
      expect(without.ok).toBe(true);
      if (!without.ok) return;
      expect(without.body.destination).not.toHaveProperty("email");
    }
  });

  it("never sends or checks an e-mail on the origin", () => {
    const result = buildCreateQuoteBody(draft({ origin: { ...draft().origin, email: "remitente@example.invalid" } }));
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.body.origin).not.toHaveProperty("email");
    expect(result.body.destination).not.toHaveProperty("email");
    expect(buildCreateQuoteBody(draft({ origin: { ...draft().origin, email: "no válido" } })).ok).toBe(true);
  });

  it("shows one message on the destination field of step 1 without the typed value", () => {
    const typed = "destinatario.sintetico@example";
    const errors = quoteDraftErrors(draft({ destination: { ...draft().destination, email: typed } }));
    expect(errors).toEqual([{ field: "destination.email", message: invalidRecipientEmailMessage }]);
    expect(stepOfField("destination.email")).toBe("where");
    expect(JSON.stringify(errors)).not.toContain(typed);
    const built = buildCreateQuoteBody(draft({ destination: { ...draft().destination, email: typed } }));
    expect(built).toEqual({ ok: false, errors: [invalidRecipientEmailMessage] });
  });
});
