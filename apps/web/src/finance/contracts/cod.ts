import { parseMxnToCents, sumCents } from "../../operations/contracts/money";
import {
  array,
  boolean,
  exactObject,
  fail,
  integer,
  isCanonicalUuid,
  nullable,
  oneOf,
  timestamp,
  uuid,
} from "../../operations/contracts/strict-json";

/**
 * /finance/cod (AI-07 cod_control) over AI-05 getOrderFinancials,
 * recordCodCollection and reconcileCod. Every amount is int64 MXN cents exactly as
 * the API returned it; totals that do not add up fail closed.
 */

export const orderStatuses = [
  "DRAFT",
  "CONFIRMED",
  "READY_FOR_PICKUP",
  "ASSIGNED",
  "AT_PICKUP",
  "PICKED_UP",
  "IN_TRANSIT",
  "DELIVERING",
  "FAILED_ATTEMPT",
  "RESCHEDULED",
  "RETURNING",
  "RETURNED",
  "DELIVERED",
  "CLOSED",
  "CLAIM_OPEN",
  "CLAIM_RESOLVED",
  "CANCELLED",
] as const;
export type OrderStatus = (typeof orderStatuses)[number];

export const codStatuses = ["EXPECTED", "RECORDED", "RECONCILED", "DISPUTED", "REVERSED"] as const;
export type CodStatus = (typeof codStatuses)[number];
export const modalities = ["OWN", "EXTERNAL", "ALLY_CAPACITY"] as const;
export type Modality = (typeof modalities)[number];

export interface ModalityCost {
  readonly modality: Modality;
  readonly cost_cents: number;
  readonly assignment_count: number;
}

export interface CodPosition {
  readonly expected_cents: number;
  readonly status: CodStatus | null;
  readonly amount_cents: number | null;
  readonly recorded: boolean;
  readonly reconciled: boolean;
  readonly satisfies_delivery_requirement: boolean;
  readonly satisfies_close_requirement: boolean;
}

export interface OrderFinancials {
  readonly order_id: string;
  readonly order_status: OrderStatus;
  readonly currency: "MXN";
  readonly revenue_cents: number;
  readonly cost_cents: number;
  readonly margin_cents: number;
  readonly margin_basis_points: number | null;
  readonly cost_by_modality: readonly ModalityCost[];
  readonly cod: CodPosition;
}

export interface CodTransaction {
  readonly id: string;
  readonly order_id: string;
  readonly amount_cents: number;
  readonly status: CodStatus;
  readonly recorded_at: string | null;
  readonly reconciled_at: string | null;
}

export const codStatusLabels: Readonly<Record<CodStatus, string>> = {
  EXPECTED: "Esperado",
  RECORDED: "Cobrado, sin conciliar",
  RECONCILED: "Conciliado",
  DISPUTED: "En disputa",
  REVERSED: "Revertido",
};

export const modalityLabels: Readonly<Record<Modality, string>> = {
  OWN: "Flota propia",
  EXTERNAL: "Repartidor externo",
  ALLY_CAPACITY: "Capacidad aliada",
};

/** FinanceConflictProblem codes. */
export const financeConflictMessages: Readonly<Record<string, string>> = {
  INVALID_REQUEST: "El servidor rechazó la solicitud por formato inválido.",
  CONFLICT:
    "La solicitud chocó con otra operación o con una clave de idempotencia usada con otros datos; se recargó la orden.",
  COD_NOT_EXPECTED: "La orden no tiene cobro contra entrega esperado.",
  COD_AMOUNT_MISMATCH: "El monto debe ser exactamente el esperado por la orden.",
  COD_ALREADY_RECORDED: "El cobro de esta orden ya estaba registrado.",
  COD_STATE_CONFLICT: "El estado del cobro no permite esta acción; se recargó la orden.",
  ORDER_STATE_CONFLICT: "El estado de la orden no permite esta acción.",
};

// ---------------------------------------------------------------------------
// Request validation

export interface RecordCodBody {
  readonly amount_cents: number;
  readonly reference: string;
}

export function isValidCodReference(value: string): boolean {
  return value.length >= 1 && value.length <= 200 && value.trim() === value;
}

/** A collection may be recorded only when a positive amount is expected and none is recorded. */
export function canRecordCollection(financials: OrderFinancials): boolean {
  return financials.cod.expected_cents > 0 && !financials.cod.recorded && !financials.cod.reconciled &&
    (financials.cod.status === null || financials.cod.status === "EXPECTED");
}

