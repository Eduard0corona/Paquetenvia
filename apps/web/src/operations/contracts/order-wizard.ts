import type { AcceptanceVersions } from "./acceptance-versions";
import {
  acceptanceDraftErrors,
  confirmationBlockerLabels,
  evaluateConfirmation,
  quoteDraftErrors,
  type AcceptanceDraft,
  type AddressDraft,
  type FieldError,
  type PackageDraft,
  type PricingTier,
  type Quote,
  type QuoteDraft,
} from "./create-order";
import { parseMxnToCents, sumCents } from "./money";
import { nextStepActions, type NextStepAction } from "./order-transitions";
import { formatServiceWindow, mazatlanWallTimeToInstant } from "./service-window";

/**
 * UI-PHASE3-ORDER-WIZARD-2026-10-10: "Nueva orden" (/ops/orders/new, AI-07 create_order) as a
 * four-step wizard that creates the order and then confirms it.
 *
 * This module is pure: the steps, the draft the person fills, which step owns each validation
 * message and the Spanish summaries. Every rule comes from the createQuote and createOrder
 * builders in create-order.ts, so a step accepts exactly what the request builders accept; the
 * API stays the authority.
 */

export const orderWizardSteps = [
  {
    id: "where",
    title: "Dónde",
    description:
      "Dirección, contacto y teléfono de quien envía y de quien recibe. Cobertura: Culiacán y zonas autorizadas.",
  },
  {
    id: "what",
    title: "Qué se envía",
    description: "Describe cada paquete con su peso y su valor declarado.",
  },
  {
    id: "service",
    title: "Servicio y precio",
    description:
      "Elige el servicio, agrega la ventana de entrega o el cobro contra entrega si aplican y calcula el precio.",
  },
  {
    id: "confirm",
    title: "Confirmar",
    description: "Indica quién paga y registra la aceptación del cliente.",
  },
] as const;

export type OrderWizardStep = (typeof orderWizardSteps)[number]["id"];

export function wizardStepIndex(step: OrderWizardStep): number {
  return orderWizardSteps.findIndex((item) => item.id === step);
}

export function nextWizardStep(step: OrderWizardStep): OrderWizardStep | null {
  return orderWizardSteps[wizardStepIndex(step) + 1]?.id ?? null;
}

export function previousWizardStep(step: OrderWizardStep): OrderWizardStep | null {
  const index = wizardStepIndex(step);
  return index > 0 ? orderWizardSteps[index - 1].id : null;
}

/** Everything the person types or ticks, as typed; requests are built from it only when sent. */
export interface OrderWizardDraft {
  readonly origin: AddressDraft;
  readonly destination: AddressDraft;
  readonly packages: readonly PackageDraft[];
  readonly serviceType: string;
  readonly consolidatedRoute: boolean;
  /** Typed as today: there is no client account list to pick from. */
  readonly clientAccountId: string;
  readonly serviceWindowFrom: string;
  readonly serviceWindowTo: string;
  readonly codAmount: string;
  readonly authorizeLowPrice: boolean;
  readonly lowPriceReason: string;
  readonly payerType: string;
  readonly accepted: boolean;
  readonly restrictedGoodsAcknowledged: boolean;
}

export const emptyAddressDraft: AddressDraft = {
  addressText: "",
  contactName: "",
  phone: "",
  lat: "",
  lng: "",
  references: "",
};

export const emptyPackageDraft: PackageDraft = {
  description: "",
  weightGrams: "",
  declaredValue: "",
  lengthMm: "",
  widthMm: "",
  heightMm: "",
};

export const initialOrderWizardDraft: OrderWizardDraft = {
  origin: emptyAddressDraft,
  destination: emptyAddressDraft,
  packages: [emptyPackageDraft],
  serviceType: "",
  consolidatedRoute: false,
  clientAccountId: "",
  serviceWindowFrom: "",
  serviceWindowTo: "",
  codAmount: "",
  authorizeLowPrice: false,
  lowPriceReason: "",
  payerType: "",
  accepted: false,
  restrictedGoodsAcknowledged: false,
};

