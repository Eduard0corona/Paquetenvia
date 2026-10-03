import { isAcceptanceVersion, type AcceptanceVersions, acceptanceVersionsUnavailableMessage } from "./acceptance-versions";
import { formatMxnCentsWithCurrency, maximumCodExpectedCents, parseMxnToCents } from "./money";
import { buildServiceWindow, type ServiceWindowBody } from "./service-window";

/**
 * /ops/orders/new (AI-07 create_order) against AI-05 createQuote and createOrder.
 * Parsers fail closed on anything outside the AI-05 schemas; money stays integer cents.
 */

export const serviceTypes = ["SAME_DAY", "URGENT", "SCHEDULED_ROUTE"] as const;
export type ServiceType = (typeof serviceTypes)[number];
export const payerTypes = ["SENDER", "RECIPIENT", "BUSINESS_ACCOUNT"] as const;
export type PayerType = (typeof payerTypes)[number];
export const acceptanceChannels = ["WEB", "PWA", "ASSISTED", "API"] as const;
export type AcceptanceChannel = (typeof acceptanceChannels)[number];
/** /ops/orders/new is operator-assisted by definition; the channel is never chosen. */
export const operatorAcceptanceChannel: AcceptanceChannel = "ASSISTED";
export const quoteStatuses = ["ACTIVE", "USED", "EXPIRED", "REVOKED"] as const;
export type QuoteStatus = (typeof quoteStatuses)[number];
export const pricingTiers = [
  "OCCASIONAL",
  "BUSINESS_1_49",
  "BUSINESS_50_199",
  "BUSINESS_200_499",
  "BUSINESS_500_PLUS",
  "CUSTOM",
] as const;
export type PricingTier = (typeof pricingTiers)[number];

/** AI05-INPUT-LIMITS: CreateQuoteRequest packages minItems 1, maxItems 20. */
export const maximumPackages = 20;
/**
 * AI-07 create_order.low_price_guard: block confirmation at <= 52 MXN total, IVA included
 * (GATE-011-VAT-INCLUDED-2026-09-29: "52 con IVA incluido"), unless the route flag
 * (consolidated_route, AI-02 low_price_guard) or an authorized override. The guard reads the
 * VAT-included total the customer pays, never the pre-tax net. The override is the quote's
 * `low_price_authorization` (LOW-PRICE-MANUAL-AUTH-2026-10-02), sent on createQuote, never on
 * createOrder; the server decides whether the quote carries it.
 */
export const lowPriceGuardTotalCents = 5_200;

/** LOW-PRICE-MANUAL-AUTH-2026-10-02: AI-05 LowPriceAuthorizationInput.reason maxLength. */
export const lowPriceAuthorizationReasonMaximum = 200;

/** GATE-011-VAT-INCLUDED-2026-09-29: every price is presented with IVA included. */
export const vatIncludedLabel = "IVA incluido";

export interface Money {
  readonly currency: "MXN";
  readonly amount_cents: number;
}

export interface QuoteBreakdownLine {
  readonly line_type: string | null;
  readonly amount_cents: number | null;
}

/**
 * LOW-PRICE-MANUAL-AUTH-2026-10-02: the manual authorization a quote carries. `actor_id`
 * and `reason` are `null` when the API withholds them from the active role.
 */
export interface QuoteLowPriceAuthorization {
  readonly valid_until: string;
  readonly actor_id: string | null;
  readonly reason: string | null;
}

export interface Quote {
  readonly id: string;
  readonly net: Money;
  readonly tax: Money;
  readonly total: Money;
  readonly rule_ids: readonly string[];
  readonly breakdown: readonly QuoteBreakdownLine[];
  readonly expires_at: string;
  readonly service_type: ServiceType;
  readonly consolidated_route: boolean;
  readonly package_count: number;
  readonly pricing_tier: PricingTier;
  readonly minimum_total_cents_snapshot: number;
  readonly pricing_policy_version: string;
  readonly status: QuoteStatus;
  readonly low_price_authorization: QuoteLowPriceAuthorization | null;
}

export interface CreatedOrder {
  readonly id: string;
  readonly public_id: string;
  readonly status: string;
  readonly version: number;
  readonly price_net: Money;
  readonly total: Money;
  readonly service_type: ServiceType;
  /** ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02: null means the zone's schedule applies. */
  readonly service_window: ServiceWindowBody | null;
}

