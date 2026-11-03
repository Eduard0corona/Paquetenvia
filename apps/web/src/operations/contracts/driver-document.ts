import { pilotTimeZone } from "../../lib/mazatlan-time";
import {
  array,
  boolean,
  boundedString,
  exactObject,
  fail,
  nullable,
  oneOf,
  timestamp,
  uuid,
} from "./strict-json";

/**
 * DRV-DOC-001-IN-PERSON-2026-11-02 (docs/adr/ADR-DRV-DOC-001-IN-PERSON-DRIVER-DOCUMENTS.md; AI-07 driver_roster
 * documents): AI-05 listDriverDocuments, recordDriverDocument and revokeDriverDocument for the dispatcher's
 * "Repartidores" screen. Dispatch reviews the original in person (Y1) and records only the type, the decision, the
 * reason and the printed or review date (Y3): no photo, file, document number, key, hash or reviewer exists here.
 * Every reader fails closed on the first value outside the contract; the document id is a key, never shown.
 */
export const driverDocumentTypes = ["IDENTITY", "DRIVER_LICENSE", "VEHICLE_CARD", "INSURANCE"] as const;
export type DriverDocumentType = (typeof driverDocumentTypes)[number];

export const driverDocumentStatuses = ["PENDING", "VALID", "REJECTED", "EXPIRED", "REVOKED"] as const;
export type DriverDocumentStatus = (typeof driverDocumentStatuses)[number];

/** Y7: the fixed reasons, never free text. */
export const driverDocumentRejectionCodes = [
  "ILLEGIBLE",
  "EXPIRED",
  "WRONG_DOCUMENT",
  "HOLDER_MISMATCH",
  "INCOMPLETE",
  "NOT_ACCEPTED",
] as const;
export type DriverDocumentRejectionCode = (typeof driverDocumentRejectionCodes)[number];

export const driverDocumentExpiryBases = ["PRINTED_EXPIRY", "PERMANENT_LICENSE_REVIEW"] as const;
export type DriverDocumentExpiryBasis = (typeof driverDocumentExpiryBases)[number];

export interface DriverDocument {
  /** A key for revokeDriverDocument; never rendered. */
  readonly id: string;
  readonly document_type: DriverDocumentType;
  readonly status: DriverDocumentStatus;
  readonly rejection_code: DriverDocumentRejectionCode | null;
  /** 00:00 America/Mazatlan of the day after the last valid day (Y12). */
  readonly expires_at: string | null;
  /** The last valid local day, yyyy-MM-dd. */
  readonly valid_through: string | null;
  readonly expiry_basis: DriverDocumentExpiryBasis | null;
  readonly recorded_at: string;
  readonly revoked_at: string | null;
}

/** One document type the driver's vehicle requires (Y11) with its latest document, or null when none was recorded. */
export interface DriverDocumentSlot {
  readonly document_type: DriverDocumentType;
  readonly document: DriverDocument | null;
}

export interface DriverDocumentSet {
  readonly driver_id: string;
  readonly documents: readonly DriverDocumentSlot[];
}

export interface DriverDocumentPage {
  /** False while Drivers:Documents:Enabled is off (the pilot until GATE-007, Y2): no column, button or dialog. */
  readonly documents_enabled: boolean;
  readonly items: readonly DriverDocumentSet[];
  readonly next_cursor: string | null;
}

/** The server page size is 100, the roster's; anything larger is not this contract. */
const maximumPageItems = 100;
const maximumCursorLength = 128;
const datePattern = /^(\d{4})-(\d{2})-(\d{2})$/;

function cursor(value: unknown): string {
  const text = boundedString(value, 1, maximumCursorLength);
  if (!/^[A-Za-z0-9_-]+$/.test(text)) fail();
  return text;
}

/** A calendar date (yyyy-MM-dd) that exists. */
export function isCalendarDate(text: string): boolean {
  const match = datePattern.exec(text);
  if (match === null) return false;
  const [year, month, day] = match.slice(1, 4).map(Number) as [number, number, number];
  const check = new Date(Date.UTC(year, month - 1, day));
  return (
    year >= 1 &&
    check.getUTCFullYear() === year &&
    check.getUTCMonth() === month - 1 &&
    check.getUTCDate() === day
  );
}

function calendarDate(value: unknown): string {
  const text = boundedString(value, 10, 10);
  if (!isCalendarDate(text)) fail();
  return text;
}