/** The createQuote draft; a role that cannot authorize a low price never sends the field. */
export function quoteDraftOf(draft: OrderWizardDraft, canAuthorizeLowPrice: boolean): QuoteDraft {
  return {
    clientAccountId: draft.clientAccountId,
    origin: draft.origin,
    destination: draft.destination,
    serviceType: draft.serviceType,
    consolidatedRoute: draft.consolidatedRoute,
    packages: draft.packages,
    authorizeLowPrice: canAuthorizeLowPrice && draft.authorizeLowPrice,
    lowPriceReason: draft.lowPriceReason,
  };
}

/** The createOrder draft: who pays, both acknowledgements, the COD and the delivery window. */
export function acceptanceDraftOf(draft: OrderWizardDraft): AcceptanceDraft {
  return {
    payerType: draft.payerType,
    accepted: draft.accepted,
    restrictedGoodsAcknowledged: draft.restrictedGoodsAcknowledged,
    codAmount: draft.codAmount,
    serviceWindowFrom: draft.serviceWindowFrom,
    serviceWindowTo: draft.serviceWindowTo,
  };
}

const confirmFields: ReadonlySet<string> = new Set([
  "payerType",
  "acceptanceVersions",
  "accepted",
  "restrictedGoodsAcknowledged",
  "acceptedAt",
]);

/** The step that shows a message about `field` (FieldError.field, or `quote`). */
export function stepOfField(field: string): OrderWizardStep {
  if (field.startsWith("origin.") || field.startsWith("destination.")) return "where";
  if (field === "packages" || field.startsWith("packages.")) return "what";
  if (confirmFields.has(field)) return "confirm";
  // serviceType, consolidatedRoute, clientAccountId, lowPriceReason, codAmount, serviceWindow, quote.
  return "service";
}

export const quoteRequiredMessage = "Calcula el precio para continuar.";

/** Step 3 needs an active, unexpired price that the low price guard lets through. */
export function quoteStateErrors(quote: Quote | null, now: Date): readonly FieldError[] {
  if (quote === null) return [{ field: "quote", message: quoteRequiredMessage }];
  return evaluateConfirmation(quote, now).map((blocker) => ({
    field: "quote",
    message: confirmationBlockerLabels[blocker],
  }));
}

/** A price that can no longer be used (expired or not active) is dropped and calculated again. */
export function quoteIsStale(quote: Quote, now: Date): boolean {
  return evaluateConfirmation(quote, now).some((blocker) => blocker !== "low_price");
}

export interface WizardValidationContext {
  readonly now: Date;
  readonly quote: Quote | null;
  readonly canAuthorizeLowPrice: boolean;
  readonly acceptanceVersions: AcceptanceVersions | null;
}

