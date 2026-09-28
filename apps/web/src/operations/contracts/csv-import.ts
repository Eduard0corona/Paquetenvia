import {
  array,
  boolean,
  boundedString,
  exactObject,
  fail,
  integer,
  nullable,
  oneOf,
  uuid,
} from "./strict-json";

/**
 * /ops/orders/import (AI-07 csv_order_import) over AI-05 previewOrderCsv and
 * commitOrderCsv. The client never parses rows: the server prevalidates the
 * uploaded bytes and reports them; these parsers accept only the AI-05 shapes.
 */

/** AI-05 CsvImportPreviewRequest.file: at most 1 MiB and 500 data rows. */
export const maximumCsvBytes = 1_048_576;
export const maximumCsvDataRows = 500;
/** The multipart filename; the local file name never leaves the browser. */
export const csvUploadFilename = "orders.csv";

export const csvFileErrors = [
  "ENCODING_INVALID",
  "MALFORMED_QUOTING",
  "HEADER_INVALID",
  "FILE_EMPTY",
  "ROW_LIMIT_EXCEEDED",
] as const;
export type CsvFileError = (typeof csvFileErrors)[number];

export const csvRowErrorCodes = [
  "COLUMN_COUNT_INVALID",
  "QUOTE_ID_INVALID",
  "QUOTE_ID_DUPLICATED",
  "PAYER_TYPE_INVALID",
  "TERMS_VERSION_INVALID",
  "PRIVACY_VERSION_INVALID",
  "ACCEPTED_AT_INVALID",
  "ACCEPTANCE_CHANNEL_INVALID",
] as const;
export type CsvRowErrorCode = (typeof csvRowErrorCodes)[number];

export const csvRowOutcomeErrors = ["QUOTE_UNAVAILABLE", "IDEMPOTENCY_CONFLICT", "INVALID_REQUEST"] as const;
export type CsvRowOutcomeError = (typeof csvRowOutcomeErrors)[number];

export interface CsvRowError {
  readonly column: string;
  readonly code: CsvRowErrorCode;
}

export interface CsvRowPreview {
  readonly row_number: number;
  readonly quote_id: string | null;
  readonly payer_type: string | null;
  readonly valid: boolean;
  readonly errors: readonly CsvRowError[];
}

export interface CsvImportPreview {
  readonly content_digest: string;
  readonly total_rows: number;
  readonly valid_rows: number;
  readonly invalid_rows: number;
  readonly file_errors: readonly CsvFileError[];
  readonly rows: readonly CsvRowPreview[];
}

export interface CsvRowOutcome {
  readonly row_number: number;
  readonly quote_id: string;
  readonly status: "CREATED" | "FAILED";
  readonly order_id: string | null;
  readonly public_id: string | null;
  readonly error_code: CsvRowOutcomeError | null;
}

export interface CsvImportCommit {
  readonly content_digest: string;
  readonly total_rows: number;
  readonly created_rows: number;
  readonly failed_rows: number;
  readonly rows: readonly CsvRowOutcome[];
}

// ---------------------------------------------------------------------------
// Spanish copy

export const csvFileErrorLabels: Readonly<Record<CsvFileError, string>> = {
  ENCODING_INVALID: "El archivo no está en UTF-8.",
  MALFORMED_QUOTING: "El archivo tiene comillas mal cerradas.",
  HEADER_INVALID: "El encabezado no es el del formato CSV-001.",
  FILE_EMPTY: "El archivo no tiene filas de datos.",
  ROW_LIMIT_EXCEEDED: "El archivo supera las 500 filas de datos.",
};

export const csvRowErrorLabels: Readonly<Record<CsvRowErrorCode, string>> = {
  COLUMN_COUNT_INVALID: "Número de columnas incorrecto",
  QUOTE_ID_INVALID: "Cotización inválida",
  QUOTE_ID_DUPLICATED: "Cotización repetida en el archivo",
  PAYER_TYPE_INVALID: "Tipo de pagador inválido",
  TERMS_VERSION_INVALID: "Versión de términos inválida",
  PRIVACY_VERSION_INVALID: "Versión de aviso de privacidad inválida",
  ACCEPTED_AT_INVALID: "Fecha de aceptación inválida",
  ACCEPTANCE_CHANNEL_INVALID: "Canal de aceptación inválido",
};

export const csvRowOutcomeLabels: Readonly<Record<CsvRowOutcomeError, string>> = {
  QUOTE_UNAVAILABLE: "La cotización ya no está disponible (usada, expirada o de otra organización).",
  IDEMPOTENCY_CONFLICT: "La fila chocó con una solicitud previa distinta.",
  INVALID_REQUEST: "El servidor rechazó la fila.",
};

/** CsvImportConflictProblem codes. */
export const csvConflictMessages: Readonly<Record<string, string>> = {
  CONFLICT:
    "El servidor rechazó el archivo (formato de carga, tamaño o contenido distinto del previsualizado). Vuelve a previsualizar.",
  IDEMPOTENCY_CONFLICT:
    "Este lote ya se confirmó con otro contenido. Vuelve a previsualizar el archivo para iniciar un lote nuevo.",
};

