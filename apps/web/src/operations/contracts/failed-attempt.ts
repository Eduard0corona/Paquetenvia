import {
  dispatchDecidesLabel,
  incidentNextActionLabels,
  incidentReasonLabels,
  type Incident,
} from "./incident";

/**
 * INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11 (AI-07 /ops/orders/:id failed_attempt): why an
 * order in FAILED_ATTEMPT failed and whether dispatch still has to decide how it continues.
 *
 * It reads the newest incident listIncidents returned for the order (newest first). An order
 * leaves the opening states when it fails, so no later incident can exist; the incident shown
 * is the one opened last in the attempt. Only the reason, the decision and the attention
 * deadline are shown: never the description, evidence, identifiers or personal data.
 */
export interface FailedAttemptView {
  /** The es-MX label of the reason, from the driver's closed list. */
  readonly reasonLabel: string;
  /** The driver reported it and dispatch has not decided yet (next_action null). */
  readonly decisionPending: boolean;
  /** What the next step is: "Lo decide despacho" or the next action recorded at report time. */
  readonly nextStepLabel: string;
  /** sla_due_at: the deadline for dispatch to attend the incident. */
  readonly attendBy: string;
}

export const failedAttemptPendingNote =
  "El repartidor reportó el intento. Despacho decide cómo sigue la orden en «Siguiente paso».";

/** The view of the newest incident, or null when the order has none to show. */
export function failedAttemptView(incidents: readonly Incident[]): FailedAttemptView | null {
  const incident = incidents[0];
  if (incident === undefined) return null;
  return {
    reasonLabel: incidentReasonLabels[incident.reason_code],
    decisionPending: incident.next_action === null,
    nextStepLabel:
      incident.next_action === null ? dispatchDecidesLabel : incidentNextActionLabels[incident.next_action],
    attendBy: incident.sla_due_at,
  };
}