export interface AddressDraft {
  readonly addressText: string;
  readonly contactName: string;
  readonly phone: string;
  readonly lat: string;
  readonly lng: string;
  readonly references: string;
}

export interface PackageDraft {
  readonly description: string;
  readonly weightGrams: string;
  readonly declaredValue: string;
  readonly lengthMm: string;
  readonly widthMm: string;
  readonly heightMm: string;
}

export interface QuoteDraft {
  readonly clientAccountId: string;
  readonly origin: AddressDraft;
  readonly destination: AddressDraft;
  readonly serviceType: string;
  readonly consolidatedRoute: boolean;
  readonly packages: readonly PackageDraft[];
  /**
   * LOW-PRICE-MANUAL-AUTH-2026-10-02: "Autorizar envío de bajo monto". Only DISPATCHER and
   * PLATFORM_ADMIN see the option; the reason is operational text without personal data.
   */
  readonly authorizeLowPrice?: boolean;
  readonly lowPriceReason?: string;
}

/** What the operator captures; versions and channel are never operator input. */
export interface AcceptanceDraft {
  readonly payerType: string;
  readonly accepted: boolean;
  /**
   * ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02: the dispatcher ticked that the shipment holds no prohibited goods.
   * It is sent as `restricted_goods_acknowledged: true` and the server records it on the order.
   */
  readonly restrictedGoodsAcknowledged: boolean;
  /**
   * D6-COD-EXPECTED: optional cash-on-delivery amount typed in MXN (e.g. `150.50`);
   * empty or absent means no COD. Converted to integer cents without floating point.
   */
  readonly codAmount?: string;
  /**
   * ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02: optional delivery window as `datetime-local`
   * values read as America/Mazatlan wall-clock time; both empty means no window.
   */
  readonly serviceWindowFrom?: string;
  readonly serviceWindowTo?: string;
}

interface AddressBody {
  address_text: string;
  contact_name: string;
  phone: string;
  lat: number;
  lng: number;
  references?: string;
}

interface PackageBody {
  description: string;
  weight_grams: number;
  declared_value_cents: number;
  length_mm?: number;
  width_mm?: number;
  height_mm?: number;
}

export interface CreateQuoteBody {
  client_account_id?: string;
  origin: AddressBody;
  destination: AddressBody;
  service_type: ServiceType;
  consolidated_route: boolean;
  packages: PackageBody[];
  low_price_authorization?: { reason: string };
}

export interface CreateOrderBody {
  quote_id: string;
  payer_type: PayerType;
  acceptance: {
    terms_version: string;
    privacy_version: string;
    accepted_at: string;
    acceptance_channel: AcceptanceChannel;
  };
  /** D6-COD-EXPECTED: integer MXN cents, sent only when the order carries COD. */
  cod_expected_cents?: number;
  /** ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02: required, and only `true` is valid. */
  restricted_goods_acknowledged: true;
  /** ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02: UTC instants, sent only when a window was typed. */
  service_window?: ServiceWindowBody;
}

export type DraftResult<T> =
  | { readonly ok: true; readonly body: T }
  | { readonly ok: false; readonly errors: readonly string[] };

const uuidPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const coordinatePattern = /^-?\d{1,3}(?:\.\d{1,10})?$/;
const positiveIntegerPattern = /^\d{1,9}$/;

/**
 * Builds the AI-05 CreateQuoteRequest from the form. Errors are Spanish field
 * messages without the typed values, so nothing personal ends up in a message.
 */