export function buildRecordCodBody(
  financials: OrderFinancials,
  amountText: string,
  reference: string,
):
  | { readonly ok: true; readonly body: RecordCodBody }
  | { readonly ok: false; readonly errors: readonly string[] } {
  const errors: string[] = [];
  const amount = parseMxnToCents(amountText, { allowZero: false });
  if (amount === null) errors.push("Captura el monto cobrado en MXN, positivo y con hasta 2 decimales.");
  else if (amount !== financials.cod.expected_cents)
    errors.push("El monto cobrado debe ser exactamente el esperado por la orden.");
  if (!isValidCodReference(reference))
    errors.push("La referencia es obligatoria (máximo 200 caracteres) y no puede iniciar ni terminar con espacios.");
  if (errors.length > 0) return { ok: false, errors };
  return { ok: true, body: { amount_cents: amount!, reference } };
}

export { isCanonicalUuid };

// ---------------------------------------------------------------------------
// Response parsers (fail closed)

function cents(value: unknown): number {
  return integer(value, 0);
}

function parseModalityCost(value: unknown): ModalityCost {
  const object = exactObject(value, ["modality", "cost_cents", "assignment_count"]);
  return {
    modality: oneOf(object.modality, modalities),
    cost_cents: cents(object.cost_cents),
    assignment_count: integer(object.assignment_count, 0),
  };
}

function parseCodPosition(value: unknown): CodPosition {
  const object = exactObject(
    value,
    ["expected_cents", "status", "recorded", "reconciled", "satisfies_delivery_requirement", "satisfies_close_requirement"],
    ["amount_cents"],
  );
  const status = nullable(object.status, (text) => oneOf(text, codStatuses));
  const hasAmount = Object.hasOwn(object, "amount_cents");
  // amount_cents is omitted exactly until a collection exists.
  if ((status === null) === hasAmount) fail();
  const position: CodPosition = {
    expected_cents: cents(object.expected_cents),
    status,
    amount_cents: hasAmount ? cents(object.amount_cents) : null,
    recorded: boolean(object.recorded),
    reconciled: boolean(object.reconciled),
    satisfies_delivery_requirement: boolean(object.satisfies_delivery_requirement),
    satisfies_close_requirement: boolean(object.satisfies_close_requirement),
  };
  if (position.reconciled && !position.recorded) fail();
  return position;
}

export function parseOrderFinancials(value: unknown): OrderFinancials {
  const object = exactObject(value, [
    "order_id",
    "order_status",
    "currency",
    "revenue_cents",
    "cost_cents",
    "margin_cents",
    "margin_basis_points",
    "cost_by_modality",
    "cod",
  ]);
  if (object.currency !== "MXN") fail();
  const buckets = array(object.cost_by_modality, 3).map(parseModalityCost);
  // One bucket per modality, in the order OWN, EXTERNAL, ALLY_CAPACITY.
  if (buckets.length !== 3 || buckets.some((bucket, index) => bucket.modality !== modalities[index])) fail();
  const revenue = cents(object.revenue_cents);
  const cost = cents(object.cost_cents);
  const margin = integer(object.margin_cents);
  if (sumCents(buckets.map((bucket) => bucket.cost_cents)) !== cost) fail();
  if (sumCents([revenue, -cost]) !== margin) fail();
  const basisPoints = nullable(object.margin_basis_points, (points) => integer(points));
  // margin_basis_points is null exactly when revenue is zero.
  if ((basisPoints === null) !== (revenue === 0)) fail();
  return {
    order_id: uuid(object.order_id),
    order_status: oneOf(object.order_status, orderStatuses),
    currency: "MXN",
    revenue_cents: revenue,
    cost_cents: cost,
    margin_cents: margin,
    margin_basis_points: basisPoints,
    cost_by_modality: buckets,
    cod: parseCodPosition(object.cod),
  };
}

export function parseCodTransaction(value: unknown): CodTransaction {
  const object = exactObject(value, ["id", "order_id", "amount_cents", "status", "recorded_at", "reconciled_at"]);
  const transaction: CodTransaction = {
    id: uuid(object.id),
    order_id: uuid(object.order_id),
    amount_cents: integer(object.amount_cents),
    status: oneOf(object.status, codStatuses),
    recorded_at: nullable(object.recorded_at, timestamp),
    reconciled_at: nullable(object.reconciled_at, timestamp),
  };
  if (transaction.status === "RECORDED" && transaction.recorded_at === null) fail();
  if (transaction.status === "RECONCILED" && transaction.reconciled_at === null) fail();
  return transaction;
}

/** Basis points as a percentage string with two decimals, by integer arithmetic. */
export function formatBasisPoints(points: number): string {
  if (!Number.isSafeInteger(points)) fail();
  const value = BigInt(points);
  const negative = value < 0n;
  const absolute = negative ? -value : value;
  return `${negative ? "-" : ""}${absolute / 100n}.${(absolute % 100n).toString().padStart(2, "0")} %`;
}
