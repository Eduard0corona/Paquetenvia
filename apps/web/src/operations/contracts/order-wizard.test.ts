import { describe, expect, it } from "vitest";
import { acceptanceVersionsUnavailableMessage } from "./acceptance-versions";
import { confirmationBlockerLabels, parseQuote, pricingTiers } from "./create-order";
import { draft, quoteResponse } from "./create-order.fixtures";
import {
  acceptanceDraftOf,
  breakdownLineLabel,
  fieldControlId,
  firstInvalidStep,
  initialOrderWizardDraft,
  nextWizardStep,
  orderWizardSteps,
  packagesSummaryText,
  previousWizardStep,
  pricingTierLabels,
  quoteDraftOf,
  quoteIsStale,
  quoteRequiredMessage,
  quoteStateErrors,
  serviceWindowSummary,
  stepOfField,
  summarizePackages,
  wizardConfirmAction,
  wizardConfirmationReason,
  wizardStepErrors,
  type OrderWizardDraft,
  type WizardValidationContext,
} from "./order-wizard";

const now = new Date("2026-09-28T17:00:00Z");
const quote = parseQuote(quoteResponse());
const context: WizardValidationContext = {
  now,
  quote,
  canAuthorizeLowPrice: true,
  acceptanceVersions: { termsVersion: "terms-1", privacyVersion: "privacy-1" },
};
const filled: OrderWizardDraft = {
  ...initialOrderWizardDraft,
  origin: draft().origin,
  destination: draft().destination,
  packages: draft().packages,
  serviceType: "SAME_DAY",
  payerType: "SENDER",
  accepted: true,
  restrictedGoodsAcknowledged: true,
};
const fields = (errors: readonly { readonly field: string }[]) => errors.map((error) => error.field);

describe("wizard steps (UI-PHASE3-ORDER-WIZARD-2026-10-10)", () => {
  it("has the four approved steps in order", () => {
    expect(orderWizardSteps.map((step) => step.title)).toEqual([
      "Dónde",
      "Qué se envía",
      "Servicio y precio",
      "Confirmar",
    ]);
    expect(nextWizardStep("where")).toBe("what");
    expect(nextWizardStep("service")).toBe("confirm");
    expect(nextWizardStep("confirm")).toBeNull();
    expect(previousWizardStep("where")).toBeNull();
    expect(previousWizardStep("confirm")).toBe("service");
  });

  it("assigns every message to the step that shows its field", () => {
    expect(stepOfField("origin.phone")).toBe("where");
    expect(stepOfField("destination.coordinates")).toBe("where");
    expect(stepOfField("packages")).toBe("what");
    expect(stepOfField("packages.3.dimensions")).toBe("what");
    for (const field of ["serviceType", "clientAccountId", "lowPriceReason", "codAmount", "serviceWindow", "quote"])
      expect(stepOfField(field), field).toBe("service");
    for (const field of ["payerType", "accepted", "restrictedGoodsAcknowledged", "acceptanceVersions", "acceptedAt"])
      expect(stepOfField(field), field).toBe("confirm");
  });

  it("validates each step on its own", () => {
    const empty = { ...context, quote: null };
    expect(fields(wizardStepErrors("where", initialOrderWizardDraft, empty))).toHaveLength(8);
    expect(fields(wizardStepErrors("what", initialOrderWizardDraft, empty))).toEqual([
      "packages.0.description",
      "packages.0.weightGrams",
      "packages.0.declaredValue",
    ]);
    expect(wizardStepErrors("service", initialOrderWizardDraft, empty)).toEqual([
      { field: "serviceType", message: "Selecciona el tipo de servicio." },
      { field: "quote", message: quoteRequiredMessage },
    ]);
    expect(fields(wizardStepErrors("confirm", initialOrderWizardDraft, empty))).toEqual([
      "payerType",
      "accepted",
      "restrictedGoodsAcknowledged",
    ]);
    for (const step of orderWizardSteps) expect(wizardStepErrors(step.id, filled, context), step.id).toEqual([]);
  });

  it("finds the first incomplete step in order", () => {
    expect(firstInvalidStep(filled, context)).toBeNull();
    expect(firstInvalidStep({ ...filled, payerType: "" }, context)?.step).toBe("confirm");
    expect(firstInvalidStep(filled, { ...context, quote: null })).toEqual({
      step: "service",
      errors: [{ field: "quote", message: quoteRequiredMessage }],
    });
    const twoSteps = firstInvalidStep({ ...filled, packages: [], accepted: false }, context);
    expect(twoSteps).toEqual({ step: "what", errors: [{ field: "packages", message: "Captura entre 1 y 20 paquetes." }] });
    expect(firstInvalidStep(filled, { ...context, acceptanceVersions: null })).toEqual({
      step: "confirm",
      errors: [{ field: "acceptanceVersions", message: acceptanceVersionsUnavailableMessage }],
    });
  });

  it("names one measure message per package, whatever the number of wrong measures", () => {
    const wrong = { ...filled, packages: [{ ...draft().packages[0], lengthMm: "0", widthMm: "x", heightMm: "1.5" }] };
    expect(wizardStepErrors("what", wrong, context)).toEqual([
      {
        field: "packages.0.dimensions",
        message: "Las medidas del paquete 1 deben ser enteros en milímetros mayores que 0.",
      },
    ]);
  });

  it("holds an expired, used or low price at step 3 and drops only the unusable ones", () => {
    expect(quoteStateErrors(quote, now)).toEqual([]);
    const expired = parseQuote(quoteResponse({ expires_at: "2026-09-28T16:59:59Z" }));
    expect(quoteStateErrors(expired, now)).toEqual([{ field: "quote", message: confirmationBlockerLabels.expired }]);
    expect(quoteIsStale(expired, now)).toBe(true);
    expect(quoteIsStale(parseQuote(quoteResponse({ status: "USED" })), now)).toBe(true);
    const low = parseQuote(quoteResponse({ total: { currency: "MXN", amount_cents: 5_200 } }));
    expect(quoteStateErrors(low, now)).toEqual([{ field: "quote", message: confirmationBlockerLabels.low_price }]);
    expect(quoteIsStale(low, now)).toBe(false);
  });

  it("never sends the low price authorization for a role that cannot give it", () => {
    const authorizing = { ...filled, authorizeLowPrice: true, lowPriceReason: "Cliente ancla" };
    expect(quoteDraftOf(authorizing, true).authorizeLowPrice).toBe(true);
    expect(quoteDraftOf(authorizing, false).authorizeLowPrice).toBe(false);
    expect(acceptanceDraftOf({ ...filled, codAmount: "150.50" })).toEqual({
      payerType: "SENDER",
      accepted: true,
      restrictedGoodsAcknowledged: true,
      codAmount: "150.50",
      serviceWindowFrom: "",
      serviceWindowTo: "",
    });
  });

  it("confirms with CONFIRMED, the prohibited-goods acknowledgement and a fixed reason", () => {
    expect(wizardConfirmAction).toMatchObject({
      target: "CONFIRMED",
      label: "Confirmar orden",
      needsRestrictedGoodsAcknowledgement: true,
    });
    expect(wizardConfirmationReason).toBe("Confirmada al crear la orden");
    expect(wizardConfirmationReason.length).toBeLessThanOrEqual(500);
  });

  it("derives stable control ids from the field names", () => {
    expect(fieldControlId("origin.phone")).toBe("order-origin-phone");
    expect(fieldControlId("packages.2.weightGrams")).toBe("order-packages-2-weightGrams");
    expect(fieldControlId("quote")).toBe("order-quote");
  });
});

