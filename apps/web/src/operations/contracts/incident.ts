import {
  array,
  boolean,
  boundedString,
  exactObject,
  fail,
  isCanonicalUuid,
  nullable,
  oneOf,
  timestamp,
  uuid,
} from "./strict-json";
import { mazatlanWallTimeToInstant } from "../../lib/mazatlan-time";

/**
 * /ops/incidents (AI-07 incident_desk) over AI-05 openIncident, resolveIncident and
 * the API-INC-LIST-PROOFS-2026-09-29 reads listIncidents, getIncident and
 * listOrderProofs. Every incident and proof shown is exactly what the API returned
 * to this tenant session.
 */

export const incidentStatuses = ["OPEN", "INVESTIGATING", "RESOLVED", "REJECTED"] as const;
export type IncidentStatus = (typeof incidentStatuses)[number];
export const incidentSeverities = ["LOW", "MEDIUM", "HIGH", "CRITICAL"] as const;
export type IncidentSeverity = (typeof incidentSeverities)[number];
export const incidentReasonCodes = [
  "RECIPIENT_ABSENT",
  "ADDRESS_NOT_FOUND",
  "RECIPIENT_REFUSED",
  "ACCESS_RESTRICTED",
  "PAYMENT_UNAVAILABLE",
  "PACKAGE_DAMAGED",
  "SECURITY_RISK",
] as const;
export type IncidentReasonCode = (typeof incidentReasonCodes)[number];
export const incidentNextActions = ["RESCHEDULED", "RETURNING"] as const;
export type IncidentNextAction = (typeof incidentNextActions)[number];
export const incidentOutcomes = ["RESOLVED", "REJECTED"] as const;
export type IncidentOutcome = (typeof incidentOutcomes)[number];

export interface Incident {
  readonly id: string;
  readonly order_id: string;
  readonly status: IncidentStatus;
  readonly severity: IncidentSeverity;
  readonly reason_code: IncidentReasonCode;
  readonly next_action: IncidentNextAction;
  readonly custody_acquired: boolean;
  readonly occurred_at: string;
  readonly sla_due_at: string;
  readonly evidence_proof_ids: readonly string[];
}

export const incidentStatusLabels: Readonly<Record<IncidentStatus, string>> = {
  OPEN: "Abierta",
  INVESTIGATING: "En investigación",
  RESOLVED: "Resuelta",
  REJECTED: "Rechazada",
};
export const incidentSeverityLabels: Readonly<Record<IncidentSeverity, string>> = {
  LOW: "Baja",
  MEDIUM: "Media",
  HIGH: "Alta",
  CRITICAL: "Crítica",
};
export const incidentReasonLabels: Readonly<Record<IncidentReasonCode, string>> = {
  RECIPIENT_ABSENT: "Destinatario ausente",
  ADDRESS_NOT_FOUND: "Domicilio no encontrado",
  RECIPIENT_REFUSED: "Destinatario rechazó el paquete",
  ACCESS_RESTRICTED: "Acceso restringido",
  PAYMENT_UNAVAILABLE: "Pago no disponible",
  PACKAGE_DAMAGED: "Paquete dañado",
  SECURITY_RISK: "Riesgo de seguridad",
};
export const incidentNextActionLabels: Readonly<Record<IncidentNextAction, string>> = {
  RESCHEDULED: "Reprogramar",
  RETURNING: "Devolver al remitente",
};
export const incidentOutcomeLabels: Readonly<Record<IncidentOutcome, string>> = {
  RESOLVED: "Resolver",
  REJECTED: "Rechazar",
};

/** IncidentConflictProblem codes. */
export const incidentConflictMessages: Readonly<Record<string, string>> = {
  INVALID_REQUEST:
    "El servidor rechazó la solicitud: revisa los datos o la hora del intento (no puede estar en el futuro).",
  IDEMPOTENCY_CONFLICT: "Esta solicitud ya se usó con otros datos; captura de nuevo.",
  ORDER_STATE_NOT_ALLOWED: "El estado actual de la orden no admite una incidencia de intento fallido.",
  EVIDENCE_NOT_AVAILABLE: "Alguna evidencia no existe, no está lista o no pertenece a la orden.",
  INCIDENT_STATE_CONFLICT: "La incidencia ya está cerrada o su estado no permite esta acción.",
  OFFLINE_OPERATION_EXPIRED: "El intento es más antiguo que la ventana permitida; no puede registrarse.",
  CONFLICT: "La solicitud chocó con otra operación; vuelve a intentar.",
};

// ---------------------------------------------------------------------------
// Request validation

export const incidentTypePattern = /^[A-Z_]{1,64}$/;
// AI-05 ResolveIncidentRequest.reason: sent exactly as typed, never trimmed.
const reasonPattern =
  /^[^\s\x00-\x1F\x7F-\x9F](?:[^\x00-\x1F\x7F-\x9F]*[^\s\x00-\x1F\x7F-\x9F])?$/;