export function parseDriverDocument(value: unknown): DriverDocument {
  const object = exactObject(value, [
    "id",
    "document_type",
    "status",
    "rejection_code",
    "expires_at",
    "valid_through",
    "expiry_basis",
    "recorded_at",
    "revoked_at",
  ]);
  const document: DriverDocument = {
    id: uuid(object.id),
    document_type: oneOf(object.document_type, driverDocumentTypes),
    status: oneOf(object.status, driverDocumentStatuses),
    rejection_code: nullable(object.rejection_code, (code) => oneOf(code, driverDocumentRejectionCodes)),
    expires_at: nullable(object.expires_at, timestamp),
    valid_through: nullable(object.valid_through, calendarDate),
    expiry_basis: nullable(object.expiry_basis, (basis) => oneOf(basis, driverDocumentExpiryBases)),
    recorded_at: timestamp(object.recorded_at),
    revoked_at: nullable(object.revoked_at, timestamp),
  };
  // The published shapes: a reason only on a rejection, the expiry instant and its last day together, a basis only
  // with a date, a permanent-license review only on a license, and a revocation time only on a revoked document.
  // Older FILE rows may be REJECTED without a reason or REVOKED without a time, so those two are one-way.
  if (document.rejection_code !== null && document.status !== "REJECTED") fail();
  if ((document.expires_at === null) !== (document.valid_through === null)) fail();
  if (document.expiry_basis !== null && document.valid_through === null) fail();
  if (document.expiry_basis === "PERMANENT_LICENSE_REVIEW" && document.document_type !== "DRIVER_LICENSE") fail();
  if (document.revoked_at !== null && document.status !== "REVOKED") fail();
  return document;
}

export function parseDriverDocumentSlot(value: unknown): DriverDocumentSlot {
  const object = exactObject(value, ["document_type", "document"]);
  const slot: DriverDocumentSlot = {
    document_type: oneOf(object.document_type, driverDocumentTypes),
    document: nullable(object.document, parseDriverDocument),
  };
  if (slot.document !== null && slot.document.document_type !== slot.document_type) fail();
  return slot;
}

export function parseDriverDocumentSet(value: unknown): DriverDocumentSet {
  const object = exactObject(value, ["driver_id", "documents"]);
  const documents = array(object.documents, driverDocumentTypes.length).map(parseDriverDocumentSlot);
  // One slot per type, in the published order.
  const order = documents.map((slot) => driverDocumentTypes.indexOf(slot.document_type));
  if (order.some((index, position) => position > 0 && index <= order[position - 1]!)) fail();
  return { driver_id: uuid(object.driver_id), documents };
}

export function parseDriverDocumentPage(value: unknown): DriverDocumentPage {
  const object = exactObject(value, ["documents_enabled", "items", "next_cursor"]);
  const enabled = boolean(object.documents_enabled);
  const items = array(object.items, maximumPageItems).map(parseDriverDocumentSet);
  if (new Set(items.map((item) => item.driver_id)).size !== items.length) fail();
  const next = nullable(object.next_cursor, cursor);
  // Off means nothing was read: no item and no next page.
  if (!enabled && (items.length > 0 || next !== null)) fail();
  return { documents_enabled: enabled, items, next_cursor: next };
}

/** Only `cursor`, and only a value this operation issued. */
export function driverDocumentSearch(cursorValue?: string | null): URLSearchParams {
  const search = new URLSearchParams();
  if (cursorValue !== undefined && cursorValue !== null) search.set("cursor", cursor(cursorValue));
  return search;
}

/** What the dispatcher decided in the "Registrar documento" form. */
export type RecordDriverDocumentInput =
  | {
      readonly documentType: DriverDocumentType;
      readonly decision: "VALID";
      readonly permanentLicense: false;
      /** The date printed on the document, yyyy-MM-dd. */
      readonly printedExpiry: string;
    }
  | {
      readonly documentType: "DRIVER_LICENSE";
      readonly decision: "VALID";
      readonly permanentLicense: true;
    }
  | {
      readonly documentType: DriverDocumentType;
      readonly decision: "REJECTED";
      readonly rejectionCode: DriverDocumentRejectionCode;
    };

/** AI-05 RecordDriverDocumentRequest: exactly one of its three shapes. */
export function recordDriverDocumentBody(input: RecordDriverDocumentInput): Readonly<Record<string, string>> {
  const type = oneOf(input.documentType, driverDocumentTypes);
  if (input.decision === "REJECTED") {
    return { document_type: type, decision: "REJECTED", rejection_code: oneOf(input.rejectionCode, driverDocumentRejectionCodes) };
  }
  if (input.decision !== "VALID") fail();
  if (input.permanentLicense) {
    if (type !== "DRIVER_LICENSE") fail();
    return { document_type: type, decision: "VALID", expiry_basis: "PERMANENT_LICENSE_REVIEW" };
  }
  if (!isCalendarDate(input.printedExpiry)) fail();
  return { document_type: type, decision: "VALID", expiry_basis: "PRINTED_EXPIRY", expires_on: input.printedExpiry };
}

