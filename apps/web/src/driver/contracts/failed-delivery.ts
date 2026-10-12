import {
  incidentReasonLabels,
  type IncidentReasonCode,
} from "../../operations/contracts/incident";

/**
 * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: the closed list of "No se pudo
 * entregar" (INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11, "Lista cerrada").
 * The four reasons of "¿Qué pasó?", then "Otro…" with its three. The labels are
 * the ones of Incidents.Domain.IncidentDriverReportPolicy, shared with the
 * dispatcher screens, so one reason reads the same for the driver and for
 * dispatch. The driver never types text: the server builds the description
 * from the reason and derives the severity from it.
 */
export const driverFailedDeliveryPrimaryReasons = [
  "RECIPIENT_ABSENT",
  "ADDRESS_NOT_FOUND",
  "RECIPIENT_REFUSED",
  "PAYMENT_UNAVAILABLE",
] as const satisfies readonly IncidentReasonCode[];

/** The reasons "Otro…" reveals. */
export const driverFailedDeliveryOtherReasons = [
  "ACCESS_RESTRICTED",
  "PACKAGE_DAMAGED",
  "SECURITY_RISK",
] as const satisfies readonly IncidentReasonCode[];

export const driverFailedDeliveryReasons = [
  ...driverFailedDeliveryPrimaryReasons,
  ...driverFailedDeliveryOtherReasons,
] as const;

export type DriverFailedDeliveryReason =
  (typeof driverFailedDeliveryReasons)[number];

/** The incident type of every driver report (AI-05 OpenIncidentRequest.type). */
export const DriverFailedDeliveryIncidentType = "FAILED_DELIVERY_ATTEMPT";

/** The reason that exists only when the stop collects cash at the door. */
export const DriverFailedDeliveryPaymentReason = "PAYMENT_UNAVAILABLE";

export function isDriverFailedDeliveryReason(
  value: unknown,
): value is DriverFailedDeliveryReason {
  return (
    typeof value === "string" &&
    (driverFailedDeliveryReasons as readonly string[]).includes(value)
  );
}

/** "No había nadie", "Dirección incorrecta", … exactly as the closed list names them. */
export function driverFailedDeliveryReasonLabel(
  reason: DriverFailedDeliveryReason,
): string {
  return incidentReasonLabels[reason];
}

/**
 * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12 (owner answer "Solo si hay cobro
 * (Recommended)"): "No pagó el cobro" is offered only on a stop with cash on
 * delivery (DriverStop.cod not null); every stop offers the other reasons and
 * "Otro…".
 */
export function driverFailedDeliveryReasonsFor(stopHasCod: boolean): {
  readonly primary: readonly DriverFailedDeliveryReason[];
  readonly other: readonly DriverFailedDeliveryReason[];
} {
  return {
    primary: driverFailedDeliveryPrimaryReasons.filter(
      (reason) => stopHasCod || reason !== DriverFailedDeliveryPaymentReason,
    ),
    other: driverFailedDeliveryOtherReasons,
  };
}

/** Whether the reason may be reported on a stop with (or without) cash on delivery. */
export function driverFailedDeliveryReasonAllowed(
  reason: DriverFailedDeliveryReason,
  stopHasCod: boolean,
): boolean {
  return stopHasCod || reason !== DriverFailedDeliveryPaymentReason;
}