/** The default and ceiling of the openIncident occurrence age (x-offline-operation-age). */
export const maximumOccurrenceAgeMilliseconds = 72 * 60 * 60 * 1000;
/** The default clock tolerance for a future occurred_at. */
export const occurrenceClockToleranceMilliseconds = 5 * 60 * 1000;

export interface OpenIncidentDraft {
  readonly orderId: string;
  readonly type: string;
  readonly severity: string;
  readonly reasonCode: string;
  readonly nextAction: string;
  readonly description: string;
  /** `YYYY-MM-DDTHH:mm` as typed, in America/Mazatlan wall time. */
  readonly occurredAtLocal: string;
  /** Proof ids separated by commas, spaces or new lines. */
  readonly evidence: string;
}

export interface OpenIncidentBody {
  readonly type: string;
  readonly severity: IncidentSeverity;
  readonly description: string;
  readonly reason_code: IncidentReasonCode;
  readonly next_action: IncidentNextAction;
  readonly occurred_at: string;
  readonly evidence_proof_ids: readonly string[];
}

export type BuildResult<T> =
  | { readonly ok: true; readonly body: T }
  | { readonly ok: false; readonly errors: readonly string[] };

const localPattern = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/;

/** Converts Mazatlán wall time (`YYYY-MM-DDTHH:mm`) to a UTC instant; `null` for a malformed or impossible value. */
export function mazatlanLocalToUtc(value: string): string | null {
  if (!localPattern.test(value)) return null;
  return mazatlanWallTimeToInstant(value)?.toISOString() ?? null;
}

export function parseProofIds(text: string): string[] {
  return text.split(/[\s,]+/).filter((value) => value.length > 0);
}

export function buildOpenIncidentBody(draft: OpenIncidentDraft, now: Date): BuildResult<OpenIncidentBody> {
  const errors: string[] = [];
  if (!isCanonicalUuid(draft.orderId)) errors.push("El ID de la orden no es válido.");
  if (!incidentTypePattern.test(draft.type))
    errors.push("El tipo usa solo mayúsculas y guion bajo (1 a 64 caracteres), por ejemplo FAILED_ATTEMPT.");
  if (!(incidentSeverities as readonly string[]).includes(draft.severity)) errors.push("Elige una severidad.");
  if (!(incidentReasonCodes as readonly string[]).includes(draft.reasonCode)) errors.push("Elige un motivo.");
  if (!(incidentNextActions as readonly string[]).includes(draft.nextAction))
    errors.push("Elige la siguiente acción.");
  if (draft.description.length < 1 || draft.description.length > 2000)
    errors.push("La descripción es obligatoria (máximo 2000 caracteres).");
  const occurredAt = mazatlanLocalToUtc(draft.occurredAtLocal);
  if (occurredAt === null) errors.push("Captura la fecha y hora del intento (hora de Mazatlán).");
  else {
    const age = now.getTime() - Date.parse(occurredAt);
    if (age < -occurrenceClockToleranceMilliseconds) errors.push("La hora del intento no puede estar en el futuro.");
    else if (age > maximumOccurrenceAgeMilliseconds)
      errors.push("El intento tiene más de 72 horas; el servidor no lo admite.");
  }
  const proofs = parseProofIds(draft.evidence);
  if (proofs.length < 1 || proofs.length > 10) errors.push("Indica de 1 a 10 evidencias.");
  else if (!proofs.every(isCanonicalUuid)) errors.push("Cada ID de evidencia debe ser válido.");
  else if (new Set(proofs).size !== proofs.length) errors.push("Las evidencias no pueden repetirse.");
  if (errors.length > 0) return { ok: false, errors };
  return {
    ok: true,
    body: {
      type: draft.type,
      severity: draft.severity as IncidentSeverity,
      description: draft.description,
      reason_code: draft.reasonCode as IncidentReasonCode,
      next_action: draft.nextAction as IncidentNextAction,
      occurred_at: occurredAt!,
      evidence_proof_ids: proofs,
    },
  };
}

export interface ResolveIncidentBody {
  readonly outcome: IncidentOutcome;
  readonly reason: string;
}

export function isValidResolutionReason(value: string): boolean {
  return value.length >= 1 && value.length <= 500 && reasonPattern.test(value);
}

export function buildResolveIncidentBody(
  incidentId: string,
  outcome: string,
  reason: string,
): BuildResult<ResolveIncidentBody> {
  const errors: string[] = [];
  if (!isCanonicalUuid(incidentId)) errors.push("El ID de la incidencia no es válido.");
  if (!(incidentOutcomes as readonly string[]).includes(outcome)) errors.push("Elige resolver o rechazar.");
  if (!isValidResolutionReason(reason))
    errors.push("El motivo es obligatorio (máximo 500 caracteres) y no puede iniciar ni terminar con espacios.");
  if (errors.length > 0) return { ok: false, errors };
  return { ok: true, body: { outcome: outcome as IncidentOutcome, reason } };
}