describe("wizard summary copy", () => {
  it("adds the typed weights and declared values without floating point", () => {
    const packages = [
      { ...draft().packages[0], weightGrams: "500", declaredValue: "100.50" },
      { ...draft().packages[0], weightGrams: "1000", declaredValue: "0.10" },
    ];
    expect(summarizePackages(packages)).toEqual({ count: 2, totalGrams: 1_500, declaredCents: 10_060 });
    expect(packagesSummaryText(summarizePackages(packages))).toBe("2 paquetes · 1,500 g");
    const unfinished = summarizePackages([{ ...packages[0], weightGrams: "", declaredValue: "1.234" }]);
    expect(unfinished).toEqual({ count: 1, totalGrams: null, declaredCents: null });
    expect(packagesSummaryText(unfinished)).toBe("1 paquete");
  });

  it("shows the window in Mazatlán time, or the zone's schedule when empty", () => {
    expect(serviceWindowSummary("", "")).toBe("Horario de la zona");
    const text = serviceWindowSummary("2026-10-02T12:00", "2026-10-02T14:00");
    expect(text).toContain("12:00");
    expect(text).toContain("14:00");
    expect(text).toContain("(hora de Mazatlán)");
    expect(serviceWindowSummary("2026-10-02T12:00", "")).toBe("Por completar");
    expect(serviceWindowSummary("2026-10-02T14:00", "2026-10-02T12:00")).toBe("Por completar");
  });

  it("explains every pricing tier in Spanish without its code", () => {
    expect(Object.keys(pricingTierLabels).sort()).toEqual([...pricingTiers].sort());
    for (const tier of pricingTiers) {
      expect(pricingTierLabels[tier]).not.toMatch(/[A-Z_]{4,}/);
      expect(pricingTierLabels[tier]).not.toContain(tier);
    }
    expect(pricingTierLabels.OCCASIONAL).toBe("ocasional");
  });

  it("names breakdown lines in Spanish and never shows an unknown line type", () => {
    expect(breakdownLineLabel("BASE_TARIFF")).toBe("Tarifa base (IVA incluido)");
    expect(breakdownLineLabel("NEW_SURCHARGE")).toBe("Otro concepto");
    expect(breakdownLineLabel("toString")).toBe("Otro concepto");
    expect(breakdownLineLabel(null)).toBe("Concepto");
  });
});