// ---------------------------------------------------------------------------
// Client-side file checks (before any request)

export type CsvFileProblem = "empty" | "too_large";

export function checkCsvFile(size: number): CsvFileProblem | null {
  if (!Number.isSafeInteger(size) || size <= 0) return "empty";
  if (size > maximumCsvBytes) return "too_large";
  return null;
}

export const csvFileProblemLabels: Readonly<Record<CsvFileProblem, string>> = {
  empty: "Selecciona un archivo CSV con contenido.",
  too_large: "El archivo supera 1 MiB; divídelo en lotes de hasta 500 filas.",
};

// ---------------------------------------------------------------------------
// Response parsers (fail closed)

/** Base64url SHA-256 without padding: 43 characters. */
const digestPattern = /^[A-Za-z0-9_-]{43}$/;

function digest(value: unknown): string {
  const text = boundedString(value, 43, 43);
  if (!digestPattern.test(text)) fail();
  return text;
}

function rowNumber(value: unknown): number {
  // Physical CSV line (AI-05 minimum 1); the header is line one.
  return integer(value, 1);
}

function parseRowError(value: unknown): CsvRowError {
  const object = exactObject(value, ["column", "code"]);
  return { column: boundedString(object.column, 1, 64), code: oneOf(object.code, csvRowErrorCodes) };
}

function parseRowPreview(value: unknown): CsvRowPreview {
  const object = exactObject(value, ["row_number", "quote_id", "payer_type", "valid", "errors"]);
  const row: CsvRowPreview = {
    row_number: rowNumber(object.row_number),
    quote_id: nullable(object.quote_id, uuid),
    payer_type: nullable(object.payer_type, (text) => boundedString(text, 1, 64)),
    valid: boolean(object.valid),
    errors: array(object.errors, 16).map(parseRowError),
  };
  // A valid row names its quote and payer and carries no error.
  if (row.valid && (row.errors.length > 0 || row.quote_id === null || row.payer_type === null)) fail();
  return row;
}

export function parseCsvImportPreview(value: unknown): CsvImportPreview {
  const object = exactObject(value, [
    "content_digest",
    "total_rows",
    "valid_rows",
    "invalid_rows",
    "file_errors",
    "rows",
  ]);
  const fileErrors = array(object.file_errors, csvFileErrors.length).map((code) => oneOf(code, csvFileErrors));
  if (new Set(fileErrors).size !== fileErrors.length) fail();
  const rows = array(object.rows, maximumCsvDataRows).map(parseRowPreview);
  const total = integer(object.total_rows, 0);
  const valid = integer(object.valid_rows, 0);
  const invalid = integer(object.invalid_rows, 0);
  if (total !== rows.length || valid + invalid !== total) fail();
  if (rows.filter((row) => row.valid).length !== valid) fail();
  // A file-level rejection has no reportable rows.
  if (fileErrors.length > 0 && rows.length > 0) fail();
  if (new Set(rows.map((row) => row.row_number)).size !== rows.length) fail();
  return {
    content_digest: digest(object.content_digest),
    total_rows: total,
    valid_rows: valid,
    invalid_rows: invalid,
    file_errors: fileErrors,
    rows,
  };
}

function parseRowOutcome(value: unknown): CsvRowOutcome {
  const object = exactObject(value, ["row_number", "quote_id", "status", "order_id", "public_id", "error_code"]);
  const status = oneOf(object.status, ["CREATED", "FAILED"] as const);
  const outcome: CsvRowOutcome = {
    row_number: rowNumber(object.row_number),
    quote_id: uuid(object.quote_id),
    status,
    order_id: nullable(object.order_id, uuid),
    public_id: nullable(object.public_id, (text) => boundedString(text, 1, 64)),
    error_code: nullable(object.error_code, (code) => oneOf(code, csvRowOutcomeErrors)),
  };
  const created = outcome.order_id !== null && outcome.public_id !== null && outcome.error_code === null;
  const failed = outcome.order_id === null && outcome.public_id === null && outcome.error_code !== null;
  if (status === "CREATED" ? !created : !failed) fail();
  return outcome;
}

export function parseCsvImportCommit(value: unknown): CsvImportCommit {
  const object = exactObject(value, ["content_digest", "total_rows", "created_rows", "failed_rows", "rows"]);
  const rows = array(object.rows, maximumCsvDataRows).map(parseRowOutcome);
  const total = integer(object.total_rows, 0);
  const created = integer(object.created_rows, 0);
  const failed = integer(object.failed_rows, 0);
  if (total !== rows.length || created + failed !== total) fail();
  if (rows.filter((row) => row.status === "CREATED").length !== created) fail();
  if (new Set(rows.map((row) => row.row_number)).size !== rows.length) fail();
  return {
    content_digest: digest(object.content_digest),
    total_rows: total,
    created_rows: created,
    failed_rows: failed,
    rows,
  };
}

/** Commit is offered only for a clean preview with at least one valid row. */
export function isCommittable(preview: CsvImportPreview): boolean {
  return preview.file_errors.length === 0 && preview.invalid_rows === 0 && preview.valid_rows > 0;
}
