import { formatMxnCentsWithCurrency, parseMxnToCents } from "../../operations/contracts/money";
import { shortId } from "../../lib/short-id";
import type { CodTransaction, PendingCodOrder } from "./cod";
import type { Settlement } from "./settlement";

/** Texts for the confirmation shown before a risky finance action; amounts stay integer cents. */
export interface ConfirmationText {
  readonly title: string;
  readonly description: string;
  readonly confirmLabel: string;
}

function settlementTarget(settlement: Settlement): string {
  return `del repartidor ${shortId(settlement.payee_id)} (${settlement.period_from} a ${settlement.period_to})`;
}

export function settlementApprovalConfirmation(settlement: Settlement): ConfirmationText {
  return {
    title: "¿Aprobar liquidación?",
    description:
      `Se aprobará la liquidación ${settlementTarget(settlement)} por un total de ` +
      `${formatMxnCentsWithCurrency(settlement.total_cents)}. Después ya no se podrán agregar ajustes.`,
    confirmLabel: "Aprobar",
  };
}

export function settlementPaymentConfirmation(settlement: Settlement): ConfirmationText {
  return {
    title: "¿Marcar liquidación como pagada?",
    description:
      `Confirma que ya se pagaron ${formatMxnCentsWithCurrency(settlement.total_cents)} ` +
      `en la liquidación ${settlementTarget(settlement)}.`,
    confirmLabel: "Marcar pagada",
  };
}

/**
 * Adding an adjustment changes the settlement total. Returns `null` when the typed amount is
 * not a valid non-zero amount, so the controller reports the validation error instead.
 */
export function settlementAdjustmentConfirmation(
  settlement: Settlement,
  amountText: string,
): ConfirmationText | null {
  const cents = parseMxnToCents(amountText, { allowNegative: true, allowZero: false });
  if (cents === null) return null;
  const verb = cents < 0 ? "descontará" : "sumará";
  const absolute = cents < 0 ? -cents : cents;
  return {
    title: "¿Agregar ajuste?",
    description:
      `Se ${verb} ${formatMxnCentsWithCurrency(absolute)} a la liquidación ${settlementTarget(settlement)}.`,
    confirmLabel: "Agregar ajuste",
  };
}

export function settlementVoidConfirmation(settlement: Settlement): ConfirmationText {
  return {
    title: "¿Anular liquidación?",
    description:
      `Se anulará la liquidación ${settlementTarget(settlement)} por ` +
      `${formatMxnCentsWithCurrency(settlement.total_cents)}. Esta acción no se puede deshacer.`,
    confirmLabel: "Anular",
  };
}

export function pendingCodReconciliationConfirmation(order: PendingCodOrder): ConfirmationText {
  return {
    title: "¿Conciliar cobro?",
    description:
      `Se conciliará el cobro registrado de la orden ${order.public_id}. ` +
      "Confirma solo si el efectivo ya se recibió completo.",
    confirmLabel: "Conciliar",
  };
}

export function codReconciliationConfirmation(transaction: CodTransaction): ConfirmationText {
  return {
    title: "¿Conciliar este cobro?",
    description:
      `Se conciliará el cobro de ${formatMxnCentsWithCurrency(transaction.amount_cents)} ` +
      `de la orden ${shortId(transaction.order_id)}. Confirma solo si el efectivo ya se recibió completo.`,
    confirmLabel: "Conciliar",
  };
}
