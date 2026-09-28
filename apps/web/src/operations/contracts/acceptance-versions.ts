/**
 * Terms and privacy notice versions the customer accepts on an operator-assisted order.
 * They are owner-set server configuration (`PAQUETERIA_TERMS_VERSION`,
 * `PAQUETERIA_PRIVACY_VERSION`), read by the page on the server and never typed by
 * the operator, so an order cannot record an acceptance of a version that is not
 * the one in force.
 */
export interface AcceptanceVersions {
  readonly termsVersion: string;
  readonly privacyVersion: string;
}

/** AI-05 CreateOrderRequest.acceptance terms_version / privacy_version (AI05-INPUT-LIMITS). */
const versionPattern = /^[A-Za-z0-9._-]{1,64}$/;
/** Deployment placeholder for a value the owner has not chosen yet (ENV-001). */
const ownerSentinel = "owner_decision_required";

export const acceptanceVersionsUnavailableMessage =
  "Faltan las versiones vigentes de términos y aviso de privacidad; contacta al administrador.";

export function isAcceptanceVersion(value: unknown): value is string {
  return (
    typeof value === "string" &&
    versionPattern.test(value) &&
    value.toLowerCase() !== ownerSentinel
  );
}

/**
 * Validates both configured values. Missing, malformed or placeholder values yield
 * `null`, which disables order confirmation; there is never a default version.
 */
export function resolveAcceptanceVersions(
  terms: string | undefined,
  privacy: string | undefined,
): AcceptanceVersions | null {
  if (!isAcceptanceVersion(terms) || !isAcceptanceVersion(privacy)) return null;
  return { termsVersion: terms, privacyVersion: privacy };
}