function allErrors(draft: OrderWizardDraft, context: WizardValidationContext): readonly FieldError[] {
  const errors = [
    ...quoteDraftErrors(quoteDraftOf(draft, context.canAuthorizeLowPrice)),
    ...acceptanceDraftErrors(acceptanceDraftOf(draft), context.acceptanceVersions, context.now),
    ...quoteStateErrors(context.quote, context.now),
  ];
  const seen = new Set<string>();
  return errors.filter((error) => {
    const key = `${error.field}\u0000${error.message}`;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

/** What keeps `step` from being completed, in the order the fields appear. */
export function wizardStepErrors(
  step: OrderWizardStep,
  draft: OrderWizardDraft,
  context: WizardValidationContext,
): readonly FieldError[] {
  return allErrors(draft, context).filter((error) => stepOfField(error.field) === step);
}

/** The first step, in order, that is not complete, with its messages; null when all are. */
export function firstInvalidStep(
  draft: OrderWizardDraft,
  context: WizardValidationContext,
): { readonly step: OrderWizardStep; readonly errors: readonly FieldError[] } | null {
  const errors = allErrors(draft, context);
  for (const { id } of orderWizardSteps) {
    const own = errors.filter((error) => stepOfField(error.field) === id);
    if (own.length > 0) return { step: id, errors: own };
  }
  return null;
}

/** The element id of the control a FieldError is about, so the screen can focus it. */
export function fieldControlId(field: string): string {
  return `order-${field.replace(/[^A-Za-z0-9]+/g, "-")}`;
}

// ---------------------------------------------------------------------------
// Confirmation right after creation (owner, 2026-10-10: "Confirmada (Recommended)").

/**
 * The transition the wizard requests after createOrder: CONFIRMED, which AI-04 guards with
 * restricted_goods_check, so it carries `metadata.restricted_goods_acknowledged: true` from the
 * step 4 checkbox, exactly as "Siguiente paso" does when the server lists it.
 */
export const wizardConfirmAction: NextStepAction = (() => {
  const [action] = nextStepActions([
    { target_status: "CONFIRMED", required_metadata: ["restricted_goods_acknowledged"] },
  ]);
  if (action === undefined) throw new Error("The CONFIRMED action is not offered.");
  return action;
})();

/** The reason recorded in the order history for that confirmation; fixed text, no personal data. */
export const wizardConfirmationReason = "Confirmada al crear la orden";

// ---------------------------------------------------------------------------
// Summary copy (es-MX; nothing here leaves the browser).

const integerFormatter = new Intl.NumberFormat("es-MX", { maximumFractionDigits: 0 });

export interface PackagesSummary {
  readonly count: number;
  /** Sum of the typed weights, or null while any of them is not a valid whole number of grams. */
  readonly totalGrams: number | null;
  /** Sum of the declared values in integer cents, or null while any of them is not valid. */
  readonly declaredCents: number | null;
}

/** Same rule as the createQuote weight: a whole number of grams from 1 to 2,147,483,647. */
function grams(text: string): number | null {
  const trimmed = text.trim();
  if (!/^\d{1,9}$/.test(trimmed)) return null;
  const value = Number(trimmed);
  return value >= 1 && value <= 2_147_483_647 ? value : null;
}

export function summarizePackages(packages: readonly PackageDraft[]): PackagesSummary {
  let totalGrams: number | null = 0;
  const declared: number[] = [];
  let declaredValid = true;
  for (const item of packages) {
    const weight = grams(item.weightGrams);
    totalGrams = totalGrams === null || weight === null ? null : totalGrams + weight;
    const cents = parseMxnToCents(item.declaredValue);
    if (cents === null) declaredValid = false;
    else declared.push(cents);
  }
  return {
    count: packages.length,
    totalGrams: packages.length > 0 ? totalGrams : null,
    declaredCents: declaredValid && packages.length > 0 ? sumCents(declared) : null,
  };
}

export function packagesSummaryText(summary: PackagesSummary): string {
  const count = summary.count === 1 ? "1 paquete" : `${summary.count} paquetes`;
  return summary.totalGrams === null ? count : `${count} · ${integerFormatter.format(summary.totalGrams)} g`;
}

/** The typed delivery window in Mazatlán time; the zone's schedule when both bounds are empty. */
export function serviceWindowSummary(fromText: string, toText: string): string {
  const fromTrimmed = fromText.trim();
  const toTrimmed = toText.trim();
  if (fromTrimmed === "" && toTrimmed === "") return "Horario de la zona";
  const from = fromTrimmed === "" ? null : mazatlanWallTimeToInstant(fromTrimmed);
  const to = toTrimmed === "" ? null : mazatlanWallTimeToInstant(toTrimmed);
  if (from === null || to === null || from.getTime() >= to.getTime()) return "Por completar";
  return `${formatServiceWindow({ from: from.toISOString(), to: to.toISOString() })} (hora de Mazatlán)`;
}

/** AI-02 pricing tiers in plain Spanish; the tier code itself is never shown. */
export const pricingTierLabels: Readonly<Record<PricingTier, string>> = {
  OCCASIONAL: "ocasional",
  BUSINESS_1_49: "empresarial de 1 a 49 envíos al mes",
  BUSINESS_50_199: "empresarial de 50 a 199 envíos al mes",
  BUSINESS_200_499: "empresarial de 200 a 499 envíos al mes",
  BUSINESS_500_PLUS: "empresarial de 500 envíos al mes o más",
  CUSTOM: "personalizada",
};

const breakdownLineLabels: Readonly<Record<string, string>> = {
  BASE_TARIFF: "Tarifa base (IVA incluido)",
};

/** A quote breakdown line in Spanish; a line type this screen does not know is never shown raw. */
export function breakdownLineLabel(lineType: string | null): string {
  if (lineType === null) return "Concepto";
  return Object.hasOwn(breakdownLineLabels, lineType) ? breakdownLineLabels[lineType] : "Otro concepto";
}
