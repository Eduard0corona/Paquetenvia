import {
  transitionRejectionCodes,
  transitionRejectionMessage,
  type TransitionRejectionCode,
} from "../../operations/contracts/order-transitions";
import { formatMxnCents } from "../../operations/contracts/money";

/**
 * Stable rule codes the server attaches to a refused queued action, kept with
 * the operation so the driver reads why it stopped, in Spanish, after a reload:
 * the AI-05 `TransitionConflictProblem` codes (ORD-002-GUARD-CODES-2026-10-05)
 * for a refused transition and the `FinanceConflictProblem` codes for a refused
 * cash collection. Any other value is never stored; the generic message stays.
 */
export const driverCodRejectionCodes = [
  "INVALID_REQUEST",
  "CONFLICT",
  "COD_NOT_EXPECTED",
  "COD_AMOUNT_MISMATCH",
  "COD_ALREADY_RECORDED",
  "COD_STATE_CONFLICT",
  "ORDER_STATE_CONFLICT",
] as const;

export type DriverCodRejectionCode = (typeof driverCodRejectionCodes)[number];

/**
 * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: the AI-05 IncidentConflictProblem
 * codes a refused failed-delivery report keeps (openIncident). OFFLINE_OPERATION_EXPIRED
 * is never kept: an expired report leaves the queue (OPS-003).
 */
export const driverIncidentRejectionCodes = [
  "INVALID_REQUEST",
  "IDEMPOTENCY_CONFLICT",
  "ORDER_STATE_NOT_ALLOWED",
  "EVIDENCE_NOT_AVAILABLE",
  "INCIDENT_STATE_CONFLICT",
  "CONFLICT",
] as const;

export type DriverIncidentRejectionCode =
  (typeof driverIncidentRejectionCodes)[number];

export type DriverRejectionCode =
  | TransitionRejectionCode
  | DriverCodRejectionCode
  | DriverIncidentRejectionCode;

export const driverRejectionCodes: readonly DriverRejectionCode[] = Object.freeze([
  ...new Set<DriverRejectionCode>([
    ...transitionRejectionCodes,
    ...driverCodRejectionCodes,
    ...driverIncidentRejectionCodes,
  ]),
]);

export function isDriverRejectionCode(value: unknown): value is DriverRejectionCode {
  return (
    typeof value === "string" &&
    (driverRejectionCodes as readonly string[]).includes(value)
  );
}

/** The code of a refused transition to keep, or null when it is not a known rule code. */
export function transitionRejectionCodeToKeep(
  code: string | null | undefined,
): DriverRejectionCode | null {
  return typeof code === "string" &&
    (transitionRejectionCodes as readonly string[]).includes(code)
    ? (code as TransitionRejectionCode)
    : null;
}

/** The code of a refused cash collection to keep, or null when it is not a known finance code. */
export function codRejectionCodeToKeep(
  code: string | null | undefined,
): DriverCodRejectionCode | null {
  return typeof code === "string" &&
    (driverCodRejectionCodes as readonly string[]).includes(code)
    ? (code as DriverCodRejectionCode)
    : null;
}

/** The code of a refused failed-delivery report to keep, or null when it is not a known incident code. */
export function incidentRejectionCodeToKeep(
  code: string | null | undefined,
): DriverIncidentRejectionCode | null {
  return typeof code === "string" &&
    (driverIncidentRejectionCodes as readonly string[]).includes(code)
    ? (code as DriverIncidentRejectionCode)
    : null;
}

/** Shown when a queued action stopped without a known rule code. */
export const driverGenericConflictMessage =
  "Esta operación no pudo aplicarse porque la parada cambió. Revisa el estado confirmado antes de continuar.";

const codMessages: Readonly<Record<DriverCodRejectionCode, string>> = {
  INVALID_REQUEST:
    "El servidor no aceptó los datos del cobro. Descarta esta acción y vuelve a registrar el cobro.",
  CONFLICT:
    "El cobro chocó con otra operación. Reintenta la misma acción o avisa a despacho.",
  COD_NOT_EXPECTED:
    "La orden ya no tiene cobro contra entrega. Descarta esta acción y avisa a despacho.",
  COD_AMOUNT_MISMATCH:
    "El monto a cobrar cambió. Descarta esta acción y revisa el cobro de la parada.",
  COD_ALREADY_RECORDED:
    "Esta orden ya tiene un registro de cobro que no permite confirmar la entrega. Avisa a despacho.",
  COD_STATE_CONFLICT:
    "El estado del cobro no permite registrarlo. Avisa a despacho.",
  ORDER_STATE_CONFLICT:
    "El estado de la orden ya no permite registrar el cobro. Revisa la parada.",
};

