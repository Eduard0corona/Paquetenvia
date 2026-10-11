/**
 * VOICE-001-MASKED-CALLS-2026-10-11: the masked call bridge as the driver PWA sees it. The PWA never receives,
 * shows or stores a phone number of the recipient; the driver's own number is typed once, sent with explicit
 * consent and never returned by the API.
 */

/** The consent text of the "Cuenta" area (AI-07 driver_account.phone), versioned by {@link driverPhoneConsentVersion}. */
export const driverPhoneConsentText =
  "Acepto que este número se use solo para conectar mis llamadas con los destinatarios de mis entregas. " +
  "El destinatario nunca verá mi número y no se usará para nada más. Puedo cambiarlo o borrarlo cuando quiera.";

/** Must equal Drivers.Application.Voice.DriverPhoneConsent.CurrentVersion and AI-05 RegisterDriverPhoneRequest. */
export const driverPhoneConsentVersion = "VOICE-001-CONSENT-V1";

export const recipientCallLabel = "Llamar al destinatario";
export const recipientCallPlacedMessage = "Te estamos llamando a tu celular…";
export const recipientCallOfflineMessage = "Necesitas conexión a internet para llamar";

export const recipientCallReasons = [
  "VOICE_CALLS_DISABLED",
  "ORDER_STATE_NOT_ALLOWED",
  "RECIPIENT_PHONE_UNAVAILABLE",
  "DRIVER_PHONE_REQUIRED",
  "RATE_LIMITED",
] as const;
export type RecipientCallReason = (typeof recipientCallReasons)[number];

export const recipientCallConflictCodes = [
  "INVALID_REQUEST",
  "IDEMPOTENCY_CONFLICT",
  "ORDER_STATE_NOT_ALLOWED",
  "RECIPIENT_PHONE_UNAVAILABLE",
  "DRIVER_PHONE_REQUIRED",
  "DRIVER_PHONE_REJECTED",
] as const;
export type RecipientCallConflictCode = (typeof recipientCallConflictCodes)[number];

export const recipientCallStatuses = ["REQUESTED", "PLACED", "UNCONFIRMED"] as const;
export type RecipientCallStatus = (typeof recipientCallStatuses)[number];

export interface RecipientCallAvailability {
  readonly available: boolean;
  readonly reason: RecipientCallReason | null;
}

export interface RecipientCallRequest {
  readonly call_request_id: string;
  readonly status: RecipientCallStatus;
}

export interface DriverPhoneStatus {
  readonly voice_calls_enabled: boolean;
  readonly registered: boolean;
  readonly consent_version: typeof driverPhoneConsentVersion | null;
  readonly consented_at: string | null;
}

export class VoiceContractError extends Error {
  public constructor() {
    super("La respuesta de llamadas no cumple el contrato esperado.");
    this.name = "VoiceContractError";
  }
}

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const timestamp = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$/;

/** AI-05 RecipientCallAvailability: exactly `available` and a closed `reason` (null when available). */
export function parseRecipientCallAvailability(value: unknown): RecipientCallAvailability {
  const root = object(value);
  exact(root, ["available", "reason"]);
  if (typeof root.available !== "boolean") throw new VoiceContractError();
  if (root.available ? root.reason !== null : !isReason(root.reason)) throw new VoiceContractError();
  return Object.freeze({ available: root.available, reason: root.reason as RecipientCallReason | null });
}

/** AI-05 RecipientCallRequest: the request id and its status, never a number. */
export function parseRecipientCallRequest(value: unknown): RecipientCallRequest {
  const root = object(value);
  exact(root, ["call_request_id", "status"]);
  if (typeof root.call_request_id !== "string" || !uuid.test(root.call_request_id) ||
      root.call_request_id === "00000000-0000-0000-0000-000000000000" ||
      !(recipientCallStatuses as readonly unknown[]).includes(root.status)) {
    throw new VoiceContractError();
  }
  return Object.freeze({ call_request_id: root.call_request_id, status: root.status as RecipientCallStatus });
}

/** AI-05 DriverPhoneStatus: whether a number is stored, never the number. */
export function parseDriverPhoneStatus(value: unknown): DriverPhoneStatus {
  const root = object(value);
  exact(root, ["voice_calls_enabled", "registered", "consent_version", "consented_at"]);
  if (typeof root.voice_calls_enabled !== "boolean" || typeof root.registered !== "boolean") {
    throw new VoiceContractError();
  }
  const consistent = root.registered
    ? root.consent_version === driverPhoneConsentVersion &&
      typeof root.consented_at === "string" && timestamp.test(root.consented_at)
    : root.consent_version === null && root.consented_at === null;
  if (!consistent) throw new VoiceContractError();
  return Object.freeze({
    voice_calls_enabled: root.voice_calls_enabled,
    registered: root.registered,
    consent_version: root.consent_version as DriverPhoneStatus["consent_version"],
    consented_at: root.consented_at as string | null,
  });
}

/**
 * The server rule (ORD-PHONE-PLUS52-LOCATIONS plus a dialable first digit): ten Mexican digits, optional leading
 * +52, ASCII spaces and hyphens; the first digit is 2-9. Returns the ten digits, or null.
 */
export function normalizeDriverPhone(value: string): string | null {
  if (value.length === 0 || value.length > 32) return null;
  let rest = value.replace(/^[ -]+/, "");
  if (rest.startsWith("+")) {
    if (!rest.startsWith("+52")) return null;
    rest = rest.slice(3);
  }
  if (!/^[0-9 -]+$/.test(rest)) return null;
  const digits = rest.replace(/[ -]/g, "");
  return /^[2-9][0-9]{9}$/.test(digits) ? digits : null;
}

/** What the driver reads when the call is not available now; null hides the action without a hint. */
export function recipientCallReasonHint(reason: RecipientCallReason | null): string | null {
  switch (reason) {
    case "DRIVER_PHONE_REQUIRED":
      return "Registra tu celular en Cuenta para poder llamar al destinatario.";
    case "RATE_LIMITED":
      return "Ya pediste varias llamadas. Intenta de nuevo en unos minutos.";
    default:
      return null;
  }
}

export function recipientCallConflictMessage(code: RecipientCallConflictCode | null): string {
  switch (code) {
    case "DRIVER_PHONE_REQUIRED":
      return "Registra tu celular en Cuenta para que podamos llamarte.";
    case "DRIVER_PHONE_REJECTED":
      return "No pudimos llamar a tu celular. Revisa el número registrado en Cuenta.";
    case "RECIPIENT_PHONE_UNAVAILABLE":
      return "Esta entrega no tiene un número de contacto del destinatario.";
    case "ORDER_STATE_NOT_ALLOWED":
      return "Solo puedes llamar al destinatario mientras realizas la entrega.";
    default:
      return "No pudimos pedir la llamada. Intenta de nuevo.";
  }
}

export function isRecipientCallConflictCode(value: unknown): value is RecipientCallConflictCode {
  return (recipientCallConflictCodes as readonly unknown[]).includes(value);
}

function isReason(value: unknown): value is RecipientCallReason {
  return (recipientCallReasons as readonly unknown[]).includes(value);
}

function object(value: unknown): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) throw new VoiceContractError();
  return value as Record<string, unknown>;
}

function exact(value: Record<string, unknown>, names: readonly string[]): void {
  const keys = Object.keys(value);
  if (keys.length !== names.length || names.some((name) => !keys.includes(name))) throw new VoiceContractError();
}
