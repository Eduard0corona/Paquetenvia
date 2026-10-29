import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import {
  buildCreateOrderBody,
  parseQuote,
  recipientWhatsAppConsentText,
  recipientWhatsAppConsentVersion,
} from "../contracts/create-order";
import { draft, quoteId, quoteResponse } from "../contracts/create-order.fixtures";
import { csvRecipientConsentField, csvRecipientConsentHint } from "../contracts/csv-import";
import {
  acceptanceDraftOf,
  initialOrderWizardDraft,
  type OrderWizardDraft,
  type OrderWizardStep,
} from "../contracts/order-wizard";
import type { CreateOrderController, CreateOrderState } from "../state/create-order-controller";
import type { CsvImportController } from "../state/csv-import-controller";
import { CreateOrderWizard } from "./create-order-wizard";
import { CsvCommitForm } from "./csv-import-shell";

/**
 * NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-004 required Vitest test: the consent checkbox; AI-07 create_order and
 * csv_order_import recipient_notices) and NTF-RECIPIENT-NOTICE-PRODUCER-2026-10-23 (N2 "Mismo texto y una nota
 * (Recommended)", N3 choice 1): one optional box, unticked by default, labelled exactly with the AI-24 consent text, on
 * step "Confirmar" of "Nueva orden" and once for the whole file on the CSV-001 commit with the fixed note; the version
 * is a constant of the web, sent only with a ticked box.
 */
const repository = (path: string) => readFileSync(resolve(process.cwd(), "../..", path), "utf8");
const runtime = repository("docs/normative/v0.6/specs/AI-24_RUNTIME_HARDENING_CONTRACT.yaml");
const uiContracts = repository("docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml").replace(/\s+/g, " ");

const consentText = "El destinatario aceptó recibir avisos de su envío por WhatsApp";
const csvNote = "Aplica a todas las filas de este archivo. Márcala solo si cada destinatario lo aceptó.";
const wizardBoxId = "order-recipientWhatsAppConsent";
const csvBoxId = "csv-recipient-whatsapp-consent";

const wizardController = {} as CreateOrderController;
const csvController = {} as Pick<CsvImportController, "commit">;

function wizardState(step: OrderWizardStep, overrides: Partial<OrderWizardDraft> = {}): CreateOrderState {
  return {
    phase: "ready",
    role: "DISPATCHER",
    canQuote: true,
    canOrder: true,
    canAuthorizeLowPrice: true,
    confirmationNeedsMfa: false,
    step,
    draft: {
      ...initialOrderWizardDraft,
      origin: draft().origin,
      destination: draft().destination,
      packages: draft().packages,
      serviceType: "SAME_DAY",
      payerType: "SENDER",
      ...overrides,
    },
    fieldErrors: [],
    validationAttempt: 0,
    quote: parseQuote(quoteResponse()),
    busy: null,
    outcome: null,
    message: null,
    stepUpHref: null,
  };
}

const wizard = (state: CreateOrderState) =>
  renderToStaticMarkup(createElement(CreateOrderWizard, { state, controller: wizardController, versionsConfigured: true }));

const csvForm = (busy = false, waiting = false) =>
  renderToStaticMarkup(
    createElement(CsvCommitForm, { validRows: 3, state: { busy }, waiting, controller: csvController }),
  );

/** The `<input>` tag of the control with this id. */
function control(markup: string, id: string): string {
  const tag = markup.match(new RegExp(`<input[^>]*id="${id}"[^>]*>`))?.[0];
  expect(tag, id).toBeDefined();
  return tag!;
}

/** The text of the `<label>` that holds the control with this id, tags removed. */
function labelText(markup: string, id: string): string {
  const label = markup.match(new RegExp(`<label[^>]*for="${id}"[^>]*>([\\s\\S]*?)</label>`))?.[1];
  expect(label, id).toBeDefined();
  return label!.replace(/<[^>]+>/g, "").replace(/\s+/g, " ").trim();
}

const acceptance = { payerType: "SENDER", accepted: true, restrictedGoodsAcknowledged: true };
const versions = { termsVersion: "terms-2026.09", privacyVersion: "privacy_v3" };

describe("the WhatsApp consent text and version", () => {
  it("are the AI-24 recipient_notices.consent text and version, the version a constant of the web", () => {
    expect(recipientWhatsAppConsentText).toBe(consentText);
    expect(recipientWhatsAppConsentVersion).toBe("NTF-RECIPIENT-WHATSAPP-CONSENT-V1");
    expect(runtime).toContain(`    text: ${consentText}\n    version: ${recipientWhatsAppConsentVersion}\n`);
  });

  it("gives the CSV box the N2 note that AI-07 records", () => {
    expect(csvRecipientConsentHint).toBe(csvNote);
    expect(uiContracts).toContain(
      `the box reads exactly the AI-24 consent text "${consentText}", version ${recipientWhatsAppConsentVersion}, ` +
        `identical to "Nueva orden", with this fixed note below it: "${csvNote}"`,
    );
  });
});