/**
 * openIncident refused the report with a rule code: the incident was not
 * opened. INVALID_REQUEST is what the server answers to the phone's short
 * report from a person it treats as dispatch (also DISPATCHER or
 * PLATFORM_ADMIN in the organization, owner answer "Registrar desde Despacho
 * (Recommended)"); its message never blames the phone's clock.
 */
const incidentMessages: Readonly<Record<DriverIncidentRejectionCode, string>> = {
  INVALID_REQUEST:
    "El servidor no aceptó este reporte desde el teléfono. Si también eres despacho en esta organización, " +
    "registra el intento desde la orden en Despacho; si no, avisa a despacho. Después descarta esta acción.",
  IDEMPOTENCY_CONFLICT:
    "Este reporte ya se había enviado con otros datos. Descarta esta acción y avisa a despacho.",
  ORDER_STATE_NOT_ALLOWED:
    "La orden ya no está en entrega, así que el reporte no se envió. Revisa la parada.",
  EVIDENCE_NOT_AVAILABLE:
    "La foto del reporte no quedó disponible en el servidor. Descarta esta acción y vuelve a reportar con otra foto.",
  INCIDENT_STATE_CONFLICT:
    "El reporte chocó con otra incidencia de esta orden. Avisa a despacho.",
  CONFLICT:
    "El reporte chocó con otra operación. Reintenta la misma acción o avisa a despacho.",
};

/**
 * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12 (owner answer "No, llama a
 * despacho"): why a failed-delivery report was held before its incident was
 * requested. With the cash recorded on the server the driver calls dispatch,
 * who decides and coordinates returning the cash; with a collection still
 * saved on the phone the driver discards it first if it was a mistake. The app
 * has no dispatch phone number, so none is shown. A report whose incident is
 * (or may be) open uses driverFailedReportAttentionMessage instead.
 */
export const driverCashRecordedReportMessage =
  "El cobro de esta entrega ya está registrado, así que el reporte no se envió. " +
  "Llama a despacho: ellos deciden y coordinan la devolución del cobro.";

export const driverCashSavedReportMessage =
  "Hay un cobro guardado en el teléfono para esta entrega, así que el reporte no se envió. " +
  "Si el cobro fue un error, descártalo y el reporte se enviará; si no, descarta este reporte.";

/**
 * The Spanish message for a queued action held for attention. A refused cash
 * collection uses the cash messages; a failed-delivery report refused at the
 * incident step uses the report messages; every other action uses the ORD-002
 * rule message of its transition (for example `COD_NOT_RECORDED`: the delivery
 * cannot be confirmed before the cash is recorded, or
 * `DISPATCH_DECISION_REQUIRED`: dispatch decides after a failed attempt).
 * Unknown or absent codes keep the generic message.
 */
export function driverRejectionMessage(
  kind: string,
  code: DriverRejectionCode | null,
): string {
  if (code === null) return driverGenericConflictMessage;
  if (kind === "COD_COLLECTION") {
    return Object.hasOwn(codMessages, code)
      ? codMessages[code as DriverCodRejectionCode]
      : driverGenericConflictMessage;
  }
  if (kind === "FAILED_DELIVERY" && Object.hasOwn(incidentMessages, code)) {
    return incidentMessages[code as DriverIncidentRejectionCode];
  }
  if (code === "COD_NOT_RECORDED") {
    return "Falta registrar el cobro contra entrega. Registra el cobro antes de confirmar la entrega.";
  }
  const message = transitionRejectionMessage(code);
  return message === transitionRejectionMessage(null)
    ? driverGenericConflictMessage
    : message;
}

/** "Recibí $1,250.00": the COD button names the exact amount from the stop. */
export function codReceivedLabel(amountCents: number): string {
  return `Recibí ${formatMxnCents(amountCents)}`;
}