export function buildCreateQuoteBody(draft: QuoteDraft): DraftResult<CreateQuoteBody> {
  const errors: string[] = [];
  const clientAccountId = draft.clientAccountId.trim();
  if (clientAccountId !== "" && !uuidPattern.test(clientAccountId))
    errors.push("La cuenta cliente debe ser un UUID.");
  const origin = address(draft.origin, "origen", errors);
  const destination = address(draft.destination, "destino", errors);
  if (!(serviceTypes as readonly string[]).includes(draft.serviceType))
    errors.push("Selecciona el tipo de servicio.");
  if (draft.packages.length < 1 || draft.packages.length > maximumPackages)
    errors.push(`Captura entre 1 y ${maximumPackages} paquetes.`);
  const packages = draft.packages.map((item, index) =>
    packageBody(item, index + 1, errors),
  );
  const lowPriceReason = draft.authorizeLowPrice === true ? (draft.lowPriceReason ?? "").trim() : null;
  if (lowPriceReason !== null && lowPriceReason === "")
    errors.push("Captura el motivo de la autorización de bajo monto.");
  if (lowPriceReason !== null && lowPriceReason.length > lowPriceAuthorizationReasonMaximum)
    errors.push(`El motivo de la autorización admite ${lowPriceAuthorizationReasonMaximum} caracteres.`);
  if (lowPriceReason !== null && /[\u0000-\u001f\u007f-\u009f]/.test(lowPriceReason))
    errors.push("El motivo de la autorización debe ser una sola línea.");
  if (errors.length > 0) return { ok: false, errors };
  const body: CreateQuoteBody = {
    origin: origin!,
    destination: destination!,
    service_type: draft.serviceType as ServiceType,
    consolidated_route: draft.consolidatedRoute,
    packages: packages as PackageBody[],
  };
  if (clientAccountId !== "") body.client_account_id = clientAccountId;
  if (lowPriceReason !== null) body.low_price_authorization = { reason: lowPriceReason };
  return { ok: true, body };
}

function address(
  draft: AddressDraft,
  label: string,
  errors: string[],
): AddressBody | null {
  const before = errors.length;
  const addressText = draft.addressText.trim();
  const contactName = draft.contactName.trim();
  const phone = normalizeMexicanPhone(draft.phone);
  const references = draft.references.trim();
  if (addressText.length < 8)
    errors.push(`La dirección de ${label} requiere al menos 8 caracteres.`);
  if (contactName === "") errors.push(`Captura el contacto de ${label}.`);
  if (draft.phone.trim() === "") errors.push(`Captura el teléfono de ${label}.`);
  else if (phone === null)
    errors.push(
      `El teléfono de ${label} debe tener 10 dígitos de México; puedes anteponer +52 y separarlos con espacios o guiones.`,
    );
  if (references.length > 500)
    errors.push(`Las referencias de ${label} admiten 500 caracteres.`);
  const lat = coordinate(draft.lat, 90);
  const lng = coordinate(draft.lng, 180);
  if (lat === null || lng === null)
    errors.push(`Captura latitud y longitud válidas de ${label}.`);
  if (errors.length > before) return null;
  const body: AddressBody = {
    address_text: addressText,
    contact_name: contactName,
    phone: phone!,
    lat: lat!,
    lng: lng!,
  };
  if (references !== "") body.references = references;
  return body;
}

function coordinate(text: string, limit: number): number | null {
  const trimmed = text.trim();
  if (!coordinatePattern.test(trimmed)) return null;
  const value = Number(trimmed);
  return Number.isFinite(value) && value >= -limit && value <= limit ? value : null;
}

function packageBody(
  draft: PackageDraft,
  position: number,
  errors: string[],
): PackageBody | null {
  const before = errors.length;
  const description = draft.description.trim();
  if (description === "" || description.length > 250)
    errors.push(`El paquete ${position} requiere una descripción de hasta 250 caracteres.`);
  const weight = positiveInteger(draft.weightGrams);
  if (weight === null) errors.push(`El peso del paquete ${position} debe ser un entero en gramos mayor que 0.`);
  const declared = parseMxnToCents(draft.declaredValue);
  if (declared === null)
    errors.push(`El valor declarado del paquete ${position} debe ser un monto en MXN con hasta 2 decimales.`);
  const dimensions: Array<[keyof PackageDraft, "length_mm" | "width_mm" | "height_mm"]> = [
    ["lengthMm", "length_mm"],
    ["widthMm", "width_mm"],
    ["heightMm", "height_mm"],
  ];
  const optional: Partial<Record<"length_mm" | "width_mm" | "height_mm", number>> = {};
  for (const [field, key] of dimensions) {
    const text = draft[field].trim();
    if (text === "") continue;
    const value = positiveInteger(text);
    if (value === null)
      errors.push(`Las medidas del paquete ${position} deben ser enteros en milímetros mayores que 0.`);
    else optional[key] = value;
  }
  if (errors.length > before) return null;
  return {
    description,
    weight_grams: weight!,
    declared_value_cents: declared!,
    ...optional,
  };
}

function positiveInteger(text: string): number | null {
  const trimmed = text.trim();
  if (!positiveIntegerPattern.test(trimmed)) return null;
  const value = Number(trimmed);
  return Number.isSafeInteger(value) && value >= 1 && value <= 2_147_483_647
    ? value
    : null;
}

