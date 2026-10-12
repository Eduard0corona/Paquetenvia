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

export type DriverRejectionCode = TransitionRejectionCode | DriverCodRejectionCode;

export const driverRejectionCodes: readonly DriverRejectionCode[] = Object.freeze([
  ...new Set<DriverRejectionCode>([
    ...transitionRejectionCodes,
    ...driverCodRejectionCodes,
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
 * The Spanish message for a queued action held for attention. A refused cash
 * collection uses the cash messages; every other action uses the ORD-002 rule
 * message of its transition (for example `COD_NOT_RECORDED`: the delivery
 * cannot be confirmed before the cash is recorded). Unknown or absent codes
 * keep the generic message.
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
