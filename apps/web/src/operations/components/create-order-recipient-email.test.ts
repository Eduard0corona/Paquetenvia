import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { invalidRecipientEmailMessage, parseCreatedOrder, parseQuote } from "../contracts/create-order";
import { draft, orderResponse, quoteResponse } from "../contracts/create-order.fixtures";
import { initialOrderWizardDraft, type OrderWizardDraft, type OrderWizardStep } from "../contracts/order-wizard";
import type { CreateOrderController, CreateOrderState } from "../state/create-order-controller";
import { CreateOrderOutcomePanel } from "./create-order-summary";
import { CreateOrderWizard } from "./create-order-wizard";

/**
 * NTF-RECIPIENT-NOTICES-2026-10-20 (AI-07 create_order.recipient_notices.email): the wizard field "Correo del
 * destinatario (opcional)" in step "Dónde", destination only; the summary is the only place that restates it, and
 * the outcome never shows it.
 */
const controller = {} as CreateOrderController;
const email = "destinatario.sintetico@example.invalid";

function state(step: OrderWizardStep, destinationEmail = "", overrides: Partial<CreateOrderState> = {}): CreateOrderState {
  const filled: OrderWizardDraft = {
    ...initialOrderWizardDraft,
    origin: draft().origin,
    destination: { ...draft().destination, email: destinationEmail },
    packages: draft().packages,
    serviceType: "SAME_DAY",
    payerType: "SENDER",
  };
  return {
    phase: "ready",
    role: "DISPATCHER",
    canQuote: true,
    canOrder: true,
    canAuthorizeLowPrice: true,
    confirmationNeedsMfa: false,
    step,
    draft: filled,
    fieldErrors: [],
    validationAttempt: 0,
    quote: parseQuote(quoteResponse()),
    busy: null,
    outcome: null,
    message: null,
    stepUpHref: null,
    ...overrides,
  };
}

const wizard = (current: CreateOrderState) =>
  renderToStaticMarkup(createElement(CreateOrderWizard, { state: current, controller, versionsConfigured: true }));

function formOf(markup: string): string {
  return markup.slice(markup.indexOf("<form"), markup.indexOf("</form>"));
}

describe("recipient e-mail field in the new order wizard", () => {
  it("is offered once, on the destination of step 1, optional and labelled in Spanish", () => {
    const form = formOf(wizard(state("where")));
    expect(form.match(/Correo del destinatario \(opcional\)/g)).toHaveLength(1);
    expect(form).toContain('id="order-destination-email"');
    expect(form).not.toContain('id="order-origin-email"');
    expect(form).toMatch(/<input[^>]*id="order-destination-email"[^>]*inputMode="email"/);
    expect(form).toMatch(/<input[^>]*id="order-destination-email"[^>]*maxLength="254"/);
    expect(form).not.toMatch(/<input[^>]*id="order-destination-email"[^>]*required/);
    // It follows the destination phone, inside the destination fieldset.
    const destination = form.slice(form.indexOf("Destino: quién recibe"));
    expect(destination).toContain('id="order-destination-email"');
    expect(destination.indexOf('id="order-destination-phone"')).toBeLessThan(destination.indexOf('id="order-destination-email"'));
  });

  it("shows its message next to the field without the typed value", () => {
    const markup = wizard(
      state("where", "destinatario.sintetico@example", {
        fieldErrors: [{ field: "destination.email", message: invalidRecipientEmailMessage }],
        validationAttempt: 1,
      }),
    );
    expect(markup).toMatch(/<input[^>]*id="order-destination-email"[^>]*aria-describedby="[^"]*order-destination-email-error/);
    expect(markup).toContain(`<p id="order-destination-email-error" class="fieldError">${invalidRecipientEmailMessage}</p>`);
    const messages = markup.match(/class="fieldError">[^<]*</g) ?? [];
    for (const message of messages) expect(message).not.toContain("destinatario.sintetico");
  });

  it("is restated by the summary only, and only when typed", () => {
    for (const step of ["where", "what", "service", "confirm"] as const) {
      const markup = wizard(state(step, email));
      const form = formOf(markup);
      const summary = markup.slice(markup.indexOf("opsWizardSummary"));
      expect(summary).toContain("Correo del destinatario");
      expect(summary).toContain(email);
      if (step !== "where") expect(form).not.toContain(email);
    }

    expect(wizard(state("confirm"))).not.toContain("Correo del destinatario<");
  });

  it("never appears in the outcome once the order exists", () => {
    const order = parseCreatedOrder(orderResponse());
    const markup = renderToStaticMarkup(
      createElement(CreateOrderOutcomePanel, {
        outcome: { kind: "confirmed", order, status: "CONFIRMED", codExpectedCents: 0 },
        busy: false,
        controller,
      }),
    );
    expect(markup).not.toContain("Correo");
    expect(markup).not.toContain("@");
  });
});
