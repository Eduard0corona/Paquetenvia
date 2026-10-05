import { sumCents } from "../../operations/contracts/money";

/**
 * SET-001 settlements (AI-05 Settlement, SettlementLine, SettlementPage). Amounts are
 * int64 cents exactly as stored; a header whose total is not the exact sum of its
 * lines fails closed, mirroring the API's own 503.
 */

export const settlementStatuses = ["DRAFT", "CALCULATED", "APPROVED", "PAID", "VOID"] as const;
export type SettlementStatus = (typeof settlementStatuses)[number];
export const settlementLineTypes = ["DELIVERY", "RETURN", "ADJUSTMENT"] as const;
export type SettlementLineType = (typeof settlementLineTypes)[number];

export interface SettlementLine {
  readonly id: string;
  readonly line_type: SettlementLineType;
  readonly order_id: string | null;
  readonly amount_cents: number;
  readonly source_reference: string;
  readonly created_at: string;
}

export interface Settlement {
  readonly id: string;
  readonly payee_type: "DRIVER";
  readonly payee_id: string;
  readonly status: SettlementStatus;
  readonly total_cents: number;
  readonly period_from: string;
  readonly period_to: string;
  readonly created_at: string;
  readonly lines: readonly SettlementLine[];
}

export interface SettlementPage {
  readonly items: readonly Settlement[];
  readonly next_cursor: string | null;
}

export const settlementStatusLabels: Readonly<Record<SettlementStatus, string>> = {
  DRAFT: "Borrador",
  CALCULATED: "Calculada",
  APPROVED: "Aprobada",
  PAID: "Pagada",
  VOID: "Anulada",
};

export const settlementLineTypeLabels: Readonly<Record<SettlementLineType, string>> = {
  DELIVERY: "Entrega",
  RETURN: "Devolución",
  ADJUSTMENT: "Ajuste",
};

/** SettlementConflictProblem codes plus the DECIDED D7 deltas (AI-05 x-pilot-contract-deltas). */
export const settlementConflictMessages: Readonly<Record<string, string>> = {
  INVALID_REQUEST: "El servidor rechazó la solicitud por formato inválido.",
  CONFLICT:
    "La solicitud chocó con otra operación o con una clave de idempotencia usada con otros datos; actualiza y vuelve a intentar.",
  SETTLEMENT_STATE_CONFLICT:
    "El estado actual de la liquidación no permite esta acción; se recargó la versión más reciente.",
  CASH_PENDING: "No se puede aprobar: hay efectivo contra entrega sin conciliar por el monto esperado.",
  INCIDENT_PENDING: "No se puede aprobar: hay una incidencia abierta o en investigación en una orden incluida.",
  CLAIM_PENDING: "No se puede aprobar: una orden incluida tiene una reclamación abierta.",
  SETTLEMENT_PERIOD_NOT_CLOSED: "Solo se liquidan periodos ya cerrados.",
  SETTLEMENT_TOTAL_NEGATIVE: "El total de la liquidación no puede quedar negativo.",
};

export type SettlementAction = "adjust" | "approve" | "pay" | "void" | "export";

/**
 * Actions the AI-05 state machine admits for a status: adjustments and approval only
 * on CALCULATED, payment only on APPROVED, void on DRAFT, CALCULATED or APPROVED.
 * Capability gating is applied on top of this; the API remains authoritative.
 */
export function actionsForStatus(status: SettlementStatus): readonly SettlementAction[] {
  switch (status) {
    case "DRAFT":
      return ["void", "export"];
    case "CALCULATED":
      return ["adjust", "approve", "void", "export"];
    case "APPROVED":
      return ["pay", "void", "export"];
    case "PAID":
    case "VOID":
      return ["export"];
  }
}

// ---------------------------------------------------------------------------
// Request validation

const uuidPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const datePattern = /^(\d{4})-(\d{2})-(\d{2})$/;
// AI-05 AddSettlementAdjustmentRequest.reason / VoidSettlementRequest.reason.
const reasonPattern =
  /^[^\s\x00-\x1F\x7F-\x9F](?:[^\x00-\x1F\x7F-\x9F]*[^\s\x00-\x1F\x7F-\x9F])?$/;

export function isCanonicalUuid(value: string): boolean {
  return uuidPattern.test(value) && value !== "00000000-0000-0000-0000-000000000000";
}

export function isCalendarDate(value: string): boolean {
  const match = datePattern.exec(value);
  if (match === null) return false;
  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])];
  const date = new Date(Date.UTC(year, month - 1, day));
  return (
    date.getUTCFullYear() === year &&
    date.getUTCMonth() === month - 1 &&
    date.getUTCDate() === day
  );
}

/** Reasons are sent exactly as typed: surrounding whitespace is rejected, never trimmed. */
export function isValidReason(value: string): boolean {
  return value.length >= 1 && value.length <= 500 && reasonPattern.test(value);
}

export interface SettlementFilters {
  readonly payeeId?: string;
  readonly status?: string;
  readonly periodFrom?: string;
  readonly periodTo?: string;
  readonly cursor?: string;
}

/** listSettlements query; each parameter at most once, invalid filters are refused client-side. */
export function buildSettlementSearch(filters: SettlementFilters): URLSearchParams | null {
  const search = new URLSearchParams();
  if (filters.payeeId) {
    if (!isCanonicalUuid(filters.payeeId)) return null;
    search.set("payee_id", filters.payeeId);
  }
  if (filters.status) {
    if (!(settlementStatuses as readonly string[]).includes(filters.status)) return null;
    search.set("status", filters.status);
  }
  if (filters.periodFrom) {
    if (!isCalendarDate(filters.periodFrom)) return null;
    search.set("period_from", filters.periodFrom);
  }
  if (filters.periodTo) {
    if (!isCalendarDate(filters.periodTo)) return null;
    search.set("period_to", filters.periodTo);
  }
  if (filters.periodFrom && filters.periodTo && filters.periodTo < filters.periodFrom)
    return null;
  if (filters.cursor) {
    if (filters.cursor.length > 128) return null;
    search.set("cursor", filters.cursor);
  }
  return search;
}

export interface CreateSettlementBody {
  readonly driver_id: string;
  readonly period_from: string;
  readonly period_to: string;
}

/** CreateSettlementRequest: period_to not before period_from, at most 366 days. */
export function validateCreateSettlement(body: CreateSettlementBody): readonly string[] {
  const errors: string[] = [];
  if (!isCanonicalUuid(body.driver_id)) errors.push("El ID del repartidor no es válido.");
  const validDates = isCalendarDate(body.period_from) && isCalendarDate(body.period_to);
  if (!validDates) errors.push("Captura fechas de periodo válidas (AAAA-MM-DD).");
  if (validDates) {
    const days =
      (Date.UTC(...ymd(body.period_to)) - Date.UTC(...ymd(body.period_from))) / 86_400_000;
    if (days < 0) errors.push("El fin del periodo no puede ser anterior al inicio.");
    else if (days + 1 > 366) errors.push("El periodo cubre como máximo 366 días.");
  }
  return errors;
}

function ymd(value: string): [number, number, number] {
  const [year, month, day] = value.split("-").map(Number);
  return [year, month - 1, day];
}

// ---------------------------------------------------------------------------
// Response parsers

export class SettlementContractError extends Error {
  public constructor() {
    super("La respuesta de liquidaciones no cumple el contrato.");
    this.name = "SettlementContractError";
  }
}