// ---------------------------------------------------------------------------
// Response parser (fail closed)

export function parseIncident(value: unknown): Incident {
  const object = exactObject(value, [
    "id",
    "order_id",
    "status",
    "severity",
    "reason_code",
    "next_action",
    "custody_acquired",
    "occurred_at",
    "sla_due_at",
    "evidence_proof_ids",
  ]);
  const proofs = array(object.evidence_proof_ids, 10).map(uuid);
  if (proofs.length < 1 || new Set(proofs).size !== proofs.length) fail();
  return {
    id: uuid(object.id),
    order_id: uuid(object.order_id),
    status: oneOf(object.status, incidentStatuses),
    severity: oneOf(object.severity, incidentSeverities),
    reason_code: oneOf(object.reason_code, incidentReasonCodes),
    next_action: oneOf(object.next_action, incidentNextActions),
    custody_acquired: boolean(object.custody_acquired),
    occurred_at: timestamp(object.occurred_at),
    sla_due_at: timestamp(object.sla_due_at),
    evidence_proof_ids: proofs,
  };
}

// ---------------------------------------------------------------------------
// Reads (API-INC-LIST-PROOFS-2026-09-29)

/** The server owns the page size; a page larger than this is not trusted. */
const maximumPageItems = 200;
/** AI-05 bounds every list cursor to 128 characters. */
export const maximumCursorLength = 128;

export interface IncidentPage {
  readonly items: readonly Incident[];
  readonly next_cursor: string | null;
}

function cursor(value: unknown): string {
  const text = boundedString(value, 1, maximumCursorLength);
  if (!/^[A-Za-z0-9_-]+$/.test(text)) fail();
  return text;
}

export function parseIncidentPage(value: unknown): IncidentPage {
  const object = exactObject(value, ["items", "next_cursor"]);
  const items = array(object.items, maximumPageItems).map(parseIncident);
  if (new Set(items.map((item) => item.id)).size !== items.length) fail();
  return { items, next_cursor: nullable(object.next_cursor, cursor) };
}

/** The AI-06 proof types; the listing never carries anything but these. */
export const proofTypes = ["PICKUP_PHOTO", "DELIVERY_PHOTO", "SIGNATURE", "DELIVERY_CODE", "RETURN_PHOTO"] as const;
export type ProofType = (typeof proofTypes)[number];
export const proofTypeLabels: Readonly<Record<ProofType, string>> = {
  PICKUP_PHOTO: "Foto de recolección",
  DELIVERY_PHOTO: "Foto de entrega",
  SIGNATURE: "Firma",
  DELIVERY_CODE: "Código de entrega",
  RETURN_PHOTO: "Foto de devolución",
};

/** AI-05 Proof: metadata only, never bytes, storage keys or URLs. */
export interface Proof {
  readonly id: string;
  readonly proof_type: ProofType;
  readonly sha256: string;
  readonly captured_at: string;
}

export interface ProofPage {
  readonly items: readonly Proof[];
  readonly next_cursor: string | null;
}

export function parseProof(value: unknown): Proof {
  const object = exactObject(value, ["id", "proof_type", "sha256", "captured_at"]);
  const sha256 = boundedString(object.sha256, 64, 64);
  if (!/^[0-9a-f]{64}$/.test(sha256)) fail();
  return {
    id: uuid(object.id),
    proof_type: oneOf(object.proof_type, proofTypes),
    sha256,
    captured_at: timestamp(object.captured_at),
  };
}

export function parseProofPage(value: unknown): ProofPage {
  const object = exactObject(value, ["items", "next_cursor"]);
  const items = array(object.items, maximumPageItems).map(parseProof);
  if (new Set(items.map((item) => item.id)).size !== items.length) fail();
  return { items, next_cursor: nullable(object.next_cursor, cursor) };
}

export interface IncidentListFilter {
  readonly status?: IncidentStatus;
  readonly orderId?: string;
}

/** Only the published filters, each at most once and already canonical. */
export function incidentListSearch(filter: IncidentListFilter, cursorValue?: string | null): URLSearchParams {
  const search = new URLSearchParams();
  if (filter.status !== undefined) {
    if (!(incidentStatuses as readonly string[]).includes(filter.status)) fail();
    search.set("status", filter.status);
  }
  if (filter.orderId !== undefined) {
    if (!isCanonicalUuid(filter.orderId)) fail();
    search.set("order_id", filter.orderId);
  }
  if (cursorValue !== undefined && cursorValue !== null) search.set("cursor", cursor(cursorValue));
  return search;
}

export function isPending(incident: Incident): boolean {
  return incident.status === "OPEN" || incident.status === "INVESTIGATING";
}