/**
 * AI-05 CreateOrderRequest; `acceptedAt` is the client-observed acceptance moment.
 * The versions come only from the owner-set server configuration and the channel is
 * always ASSISTED, so the acceptance record cannot be shaped by the operator.
 */
export function buildCreateOrderBody(
  quoteId: string,
  draft: AcceptanceDraft,
  versions: AcceptanceVersions | null,
  acceptedAt: Date,
): DraftResult<CreateOrderBody> {
  const errors: string[] = [];
  if (!uuidPattern.test(quoteId)) errors.push("Cotiza de nuevo antes de confirmar.");
  if (!(payerTypes as readonly string[]).includes(draft.payerType))
    errors.push("Selecciona quién paga.");
  if (
    versions === null ||
    !isAcceptanceVersion(versions.termsVersion) ||
    !isAcceptanceVersion(versions.privacyVersion)
  )
    errors.push(acceptanceVersionsUnavailableMessage);
  if (!draft.accepted)
    errors.push("Confirma que el cliente aceptó términos y aviso de privacidad.");
  if (draft.restrictedGoodsAcknowledged !== true) errors.push(restrictedGoodsRequiredMessage);
  if (Number.isNaN(acceptedAt.getTime())) errors.push("La hora de aceptación no es válida.");
  const codCents = codExpectedCents(draft.codAmount);
  if (codCents === null) errors.push(invalidCodAmountMessage);
  else if (codCents > maximumCodExpectedCents) errors.push(codAmountAboveCapMessage);
  const serviceWindow = buildServiceWindow(draft.serviceWindowFrom, draft.serviceWindowTo, acceptedAt);
  if (!serviceWindow.ok) errors.push(serviceWindow.error);
  if (errors.length > 0) return { ok: false, errors };
  const body: CreateOrderBody = {
    quote_id: quoteId,
    payer_type: draft.payerType as PayerType,
    acceptance: {
      terms_version: versions!.termsVersion,
      privacy_version: versions!.privacyVersion,
      accepted_at: acceptedAt.toISOString(),
      acceptance_channel: operatorAcceptanceChannel,
    },
    restricted_goods_acknowledged: true,
  };
  // Zero is "no COD": the field is left out, which the server treats identically.
  if (codCents! > 0) body.cod_expected_cents = codCents!;
  // Both empty is "no window": the field is left out and the zone's schedule applies.
  if (serviceWindow.ok && serviceWindow.window !== null) body.service_window = serviceWindow.window;
  return { ok: true, body };
}

export const restrictedGoodsRequiredMessage =
  "Confirma que el envío no contiene artículos prohibidos.";

/** ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02: AI-05 AddressInput.phone bound on the raw text. */
export const maximumPhoneInputLength = 32;

/**
 * ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02 and ORD-PHONE-PLUS52-LOCATIONS-2026-10-03: a 10-digit Mexican phone.
 * ASCII spaces and hyphens are removed, and so is one optional leading `+52` (separators allowed before and after
 * it); what remains must be exactly ten ASCII digits, so another country prefix, `52` without `+`, parentheses,
 * dots and any other character are rejected (`null`). Mirrors the server's MexicanPhonePolicy.
 */
export function normalizeMexicanPhone(text: string): string | null {
  if (text.length === 0 || text.length > maximumPhoneInputLength) return null;
  const national = text.replace(/^[ -]*\+52/, "");
  const digits = national.replace(/[ -]/g, "");
  return /^[0-9]{10}$/.test(digits) ? digits : null;
}

export const invalidCodAmountMessage =
  "El cobro contra entrega debe ser un monto en MXN con hasta 2 decimales, sin signos, comas ni símbolos.";

/** COD-CAP-20000-2026-10-02: the server rejects more than 20,000.00 MXN per order. */
export const codAmountAboveCapMessage =
  `El cobro contra entrega no puede superar ${formatMxnCentsWithCurrency(maximumCodExpectedCents)} por pedido.`;

/**
 * D6-COD-EXPECTED: the typed COD in MXN as integer cents (0 when left empty), or
 * `null` when the text is not a plain non-negative amount with at most two decimals.
 * Uses the integer-only {@link parseMxnToCents}; no floating-point value is involved.
 */