describe("the consent box of \"Nueva orden\" (AI-07 create_order.recipient_notices.consent)", () => {
  it("is offered once, on step \"Confirmar\" only, optional, unticked by default and labelled with the AI-24 text", () => {
    const markup = wizard(wizardState("confirm"));
    expect(markup.split(`id="${wizardBoxId}"`)).toHaveLength(2);
    const box = control(markup, wizardBoxId);
    expect(box).toContain('type="checkbox"');
    expect(box).not.toMatch(/\schecked/);
    expect(box).not.toMatch(/\srequired/);
    expect(labelText(markup, wizardBoxId)).toBe(consentText);
    // In its own group after the two acceptance boxes, inside the form, before the confirmation.
    const form = markup.slice(markup.indexOf("<form"), markup.indexOf("</form>"));
    const group = form.slice(form.indexOf("<legend>Avisos al destinatario</legend>"));
    expect(group).toContain(`id="${wizardBoxId}"`);
    expect(form.indexOf("no contiene artículos prohibidos")).toBeLessThan(form.indexOf(`id="${wizardBoxId}"`));
    for (const step of ["where", "what", "service"] as const) expect(wizard(wizardState(step))).not.toContain(wizardBoxId);
  });

  it("shows the dispatcher's tick and never blocks the confirmation", () => {
    expect(control(wizard(wizardState("confirm", { recipientWhatsAppConsent: true })), wizardBoxId)).toMatch(/\schecked/);
    const unticked = buildCreateOrderBody(quoteId, acceptanceDraftOf({ ...initialOrderWizardDraft, ...acceptance }), versions, new Date());
    expect(unticked.ok).toBe(true);
  });

  it("is unticked in a new draft", () => {
    expect(initialOrderWizardDraft.recipientWhatsAppConsent).toBe(false);
    expect(acceptanceDraftOf(initialOrderWizardDraft).recipientWhatsAppConsent).toBe(false);
  });

  it("adds recipient_notices to createOrder only when ticked, with the web's version", () => {
    const at = new Date("2026-09-28T17:00:00Z");
    for (const answer of [undefined, false]) {
      const result = buildCreateOrderBody(quoteId, { ...acceptance, recipientWhatsAppConsent: answer }, versions, at);
      expect(result.ok && "recipient_notices" in result.body).toBe(false);
    }
    const ticked = buildCreateOrderBody(
      quoteId,
      acceptanceDraftOf({ ...initialOrderWizardDraft, ...acceptance, recipientWhatsAppConsent: true }),
      versions,
      at,
    );
    expect(ticked.ok && ticked.body.recipient_notices).toEqual({
      whatsapp_consent_attested: true,
      whatsapp_consent_version: "NTF-RECIPIENT-WHATSAPP-CONSENT-V1",
    });
    // Nothing about the recipient travels with the box.
    expect(JSON.stringify(ticked.ok && ticked.body.recipient_notices)).not.toMatch(/phone|email|name|\+52/i);
  });
});

describe("the consent box of the CSV-001 commit (AI-07 csv_order_import.recipient_notices)", () => {
  it("is one optional box for the file, unticked, labelled with the same text and followed by the fixed note", () => {
    const markup = csvForm();
    expect(markup.match(/type="checkbox"/g)).toHaveLength(2);
    const box = control(markup, csvBoxId);
    expect(box).toContain(`name="${csvRecipientConsentField}"`);
    expect(box).not.toMatch(/\schecked/);
    expect(box).not.toMatch(/\srequired/);
    expect(box).toContain('aria-describedby="csv-recipient-whatsapp-consent-hint"');
    expect(labelText(markup, csvBoxId)).toBe(consentText);
    expect(markup).toContain(`<p id="csv-recipient-whatsapp-consent-hint" class="fieldHint">${csvNote}</p>`);
    // The prohibited-goods confirmation stays the one required box, first.
    expect(markup).toMatch(/<input type="checkbox" required="" name="restricted_goods_acknowledged"\/>/);
    expect(markup.indexOf("restricted_goods_acknowledged")).toBeLessThan(markup.indexOf(csvBoxId));
    expect(markup).toContain("Confirmar lote de 3 orden(es)");
  });

  it("keeps the commit disabled while busy or waiting for the CSV budget", () => {
    expect(csvForm()).not.toMatch(/<button[^>]*disabled/);
    expect(csvForm(true)).toMatch(/<button[^>]*disabled/);
    expect(csvForm(false, true)).toMatch(/<button[^>]*disabled/);
  });
});
