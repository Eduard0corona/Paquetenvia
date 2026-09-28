import {
  array,
  boolean,
  exactObject,
  fail,
  isCanonicalUuid,
  oneOf,
  timestamp,
  uuid,
} from "./strict-json";

/**
 * /ops/incidents (AI-07 incident_desk) over AI-05 openIncident and resolveIncident.
 * AI-05 publishes no incident read, so the screen shows only the Incident the API
 * returned to this tenant session.
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

const localPattern = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/;
const mazatlanParts = new Intl.DateTimeFormat("en-US", {
  timeZone: "America/Mazatlan",
  hourCycle: "h23",
  year: "numeric",
  month: "2-digit",
  day: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
});

function mazatlanWallMillis(instant: number): number {
  const parts = Object.fromEntries(
    mazatlanParts.formatToParts(new Date(instant)).map((part) => [part.type, part.value]),
  );
  return Date.UTC(
    Number(parts.year),
    Number(parts.month) - 1,
    Number(parts.day),
    Number(parts.hour),
    Number(parts.minute),
  );
}

/** Converts Mazatlán wall time to a UTC instant; `null` for a malformed or impossible value. */
export function mazatlanLocalToUtc(value: string): string | null {
  const match = localPattern.exec(value);
  if (match === null) return null;
  const [year, month, day, hour, minute] = match.slice(1).map(Number);
  const wall = Date.UTC(year, month - 1, day, hour, minute);
  const check = new Date(wall);
  if (
    check.getUTCFullYear() !== year ||
    check.getUTCMonth() !== month - 1 ||
    check.getUTCDate() !== day ||
    hour > 23 ||
    minute > 59
  )
    return null;
  const offset = mazatlanWallMillis(wall) - wall;
  const instant = wall - offset;
  if (mazatlanWallMillis(instant) !== wall) return null;
  return new Date(instant).toISOString();
}

export function parseProofIds(text: string): string[] {
  return text.split(/[\s,]+/).filter((value) => value.length > 0);
}

export function buildOpenIncidentBody(draft: OpenIncidentDraft, now: Date): BuildResult<OpenIncidentBody> {
  const errors: string[] = [];
  if (!isCanonicalUuid(draft.orderId)) errors.push("La orden debe ser un UUID.");
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
  if (proofs.length < 1 || proofs.length > 10) errors.push("Indica de 1 a 10 evidencias (UUID de prueba).");
  else if (!proofs.every(isCanonicalUuid)) errors.push("Cada evidencia debe ser un UUID.");
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
  if (!isCanonicalUuid(incidentId)) errors.push("La incidencia debe ser un UUID.");
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