export function codExpectedCents(text: string | undefined): number | null {
  const trimmed = (text ?? "").trim();
  if (trimmed === "") return 0;
  return parseMxnToCents(trimmed);
}

export type ConfirmationBlocker = "inactive" | "expired" | "low_price";

/**
 * AI-07 create_order: quote not expired and the low price guard. A quote that carries the
 * manual authorization (LOW-PRICE-MANUAL-AUTH-2026-10-02) passes the guard; its validity is
 * the quote's own expiry, which the expiry blocker already enforces.
 */
export function evaluateConfirmation(
  quote: Quote,
  now: Date,
): readonly ConfirmationBlocker[] {
  const blockers: ConfirmationBlocker[] = [];
  if (quote.status !== "ACTIVE") blockers.push("inactive");
  if (Date.parse(quote.expires_at) <= now.getTime()) blockers.push("expired");
  if (
    quote.total.amount_cents <= lowPriceGuardTotalCents &&
    !quote.consolidated_route &&
    quote.low_price_authorization === null
  )
    blockers.push("low_price");
  return blockers;
}

export const confirmationBlockerLabels: Readonly<Record<ConfirmationBlocker, string>> = {
  inactive: "La cotización ya no está activa; cotiza de nuevo.",
  expired: "La cotización expiró; cotiza de nuevo.",
  low_price:
    "Total de 52 MXN o menos (IVA incluido): solo se confirma con ruta consolidada o con autorización de envío de bajo monto.",
};

/** LOW-PRICE-MANUAL-AUTH-2026-10-02: Spanish copy for the uniform 409 of createQuote. */
export const lowPriceAuthorizationNotNeededMessage =
  "La autorización de bajo monto no aplica a esta cotización (ruta consolidada o total mayor a 52 MXN con IVA incluido). Cotiza sin autorizar.";

// ---------------------------------------------------------------------------
// Response parsers

export class CreateOrderContractError extends Error {
  public constructor() {
    super("La respuesta no cumple el contrato.");
    this.name = "CreateOrderContractError";
  }
}

export function parseQuote(value: unknown): Quote {
  const object = knownObject(
    value,
    [
      "id",
      "net",
      "tax",
      "total",
      "rule_ids",
      "expires_at",
      "origin_location_id",
      "destination_location_id",
      "service_type",
      "consolidated_route",
      "package_snapshot",
      "pricing_tier",
      "minimum_total_cents_snapshot",
      "pricing_policy_version",
      "status",
      "city_id",
    ],
    ["breakdown", "service_area_id", "request_snapshot_redacted", "low_price_authorization"],
  );
  uuid(object.origin_location_id);
  uuid(object.destination_location_id);
  uuid(object.city_id);
  if (object.service_area_id !== undefined && object.service_area_id !== null)
    uuid(object.service_area_id);
  const packages = array(object.package_snapshot);
  if (packages.length < 1 || packages.length > maximumPackages) fail();
  const ruleIds = array(object.rule_ids).map(uuid);
  const breakdown =
    object.breakdown === undefined ? [] : array(object.breakdown).map(breakdownLine);
  if (breakdown.length > 100) fail();
  // request_snapshot_redacted is validated as an object and deliberately not kept.
  if (
    object.request_snapshot_redacted !== undefined &&
    (typeof object.request_snapshot_redacted !== "object" ||
      object.request_snapshot_redacted === null ||
      Array.isArray(object.request_snapshot_redacted))
  )
    fail();
  return {
    id: uuid(object.id),
    net: money(object.net),
    tax: money(object.tax),
    total: money(object.total),
    rule_ids: ruleIds,
    breakdown,
    expires_at: utc(object.expires_at),
    service_type: oneOf(object.service_type, serviceTypes),
    consolidated_route: boolean(object.consolidated_route),
    package_count: packages.length,
    pricing_tier: oneOf(object.pricing_tier, pricingTiers),
    minimum_total_cents_snapshot: nonNegativeInteger(object.minimum_total_cents_snapshot),
    pricing_policy_version: boundedString(object.pricing_policy_version, 1, 128),
    status: oneOf(object.status, quoteStatuses),
    low_price_authorization: lowPriceAuthorization(object.low_price_authorization),
  };
}

