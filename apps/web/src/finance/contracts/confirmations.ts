import { formatMxnCentsWithCurrency, parseMxnToCents } from "../../operations/contracts/money";
import { shortId } from "../../lib/short-id";
import type { CodTransaction, PendingCodOrder } from "./cod";
import type { CreateSettlementBody, Settlement } from "./settlement";

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

const operatingDayFormatter = new Intl.DateTimeFormat("es-MX", {
  dateStyle: "long",
  timeZone: "UTC",
});

/**
 * A settlement period day (`YYYY-MM-DD`, an operating day in Mazatlán time) in words, such as
 * "1 de septiembre de 2026". The calendar date is formatted as written: no device or zone
 * offset can move it to another day.
 */
function formatOperatingDay(value: string): string {
  const [year, month, day] = value.split("-").map(Number);
  return operatingDayFormatter.format(new Date(Date.UTC(year, month - 1, day)));
}

/** "Calcular liquidación", for a request validateCreateSettlement already accepted. */
export function settlementCreationConfirmation(body: CreateSettlementBody): ConfirmationText {
  const period =
    body.period_from === body.period_to
      ? `del ${formatOperatingDay(body.period_from)} (día completo, hora de Mazatlán)`
      : `del ${formatOperatingDay(body.period_from)} al ${formatOperatingDay(body.period_to)} ` +
        "(días completos, hora de Mazatlán)";
  return {
    title: "¿Calcular liquidación?",
    description: `Se calculará la liquidación del repartidor ${shortId(body.driver_id)} ${period}.`,
    confirmLabel: "Calcular",
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

/** A COD confirmation names the order by its tracking number when the screen has it. */
function codOrderName(orderId: string, publicId: string | null): string {
  return publicId ?? shortId(orderId);
}

/**
 * "Registrar cobro", for the exact integer cents that will be sent. The reference the person
 * typed is never repeated here (AI-07 cod_control).
 */
export function codRecordConfirmation(
  orderId: string,
  amountCents: number,
  publicId: string | null = null,
): ConfirmationText {
  return {
    title: "¿Registrar cobro?",
    description:
      `Se registrará el cobro contra entrega de ${formatMxnCentsWithCurrency(amountCents)} ` +
      `de la orden ${codOrderName(orderId, publicId)}. Después quedará pendiente de conciliar.`,
    confirmLabel: "Registrar cobro",
  };
}

/** "Conciliar" from the pending list, once getOrderFinancials returned the RECORDED collection. */
export function pendingCodReconciliationConfirmation(
  order: PendingCodOrder,
  record: CodTransaction,
): ConfirmationText {
  return {
    title: "¿Conciliar cobro?",
    description:
      `Se conciliará el cobro de ${formatMxnCentsWithCurrency(record.amount_cents)} ` +
      `de la orden ${order.public_id}. Confirma solo si el efectivo ya se recibió completo.`,
    confirmLabel: "Conciliar",
  };
}

export function codReconciliationConfirmation(
  transaction: CodTransaction,
  publicId: string | null = null,
): ConfirmationText {
  return {
    title: "¿Conciliar este cobro?",
    description:
      `Se conciliará el cobro de ${formatMxnCentsWithCurrency(transaction.amount_cents)} ` +
      `de la orden ${codOrderName(transaction.order_id, publicId)}. ` +
      "Confirma solo si el efectivo ya se recibió completo.",
    confirmLabel: "Conciliar",
  };
}