/** AI-07 driver_roster documents.type_labels. */
export const driverDocumentTypeLabels: Readonly<Record<DriverDocumentType, string>> = {
  IDENTITY: "Identificación oficial",
  DRIVER_LICENSE: "Licencia de conducir",
  VEHICLE_CARD: "Tarjeta de circulación",
  INSURANCE: "Seguro del vehículo",
};

/** Y7: the reasons as the dialog lists them. */
export const driverDocumentRejectionLabels: Readonly<Record<DriverDocumentRejectionCode, string>> = {
  ILLEGIBLE: "Ilegible",
  EXPIRED: "Vencido",
  WRONG_DOCUMENT: "No es el pedido",
  HOLDER_MISMATCH: "No es del repartidor",
  INCOMPLETE: "Incompleto",
  NOT_ACCEPTED: "No aceptado",
};

export const driverDocumentStatusLabels: Readonly<Record<DriverDocumentStatus, string>> = {
  PENDING: "Pendiente",
  VALID: "Aprobado",
  REJECTED: "Rechazado",
  EXPIRED: "Vencido",
  REVOKED: "Revocado",
};

/** The fixed line of the record dialog (Y3). */
export const inPersonReviewLine =
  "El repartidor te muestra el original. No tomes fotos ni copias; solo anota el tipo y la fecha.";

/** Y6: the warning starts this many days before the last valid day. */
export const expiryWarningDays = 30;
/** Y5: a permanent Sinaloa license is reviewed again this many months after the review day. */
export const permanentLicenseReviewMonths = 12;

const todayFormatter = new Intl.DateTimeFormat("en-CA", {
  timeZone: pilotTimeZone,
  year: "numeric",
  month: "2-digit",
  day: "2-digit",
});

/** Today in Culiacán (America/Mazatlan), yyyy-MM-dd, whatever the device zone. */
export function mazatlanToday(now: number): string {
  return todayFormatter.format(new Date(now));
}

function dayNumber(date: string): number {
  const match = datePattern.exec(date);
  if (match === null) fail();
  const [year, month, day] = match.slice(1, 4).map(Number) as [number, number, number];
  return Date.UTC(year, month - 1, day) / 86_400_000;
}

/** Whole days from `from` to `to` (both yyyy-MM-dd). */
export function daysBetween(from: string, to: string): number {
  return Math.round(dayNumber(to) - dayNumber(from));
}

/** Y5 preview: the review day plus 12 months; February 29 becomes February 28. */
export function permanentLicenseReviewDate(today: string): string {
  const match = datePattern.exec(today);
  if (match === null) fail();
  const [year, month, day] = match.slice(1, 4).map(Number) as [number, number, number];
  const targetYear = year + Math.floor((month - 1 + permanentLicenseReviewMonths) / 12);
  const targetMonth = (month - 1 + permanentLicenseReviewMonths) % 12;
  const lastDay = new Date(Date.UTC(targetYear, targetMonth + 1, 0)).getUTCDate();
  const result = new Date(Date.UTC(targetYear, targetMonth, Math.min(day, lastDay)));
  return result.toISOString().slice(0, 10);
}

const dateFormatter = new Intl.DateTimeFormat("es-MX", { timeZone: "UTC", dateStyle: "medium" });

/** A calendar date as es-MX text ("1 mar 2027"); the date is already local to Culiacán. */
export function formatCalendarDate(date: string): string {
  return dateFormatter.format(new Date(dayNumber(date) * 86_400_000));
}

export type DocumentTone = "muted" | "ok" | "warn" | "crit";

export interface DocumentIndicator {
  readonly text: string;
  readonly tone: DocumentTone;
}

/**
 * Y6 and Y5: the warning of one VALID document - «Vencido» (or «Revisión vencida») once expires_at has passed,
 * «Vence en N días» (or «Revisión en N días»; 0 reads «Vence hoy» / «Revisión hoy») from 30 days before its last
 * valid day, nothing otherwise. Computed here; nothing is stored as EXPIRED.
 */
