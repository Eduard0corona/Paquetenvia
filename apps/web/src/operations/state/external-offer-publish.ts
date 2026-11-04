import type { ExternalOfferVehicleType } from "../contracts/external-offer-confirmation";
import type { PendingSubmissions, PreparedSubmission } from "./pending-submissions";

/** The external offer "Publicar oferta" sends: commission in integer cents, expiry as its UTC instant. */
export interface ExternalOfferSubmission {
  readonly orderId: string;
  readonly commissionCents: number;
  readonly expiresAt: string;
  readonly vehicleType: ExternalOfferVehicleType;
}

/**
 * Z25 ("Corregirlo"): the Idempotency-Key of "Publicar oferta" is reused only to resend an identical offer that was
 * sent without a confirmation (the PendingSubmissions convention). A changed commission, expiry or vehicle is another
 * offer with its own key, so a key never goes with another body (AI-05: that is IDEMPOTENCY_CONFLICT). Each offer
 * sent without a confirmation keeps its key, so going back to it resends it as it was.
 */
export function prepareExternalOffer(
  submissions: PendingSubmissions,
  offer: ExternalOfferSubmission,
): PreparedSubmission<ExternalOfferSubmission> {
  const fingerprint = JSON.stringify([offer.orderId, offer.commissionCents, offer.expiresAt, offer.vehicleType]);
  return submissions.prepare(fingerprint, fingerprint, () => ({ ...offer }));
}

/** The server confirmed an offer: every kept key goes, and the next offer, even an identical one, gets a new key. */
export function externalOfferPublished(submissions: PendingSubmissions): void {
  submissions.clear();
}