export function parseSettlement(value: unknown): Settlement {
  const object = exactObject(value, [
    "id",
    "payee_type",
    "payee_id",
    "status",
    "total_cents",
    "period_from",
    "period_to",
    "created_at",
    "lines",
  ]);
  if (object.payee_type !== "DRIVER") fail();
  const lines = array(object.lines).map(parseLine);
  if (lines.length > 10_000) fail();
  const total = integer(object.total_cents);
  // Always the exact sum of amount_cents over lines (AI-05 Settlement.total_cents).
  if (sumCents(lines.map((line) => line.amount_cents)) !== total) fail();
  const periodFrom = date(object.period_from);
  const periodTo = date(object.period_to);
  if (periodTo < periodFrom) fail();
  return {
    id: uuid(object.id),
    payee_type: "DRIVER",
    payee_id: uuid(object.payee_id),
    status: oneOf(object.status, settlementStatuses),
    total_cents: total,
    period_from: periodFrom,
    period_to: periodTo,
    created_at: timestamp(object.created_at),
    lines,
  };
}

export function parseSettlementPage(value: unknown): SettlementPage {
  const object = exactObject(value, ["items", "next_cursor"]);
  const items = array(object.items);
  if (items.length > 500) fail();
  let nextCursor: string | null = null;
  if (object.next_cursor !== null) {
    if (typeof object.next_cursor !== "string" || object.next_cursor.length < 1 || object.next_cursor.length > 128)
      fail();
    nextCursor = object.next_cursor;
  }
  return { items: items.map(parseSettlement), next_cursor: nextCursor };
}

function parseLine(value: unknown): SettlementLine {
  const object = exactObject(value, [
    "id",
    "line_type",
    "order_id",
    "amount_cents",
    "source_reference",
    "created_at",
  ]);
  const lineType = oneOf(object.line_type, settlementLineTypes);
  const orderId = object.order_id === null ? null : uuid(object.order_id);
  // An ADJUSTMENT has no order; DELIVERY and RETURN always name theirs.
  if ((lineType === "ADJUSTMENT") !== (orderId === null)) fail();
  const source = boundedString(object.source_reference, 1, 128);
  const expectedPrefix =
    lineType === "ADJUSTMENT" ? "platform.audit_logs/" : "dispatch.assignments/";
  if (!source.startsWith(expectedPrefix) || !isCanonicalUuid(source.slice(expectedPrefix.length)))
    fail();
  return {
    id: uuid(object.id),
    line_type: lineType,
    order_id: orderId,
    amount_cents: integer(object.amount_cents),
    source_reference: source,
    created_at: timestamp(object.created_at),
  };
}

function exactObject(value: unknown, keys: readonly string[]): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) fail();
  const object = value as Record<string, unknown>;
  const actual = Object.keys(object).sort();
  const expected = [...keys].sort();
  if (actual.length !== expected.length || actual.some((key, index) => key !== expected[index]))
    fail();
  return object;
}

function array(value: unknown): readonly unknown[] {
  if (!Array.isArray(value)) fail();
  return value;
}

function boundedString(value: unknown, minimum: number, maximum: number): string {
  if (typeof value !== "string" || value.length < minimum || value.length > maximum) fail();
  return value;
}

function uuid(value: unknown): string {
  const text = boundedString(value, 36, 36);
  if (!isCanonicalUuid(text)) fail();
  return text;
}

function integer(value: unknown): number {
  if (!Number.isSafeInteger(value)) fail();
  return value as number;
}

function date(value: unknown): string {
  const text = boundedString(value, 10, 10);
  if (!isCalendarDate(text)) fail();
  return text;
}

function timestamp(value: unknown): string {
  const text = boundedString(value, 20, 40);
  if (!/(?:Z|[+-]\d{2}:\d{2})$/.test(text) || Number.isNaN(Date.parse(text))) fail();
  return text;
}

function oneOf<const T extends readonly string[]>(value: unknown, values: T): T[number] {
  if (typeof value !== "string" || !values.includes(value)) fail();
  return value as T[number];
}

function fail(): never {
  throw new SettlementContractError();
}