function lowPriceAuthorization(value: unknown): QuoteLowPriceAuthorization | null {
  if (value === undefined || value === null) return null;
  const object = knownObject(value, ["valid_until"], ["actor_id", "reason"]);
  return {
    valid_until: utc(object.valid_until),
    actor_id: object.actor_id === undefined ? null : uuid(object.actor_id),
    reason:
      object.reason === undefined
        ? null
        : boundedString(object.reason, 1, lowPriceAuthorizationReasonMaximum),
  };
}

export function parseCreatedOrder(value: unknown): CreatedOrder {
  const object = knownObject(
    value,
    [
      "id",
      "public_id",
      "owner_org_id",
      "status",
      "price_net",
      "version",
      "origin_location_id",
      "destination_location_id",
      "service_type",
      "pricing_tier",
      "total",
      "city_id",
    ],
    [
      "operator_org_id",
      "quote_id",
      "service_area_id",
      "claim_window_ends_at",
      "finalized_at",
      "service_window",
    ],
  );
  uuid(object.owner_org_id);
  if (object.operator_org_id !== undefined && object.operator_org_id !== null)
    uuid(object.operator_org_id);
  if (object.quote_id !== undefined) uuid(object.quote_id);
  oneOf(object.pricing_tier, pricingTiers);
  const version = object.version;
  if (!Number.isSafeInteger(version) || (version as number) < 1) fail();
  return {
    id: uuid(object.id),
    public_id: boundedString(object.public_id, 1, 128),
    status: boundedString(object.status, 1, 32),
    version: version as number,
    price_net: money(object.price_net),
    total: money(object.total),
    service_type: oneOf(object.service_type, serviceTypes),
    service_window:
      object.service_window === undefined || object.service_window === null
        ? null
        : parseServiceWindow(object.service_window),
  };
}

/** ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02: exactly `from` and `to`, offset-qualified, from before to. */
export function parseServiceWindow(value: unknown): ServiceWindowBody {
  const object = knownObject(value, ["from", "to"], []);
  const from = utc(object.from);
  const to = utc(object.to);
  if (Date.parse(from) >= Date.parse(to)) fail();
  return { from, to };
}

function breakdownLine(value: unknown): QuoteBreakdownLine {
  if (typeof value !== "object" || value === null || Array.isArray(value)) fail();
  const object = value as Record<string, unknown>;
  const lineType =
    object.line_type === undefined ? null : boundedString(object.line_type, 1, 64);
  let amount: number | null = null;
  if (object.amount_cents !== undefined) {
    if (!Number.isSafeInteger(object.amount_cents)) fail();
    amount = object.amount_cents as number;
  }
  return { line_type: lineType, amount_cents: amount };
}

function knownObject(
  value: unknown,
  required: readonly string[],
  optional: readonly string[],
): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) fail();
  const object = value as Record<string, unknown>;
  const allowed = new Set([...required, ...optional]);
  for (const key of Object.keys(object)) if (!allowed.has(key)) fail();
  for (const key of required) if (!(key in object)) fail();
  return object;
}

function money(value: unknown): Money {
  const object = knownObject(value, ["currency", "amount_cents"], []);
  if (object.currency !== "MXN") fail();
  return { currency: "MXN", amount_cents: nonNegativeInteger(object.amount_cents) };
}

function nonNegativeInteger(value: unknown): number {
  if (!Number.isSafeInteger(value) || (value as number) < 0) fail();
  return value as number;
}

function array(value: unknown): readonly unknown[] {
  if (!Array.isArray(value)) fail();
  return value;
}

function boundedString(value: unknown, minimum: number, maximum: number): string {
  if (typeof value !== "string" || value.length < minimum || value.length > maximum)
    fail();
  return value;
}

function uuid(value: unknown): string {
  const text = boundedString(value, 36, 36);
  if (!uuidPattern.test(text) || text === "00000000-0000-0000-0000-000000000000")
    fail();
  return text;
}

function utc(value: unknown): string {
  const text = boundedString(value, 20, 40);
  if (!/(?:Z|[+-]\d{2}:\d{2})$/.test(text) || Number.isNaN(Date.parse(text))) fail();
  return text;
}

function boolean(value: unknown): boolean {
  if (typeof value !== "boolean") fail();
  return value;
}

function oneOf<const T extends readonly string[]>(value: unknown, values: T): T[number] {
  if (typeof value !== "string" || !values.includes(value)) fail();
  return value as T[number];
}

function fail(): never {
  throw new CreateOrderContractError();
}
