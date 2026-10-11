import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { parseCreatedOrder, parseQuote } from "../contracts/create-order";
import { draft, orderResponse, quoteResponse } from "../contracts/create-order.fixtures";
import { initialOrderWizardDraft, type OrderWizardDraft, type OrderWizardStep } from "../contracts/order-wizard";
import type {
  CreateOrderController,
  CreateOrderOutcome,
  CreateOrderState,
} from "../state/create-order-controller";
import { CreateOrderOutcomePanel } from "./create-order-summary";
import { CreateOrderWizard } from "./create-order-wizard";

/** Handlers never run during a static render. */
const controller = {} as CreateOrderController;
const order = parseCreatedOrder(orderResponse());
const filled: OrderWizardDraft = {
  ...initialOrderWizardDraft,
  origin: draft().origin,
  destination: draft().destination,
  packages: draft().packages,
  serviceType: "SAME_DAY",
  codAmount: "150.50",
  payerType: "SENDER",
};

function state(step: OrderWizardStep, overrides: Partial<CreateOrderState> = {}): CreateOrderState {
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

const wizard = (current: CreateOrderState, versionsConfigured = true) =>
  renderToStaticMarkup(createElement(CreateOrderWizard, { state: current, controller, versionsConfigured }));

const outcomePanel = (outcome: CreateOrderOutcome) =>
  renderToStaticMarkup(createElement(CreateOrderOutcomePanel, { outcome, busy: false, controller }));

/** Text a person reads or hears: text nodes plus the attributes assistive technology reads. */
function readable(markup: string): string {
  const attributes = [...markup.matchAll(/\s(?:aria-label|title|placeholder|alt)="([^"]*)"/g)].map((match) => match[1]);
  const text = markup
    .replace(/<[^>]+>/g, " ")
    .replace(/&amp;/g, "&")
    .replace(/&quot;/g, '"')
    .replace(/&#x27;/g, "'")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">");
  return `${text} ${attributes.join(" ")}`.replace(/\s+/g, " ");
}

const uuid = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i;
const internalCodes = /\b(API|DRAFT|CONFIRMED|SAME_DAY|SENDER|OCCASIONAL|BASE_TARIFF|ASSISTED|UUID)\b/;

const steps: readonly OrderWizardStep[] = ["where", "what", "service", "confirm"];

describe("new order wizard markup (UI-PHASE3-ORDER-WIZARD-2026-10-10)", () => {
  it.each(steps)("keeps identifiers, internal codes and the word API out of step %s", (step) => {
    const text = readable(wizard(state(step)));
    expect(text).not.toMatch(uuid);
    expect(text).not.toMatch(internalCodes);
    expect(text).not.toMatch(/\bprc-001\b|terms-1|privacy-1/);
  });

  it("shows the stepper with completed, current and pending steps", () => {
    const markup = wizard(state("service"));
    const items = markup.match(/<li class="opsWizardStep [^"]*"[^>]*>/g) ?? [];
    expect(items).toHaveLength(4);
    expect(items[0]).toContain("opsWizardStep-done");
    expect(items[2]).toContain('aria-current="step"');
    expect(items[3]).toContain("opsWizardStep-pending");
    expect(markup.match(/aria-current="step"/g)).toHaveLength(1);
    // Completed steps are buttons back to them; the current and the pending ones are not controls.
    expect(markup.match(/<button type="button" class="opsWizardStepItem"/g)).toHaveLength(2);
    expect(readable(markup)).toContain("Paso 3 de 4: Servicio y precio");
    expect(readable(markup)).toContain("(completado; volver a este paso)");
    expect(readable(markup)).toContain("(pendiente)");
  });

  it("asks only for the shipment in steps 1 to 3", () => {
    const where = readable(wizard(state("where")));
    for (const label of ["Origen: quién envía", "Destino: quién recibe", "Dirección", "Teléfono", "Latitud", "Longitud"])
      expect(where).toContain(label);
    expect(where).toContain("Culiacán");
    const what = readable(wizard(state("what")));
    for (const label of ["Paquete 1", "Peso (gramos)", "Valor declarado (MXN)", "Medidas en milímetros (opcional)"])
      expect(what).toContain(label);
    const service = readable(wizard(state("service")));
    for (const label of [
      "Tipo de servicio",
      "Ruta consolidada",
      "Ventana de entrega (opcional)",
      "hora de Mazatlán",
      "máximo 12 horas",
      "Cobro contra entrega (MXN, opcional)",
      "$20,000.00 MXN",
      "ID de la cuenta cliente (opcional)",
      "Autorizar envío de bajo monto",
    ])
      expect(service).toContain(label);
  });

  it("leaves exactly who pays and the two acceptance checkboxes for step 4", () => {
    const markup = wizard(state("confirm"));
    const form = markup.slice(markup.indexOf("<form"), markup.indexOf("</form>"));
    expect(form.match(/<select/g)).toHaveLength(1);
    expect(form.match(/type="checkbox"/g)).toHaveLength(2);
    expect(form).not.toMatch(/<input(?![^>]*type="checkbox")/);
    const text = readable(form);
    expect(text).toContain("Quién paga");
    expect(text).toContain("aceptó los términos y el aviso de privacidad vigentes");
    expect(text).toContain("no contiene artículos prohibidos");
    expect(text).toContain("Crear y confirmar orden");
    expect(text).toContain("queda lista para preparar");
  });

  it("shows the price breakdown before the confirmation on the last step", () => {
    const markup = wizard(state("confirm"));
    const summary = markup.indexOf('aria-labelledby="order-summary-title"');
    expect(summary).toBeGreaterThan(-1);
    expect(summary).toBeLessThan(markup.indexOf("<form"));
    const text = readable(markup);
    for (const label of ["Neto sin IVA", "IVA", "Total (IVA incluido)", "Regla aplicada: tarifa ocasional", "$92.80 MXN"])
      expect(text).toContain(label);
    // While capturing, the summary follows the fields.
    const capture = wizard(state("service"));
    expect(capture.indexOf('aria-labelledby="order-summary-title"')).toBeGreaterThan(capture.indexOf("</form>"));
  });

  it("keeps the confirmation disabled and says why when the versions are not configured", () => {
    const markup = wizard(state("confirm"), false);
    expect(readable(markup)).toContain("Faltan las versiones vigentes de términos y aviso de privacidad");
    expect(markup).toMatch(/<button[^>]*type="submit"[^>]*disabled=""[^>]*>Crear y confirmar orden/);
  });

  it("warns PLATFORM_ADMIN that confirming needs MFA", () => {
    expect(readable(wizard(state("confirm", { confirmationNeedsMfa: true })))).toContain(
      "Confirmar la orden requiere verificar tu identidad (MFA)",
    );
  });

  it("links each message to its field", () => {
    const markup = wizard(
      state("where", {
        fieldErrors: [{ field: "origin.phone", message: "Captura el teléfono de origen." }],
      }),
    );
    expect(markup).toContain('id="order-origin-phone"');
    expect(markup).toMatch(/<input[^>]*id="order-origin-phone"[^>]*aria-describedby="[^"]*order-origin-phone-error/);
    expect(markup).toContain('aria-invalid="true"');
    expect(markup).toContain('<p id="order-origin-phone-error" class="fieldError">Captura el teléfono de origen.</p>');
  });

  it("calls out a price of 52 MXN or less in step 3", () => {
    const low = parseQuote(quoteResponse({ total: { currency: "MXN", amount_cents: 5_200 } }));
    expect(readable(wizard(state("service", { quote: low })))).toContain("El total es de 52 MXN o menos");
    const priceless = readable(wizard(state("service", { quote: null })));
    expect(priceless).toContain("Calcular precio");
    expect(priceless).toContain("Se calcula en el paso 3");
  });
});

describe("new order outcome", () => {
  it("reports the confirmed order with its tracking number and links, without identifiers", () => {
    const markup = outcomePanel({ kind: "confirmed", order, status: "CONFIRMED", codExpectedCents: 15_050 });
    const text = readable(markup);
    expect(text).toContain("Orden creada y confirmada");
    expect(text).toContain("Número de guía");
    expect(text).toContain("PQ-000123");
    expect(text).toContain("Confirmada");
    expect(text).toContain("$150.50 MXN");
    expect(text).toContain("Abrir orden");
    expect(text).toContain("Capturar otra orden");
    expect(markup).toContain(`href="/ops/orders/${order.id}"`);
    expect(markup).not.toContain('href="/ops/orders/new"');
    expect(text).not.toMatch(uuid);
    expect(text).not.toMatch(internalCodes);
  });

  it("never reads as confirmed when the confirmation was refused", () => {
    const markup = outcomePanel({
      kind: "not_confirmed",
      order,
      codExpectedCents: 0,
      certainty: "draft",
      message: "Falta confirmar que el envío no contiene artículos prohibidos.",
      stepUpHref: null,
      retryable: false,
    });
    const text = readable(markup);
    expect(text).toContain("Orden creada en borrador");
    expect(text).toContain("No se pudo confirmar. Falta confirmar que el envío no contiene artículos prohibidos.");
    expect(text).toContain("“Siguiente paso”");
    expect(text).not.toContain("creada y confirmada");
    expect(text).not.toContain("Reintentar confirmación");
    expect(markup).toContain(`href="/ops/orders/${order.id}"`);
    expect(text).not.toMatch(uuid);
  });

  it("offers the retry and the MFA step-up only when they apply", () => {
    const unknown = readable(
      outcomePanel({
        kind: "not_confirmed",
        order,
        codExpectedCents: 0,
        certainty: "unknown",
        message: "Sin respuesta válida del servidor.",
        stepUpHref: null,
        retryable: true,
      }),
    );
    expect(unknown).toContain("Orden creada sin confirmación");
    expect(unknown).toContain("no sabemos si quedó confirmada");
    expect(unknown).toContain("Reintentar confirmación");
    const mfa = outcomePanel({
      kind: "not_confirmed",
      order,
      codExpectedCents: 0,
      certainty: "draft",
      message: "Esta acción requiere verificar tu identidad (MFA).",
      stepUpHref: "/login?mfa=required&return_url=%2Fops%2Forders%2Fx",
      retryable: false,
    });
    expect(readable(mfa)).toContain("Verificar identidad");
    expect(mfa).toContain('href="/login?mfa=required&amp;return_url=%2Fops%2Forders%2Fx"');
  });
});
