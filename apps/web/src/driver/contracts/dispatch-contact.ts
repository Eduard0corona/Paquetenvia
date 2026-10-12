/**
 * DRV-DISPATCH-CONTACT-2026-10-12 (project owner, "Sí, configurable
 * (Recommended)": "Un número de despacho por empresa en la configuración del
 * piloto; la app lo muestra como botón "Llamar a despacho". Tú me das el
 * número cuando lo tengas."): GET /api/v1/driver/me/dispatch-contact (AI-05
 * getMyDispatchContact) answers the dispatch number of the session's
 * organization as {"phone_e164": "+52…"}, or {"phone_e164": null} when it has
 * none. The number is a business contact; it is never logged.
 */

export class DispatchContactContractError extends Error {
  public constructor() {
    super("El teléfono de despacho no pudo validarse.");
    this.name = "DispatchContactContractError";
  }
}

/** The label of the link (approved option text). */
export const driverDispatchCallLabel = "Llamar a despacho";

/**
 * AI-05 DriverDispatchContact.phone_e164: +52 and the ten Mexican national
 * digits, the first one 2-9. JavaScript's `$` without the `m` flag only
 * matches at the very end, so a trailing line break is refused.
 */
export const dispatchPhoneE164Pattern = /^\+52[2-9][0-9]{9}$/;

/** A number of the AI-05 form, or null for "none configured". */
export function parseDispatchPhone(value: unknown): string | null {
  if (value === null) return null;
  if (typeof value !== "string" || !dispatchPhoneE164Pattern.test(value)) {
    throw new DispatchContactContractError();
  }
  return value;
}

/** Reads the answer strictly: exactly the member `phone_e164`, a number of the AI-05 form or null. */
export function parseMyDispatchContact(value: unknown): string | null {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new DispatchContactContractError();
  }
  const keys = Object.keys(value);
  if (keys.length !== 1 || keys[0] !== "phone_e164") {
    throw new DispatchContactContractError();
  }
  return parseDispatchPhone((value as { readonly phone_e164: unknown }).phone_e164);
}

/** The `tel:` URI of a number of the AI-05 form (RFC 3966 global number: the E.164 text itself). */
export function driverDispatchCallHref(phoneE164: string): string {
  return `tel:${parseDispatchPhone(phoneE164)}`;
}