export function documentWarning(document: DriverDocument, now: number): DocumentIndicator | null {
  if (document.status !== "VALID" || document.expires_at === null || document.valid_through === null) return null;
  const review = document.expiry_basis === "PERMANENT_LICENSE_REVIEW";
  if (now >= Date.parse(document.expires_at)) {
    return { text: review ? "Revisión vencida" : "Vencido", tone: "crit" };
  }
  const days = daysBetween(mazatlanToday(now), document.valid_through);
  if (days > expiryWarningDays) return null;
  const word = review ? "Revisión" : "Vence";
  if (days <= 0) return { text: `${word} hoy`, tone: "warn" };
  return { text: `${word} en ${days} ${days === 1 ? "día" : "días"}`, tone: "warn" };
}

/** The status of one slot in the documents dialog: "Falta" when none was recorded, the reason on a rejection. */
export function slotStatusText(slot: DriverDocumentSlot): string {
  const document = slot.document;
  if (document === null) return "Falta";
  if (document.rejection_code !== null) {
    return `${driverDocumentStatusLabels.REJECTED}: ${driverDocumentRejectionLabels[document.rejection_code]}`;
  }
  return driverDocumentStatusLabels[document.status];
}

/** "Vence el {fecha}" or "Revisión el {fecha}" for the document's last valid day, or null without one. */
export function slotDateText(slot: DriverDocumentSlot): string | null {
  const document = slot.document;
  if (document === null || document.valid_through === null) return null;
  const word = document.expiry_basis === "PERMANENT_LICENSE_REVIEW" ? "Revisión" : "Vence";
  return `${word} el ${formatCalendarDate(document.valid_through)}`;
}

function isCurrent(slot: DriverDocumentSlot, now: number): boolean {
  const document = slot.document;
  return (
    document !== null &&
    document.status === "VALID" &&
    document.expires_at !== null &&
    now < Date.parse(document.expires_at)
  );
}

/**
 * The "Documentos" column of a driver: «Vencido» first, then "Incompletos" (a required document missing, REJECTED,
 * REVOKED, PENDING or EXPIRED), then the nearest warning, then "Completos". A vehicle that requires no document
 * reads "Sin documentos exigidos".
 */
export function documentsColumn(slots: readonly DriverDocumentSlot[], now: number): DocumentIndicator {
  if (slots.length === 0) return { text: "Sin documentos exigidos", tone: "muted" };
  const warnings = slots
    .map((slot) => (slot.document === null ? null : documentWarning(slot.document, now)))
    .filter((warning): warning is DocumentIndicator => warning !== null);
  const expired = warnings.filter((warning) => warning.tone === "crit");
  if (expired.length > 0) {
    return expired.some((warning) => warning.text === "Vencido") ? { text: "Vencido", tone: "crit" } : expired[0]!;
  }
  if (slots.some((slot) => !isCurrent(slot, now))) return { text: "Incompletos", tone: "crit" };
  // Every document is current: the nearest last valid day decides the warning, a printed date before a review.
  let soonest: DriverDocument | null = null;
  for (const slot of slots) {
    const document = slot.document!;
    if (documentWarning(document, now) === null) continue;
    if (
      soonest === null ||
      document.valid_through! < soonest.valid_through! ||
      (document.valid_through === soonest.valid_through && document.expiry_basis !== "PERMANENT_LICENSE_REVIEW")
    )
      soonest = document;
  }
  return soonest === null ? { text: "Completos", tone: "ok" } : documentWarning(soonest, now)!;
}

/** A slot whose document dispatch may revoke: the latest of its type and VALID (Y10). */
export function isRevocable(slot: DriverDocumentSlot): boolean {
  return slot.document !== null && slot.document.status === "VALID";
}

/** The slots with one document replaced by the authoritative answer of a record or a revoke. */
export function withDocument(
  slots: readonly DriverDocumentSlot[],
  document: DriverDocument,
): readonly DriverDocumentSlot[] {
  return slots.map((slot) => (slot.document_type === document.document_type ? { ...slot, document } : slot));
}

/** es-MX messages for the stable problem codes of the two writes; never server text. */
export const driverDocumentConflictMessages: Readonly<Record<string, string>> = {
  DOCUMENT_TYPE_NOT_REQUIRED: "Ese documento no se pide para el vehículo de este repartidor.",
  EXPIRY_DATE_OUT_OF_RANGE: "La fecha ya pasó o está a más de 10 años; si ya venció, recházalo como Vencido",
  DOCUMENT_NOT_REVOCABLE: "Ese documento ya no es el vigente. Actualiza la lista.",
  IDEMPOTENCY_CONFLICT: "Ese registro ya se envió con otros datos. Revisa la lista y vuelve a intentarlo.",
  INVALID_REQUEST: "Revisa los datos del documento.",
};
