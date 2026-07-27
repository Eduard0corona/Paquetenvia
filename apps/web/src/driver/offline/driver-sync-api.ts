import { asUuid } from "../../realtime/envelope";
import type { DriverSession } from "../session/driver-session";
import {
  type DriverOfflineOperation,
  type DriverOperationalStatus,
  type DriverProofType,
} from "./operation-contract";

export type DriverSyncApiFailure =
  | "unauthorized"
  | "forbidden"
  | "not-found"
  | "conflict"
  | "recoverable"
  | "session-expired"
  | "invalid-contract"
  | "cancelled";

export class DriverSyncApiError extends Error {
  public constructor(
    public readonly category: DriverSyncApiFailure,
    public readonly publicCode: string | null = null,
  ) {
    super("No fue posible sincronizar la operación.");
    this.name = "DriverSyncApiError";
  }
}

export interface DriverTransitionReceipt {
  readonly id: string;
  readonly status: DriverOperationalStatus;
  readonly version: number;
}

export interface DriverUploadGrant {
  readonly id: string;
  readonly status: string;
  readonly uploadUrl: string;
  readonly expiresAt: string;
  readonly requiredHeaders: Readonly<Record<string, string>>;
}

export interface DriverProofReceipt {
  readonly id: string;
  readonly proofType: DriverProofType;
  readonly sha256: string;
  readonly capturedAt: string;
}

export interface DriverSyncApi {
  transitionOrder(
    operation: DriverOfflineOperation,
    signal?: AbortSignal,
  ): Promise<DriverTransitionReceipt>;
  createProofUploadSession(
    operation: DriverOfflineOperation,
    proof: {
      readonly contentType: "image/jpeg" | "image/png";
      readonly sizeBytes: number;
      readonly sha256: string;
    },
    signal?: AbortSignal,
  ): Promise<DriverUploadGrant>;
  finalizeProof(
    operation: DriverOfflineOperation,
    uploadSessionId: string,
    sha256: string,
    signal?: AbortSignal,
  ): Promise<DriverProofReceipt>;
}

export interface DriverSyncApiOptions {
  readonly baseUrl: string;
  readonly session: DriverSession;
  readonly timeoutMilliseconds?: number;
  readonly production?: boolean;
  readonly fetch?: typeof fetch;
}

export function createDriverSyncApi(
  options: DriverSyncApiOptions,
): DriverSyncApi {
  const fetchImplementation = options.fetch ?? fetch;
  const timeoutMilliseconds = options.timeoutMilliseconds ?? 10_000;

  return Object.freeze({
    transitionOrder: (
      operation: DriverOfflineOperation,
      signal?: AbortSignal,
    ) =>
      requestJson(
        options,
        fetchImplementation,
        timeoutMilliseconds,
        `/api/v1/orders/${operation.orderId}/transitions`,
        200,
        operation.transitionIdempotencyKey,
        {
          target_status: operation.targetStatus,
          reason: operation.reason,
          expected_version: operation.expectedVersion,
          metadata: {},
        },
        parseTransitionReceipt,
        signal,
      ),
    createProofUploadSession: (
      operation: DriverOfflineOperation,
      proof: {
        readonly contentType: "image/jpeg" | "image/png";
        readonly sizeBytes: number;
        readonly sha256: string;
      },
      signal?: AbortSignal,
    ) =>
      requestJson(
        options,
        fetchImplementation,
        timeoutMilliseconds,
        `/api/v1/orders/${operation.orderId}/proof-upload-sessions`,
        201,
        requireProofKey(operation.sessionIdempotencyKey),
        {
          proof_type: proofTypeFor(operation),
          content_type: proof.contentType,
          size_bytes: proof.sizeBytes,
          sha256: proof.sha256,
        },
        (value) =>
          parseUploadGrant(value, options.production ?? false),
        signal,
      ),
    finalizeProof: (
      operation: DriverOfflineOperation,
      uploadSessionId: string,
      sha256: string,
      signal?: AbortSignal,
    ) =>
      requestJson(
        options,
        fetchImplementation,
        timeoutMilliseconds,
        `/api/v1/orders/${operation.orderId}/proofs`,
        201,
        requireProofKey(operation.finalizeIdempotencyKey),
        {
          upload_session_id: asUuid(uploadSessionId),
          proof_type: proofTypeFor(operation),
          captured_at: operation.clientOccurredAt,
          sha256,
        },
        parseProofReceipt,
        signal,
      ),
  });
}

export async function uploadDriverProof(
  grant: DriverUploadGrant,
  blob: Blob,
  options: {
    readonly timeoutMilliseconds?: number;
    readonly fetch?: typeof fetch;
    readonly signal?: AbortSignal;
  } = {},
): Promise<void> {
  const fetchImplementation = options.fetch ?? fetch;
  const controller = new AbortController();
  const onAbort = () => controller.abort();
  options.signal?.addEventListener("abort", onAbort, { once: true });
  const timeout = globalThis.setTimeout(
    () => controller.abort(),
    options.timeoutMilliseconds ?? 30_000,
  );
  try {
    if (options.signal?.aborted) {
      throw new DriverSyncApiError("cancelled");
    }
    const response = await fetchImplementation(grant.uploadUrl, {
      method: "PUT",
      body: blob,
      headers: grant.requiredHeaders,
      credentials: "omit",
      cache: "no-store",
      referrerPolicy: "no-referrer",
      signal: controller.signal,
    });
    if (!response.ok) {
      throw new DriverSyncApiError(
        response.status === 401 || response.status === 403
          ? "session-expired"
          : response.status >= 500
            ? "recoverable"
            : "conflict",
      );
    }
  } catch (error) {
    if (error instanceof DriverSyncApiError) throw error;
    if (controller.signal.aborted) {
      throw new DriverSyncApiError(
        options.signal?.aborted ? "cancelled" : "recoverable",
      );
    }
    throw new DriverSyncApiError("recoverable");
  } finally {
    globalThis.clearTimeout(timeout);
    options.signal?.removeEventListener("abort", onAbort);
  }
}

async function requestJson<T>(
  options: DriverSyncApiOptions,
  fetchImplementation: typeof fetch,
  timeoutMilliseconds: number,
  path: string,
  expectedStatus: number,
  idempotencyKey: string,
  body: unknown,
  parser: (value: unknown) => T,
  signal?: AbortSignal,
): Promise<T> {
  const controller = new AbortController();
  const onAbort = () => controller.abort();
  signal?.addEventListener("abort", onAbort, { once: true });
  const timeout = globalThis.setTimeout(
    () => controller.abort(),
    timeoutMilliseconds,
  );
  try {
    if (signal?.aborted) throw new DriverSyncApiError("cancelled");
    const token = await options.session.getAccessToken();
    if (typeof token !== "string" || token.length < 1) {
      throw new DriverSyncApiError("unauthorized");
    }
    const response = await fetchImplementation(new URL(path, options.baseUrl), {
      method: "POST",
      cache: "no-store",
      credentials: "omit",
      signal: controller.signal,
      headers: {
        Accept: "application/json",
        Authorization: `Bearer ${token}`,
        "Content-Type": "application/json",
        "Idempotency-Key": idempotencyKey,
        "X-Organization-Id": options.session.organizationId,
      },
      body: JSON.stringify(body),
    });
    if (response.status !== expectedStatus) {
      throw await classifyResponse(response);
    }
    assertJsonContentType(response);
    try {
      return parser(await response.json());
    } catch (error) {
      if (error instanceof DriverSyncApiError) throw error;
      throw new DriverSyncApiError("invalid-contract");
    }
  } catch (error) {
    if (error instanceof DriverSyncApiError) throw error;
    if (controller.signal.aborted) {
      throw new DriverSyncApiError(
        signal?.aborted ? "cancelled" : "recoverable",
      );
    }
    throw new DriverSyncApiError("recoverable");
  } finally {
    globalThis.clearTimeout(timeout);
    signal?.removeEventListener("abort", onAbort);
  }
}

async function classifyResponse(
  response: Response,
): Promise<DriverSyncApiError> {
  if (response.status === 401) return new DriverSyncApiError("unauthorized");
  if (response.status === 403) return new DriverSyncApiError("forbidden");
  if (response.status === 404) return new DriverSyncApiError("not-found");
  if (response.status >= 500) return new DriverSyncApiError("recoverable");
  if (response.status === 409) {
    return new DriverSyncApiError(
      "conflict",
      await readProblemCode(response),
    );
  }
  return new DriverSyncApiError("invalid-contract");
}

async function readProblemCode(response: Response): Promise<string | null> {
  const contentType = response.headers.get("content-type")?.toLowerCase() ?? "";
  if (
    !contentType.startsWith("application/problem+json") &&
    !contentType.startsWith("application/json")
  ) {
    return null;
  }
  try {
    const record = readRecord((await response.json()) as unknown);
    const allowedKeys = new Set([
      "type",
      "title",
      "status",
      "detail",
      "instance",
      "code",
      "traceId",
    ]);
    if (
      Object.keys(record).length < 1 ||
      Object.keys(record).some((key) => !allowedKeys.has(key))
    ) {
      return null;
    }
    for (const key of ["type", "title", "detail", "instance", "traceId"]) {
      if (
        record[key] !== undefined &&
        (typeof record[key] !== "string" ||
          (record[key] as string).length > 2048)
      ) {
        return null;
      }
    }
    if (
      record.status !== undefined &&
      (!Number.isSafeInteger(record.status) ||
        (record.status as number) < 400 ||
        (record.status as number) > 599)
    ) {
      return null;
    }
    const code = record.code;
    return typeof code === "string" && /^[A-Z0-9_]{1,80}$/.test(code)
      ? code
      : null;
  } catch {
    return null;
  }
}

function assertJsonContentType(response: Response): void {
  const contentType = response.headers.get("content-type")?.toLowerCase() ?? "";
  if (!contentType.startsWith("application/json")) {
    throw new DriverSyncApiError("invalid-contract");
  }
}

function parseTransitionReceipt(value: unknown): DriverTransitionReceipt {
  const record = readExactRecord(
    value,
    new Set([
      "id",
      "public_id",
      "owner_org_id",
      "operator_org_id",
      "status",
      "price_net",
      "version",
      "origin_location_id",
      "destination_location_id",
      "service_type",
      "quote_id",
      "city_id",
      "service_area_id",
      "pricing_tier",
      "total",
      "claim_window_ends_at",
      "finalized_at",
    ]),
  );
  const id = readUuid(record.id);
  readText(record.public_id, 128);
  readUuid(record.owner_org_id);
  readNullableUuid(record.operator_org_id);
  const status = readOperationalStatus(record.status);
  readMoney(record.price_net);
  const version = readPositiveInteger(record.version);
  readUuid(record.origin_location_id);
  readUuid(record.destination_location_id);
  readText(record.service_type, 80);
  readUuid(record.quote_id);
  readUuid(record.city_id);
  readNullableUuid(record.service_area_id);
  readText(record.pricing_tier, 80);
  readMoney(record.total);
  readNullableUtcTimestamp(record.claim_window_ends_at);
  readNullableUtcTimestamp(record.finalized_at);
  return Object.freeze({ id, status, version });
}

function parseUploadGrant(
  value: unknown,
  production: boolean,
): DriverUploadGrant {
  const record = readExactRecord(
    value,
    new Set([
      "id",
      "status",
      "upload_url",
      "object_key",
      "expires_at",
      "required_headers",
    ]),
  );
  const url = readSignedUrl(record.upload_url, production);
  readText(record.object_key, 1024);
  const status = readUploadSessionStatus(record.status);
  const requiredHeaders = readRequiredHeaders(record.required_headers);
  return Object.freeze({
    id: readUuid(record.id),
    status,
    uploadUrl: url,
    expiresAt: readUtcTimestamp(record.expires_at),
    requiredHeaders,
  });
}

function parseProofReceipt(value: unknown): DriverProofReceipt {
  const record = readExactRecord(
    value,
    new Set(["id", "proof_type", "sha256", "captured_at"]),
  );
  const proofType = record.proof_type;
  if (proofType !== "PICKUP_PHOTO" && proofType !== "DELIVERY_PHOTO") {
    throw new DriverSyncApiError("invalid-contract");
  }
  const sha256 = record.sha256;
  if (typeof sha256 !== "string" || !/^[a-f0-9]{64}$/.test(sha256)) {
    throw new DriverSyncApiError("invalid-contract");
  }
  return Object.freeze({
    id: readUuid(record.id),
    proofType,
    sha256,
    capturedAt: readUtcTimestamp(record.captured_at),
  });
}

function readSignedUrl(value: unknown, production: boolean): string {
  if (typeof value !== "string") {
    throw new DriverSyncApiError("invalid-contract");
  }
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new DriverSyncApiError("invalid-contract");
  }
  if (
    (url.protocol !== "https:" && url.protocol !== "http:") ||
    (production && url.protocol !== "https:") ||
    url.username.length > 0 ||
    url.password.length > 0 ||
    url.hash.length > 0
  ) {
    throw new DriverSyncApiError("invalid-contract");
  }
  return url.toString();
}

function readRequiredHeaders(
  value: unknown,
): Readonly<Record<string, string>> {
  const record = readRecord(value);
  const entries = Object.entries(record);
  if (entries.length < 1 || entries.length > 20) {
    throw new DriverSyncApiError("invalid-contract");
  }
  const headers: Record<string, string> = {};
  for (const [name, headerValue] of entries) {
    const lower = name.toLowerCase();
    if (
      !/^[a-z0-9-]{1,80}$/.test(lower) ||
      typeof headerValue !== "string" ||
      headerValue.length < 1 ||
      headerValue.length > 2048 ||
      [
        "authorization",
        "cookie",
        "host",
        "content-length",
        "idempotency-key",
        "x-organization-id",
      ].includes(lower)
    ) {
      throw new DriverSyncApiError("invalid-contract");
    }
    headers[name] = headerValue;
  }
  return Object.freeze(headers);
}

function proofTypeFor(operation: DriverOfflineOperation): DriverProofType {
  if (operation.kind === "PICKUP_PROOF") return "PICKUP_PHOTO";
  if (operation.kind === "DELIVERY_PROOF") return "DELIVERY_PHOTO";
  throw new DriverSyncApiError("invalid-contract");
}

function requireProofKey(value: string | null): string {
  if (value === null) throw new DriverSyncApiError("invalid-contract");
  return value;
}

function readUploadSessionStatus(value: unknown): string {
  if (
    typeof value !== "string" ||
    ![
      "CREATED",
      "UPLOADED",
      "VALIDATING",
      "READY",
      "REJECTED",
      "EXPIRED",
      "CONSUMED",
    ].includes(value)
  ) {
    throw new DriverSyncApiError("invalid-contract");
  }
  return value;
}

function readExactRecord(
  value: unknown,
  keys: ReadonlySet<string>,
): Record<string, unknown> {
  const record = readRecord(value);
  const actual = Object.keys(record);
  if (actual.length !== keys.size || actual.some((key) => !keys.has(key))) {
    throw new DriverSyncApiError("invalid-contract");
  }
  return record;
}

function readRecord(value: unknown): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw new DriverSyncApiError("invalid-contract");
  }
  return value as Record<string, unknown>;
}

function readUuid(value: unknown): string {
  if (typeof value !== "string") {
    throw new DriverSyncApiError("invalid-contract");
  }
  try {
    return asUuid(value);
  } catch {
    throw new DriverSyncApiError("invalid-contract");
  }
}

function readNullableUuid(value: unknown): string | null {
  return value === null ? null : readUuid(value);
}

function readMoney(value: unknown): void {
  const record = readExactRecord(
    value,
    new Set(["currency", "amount_cents"]),
  );
  if (
    typeof record.currency !== "string" ||
    !/^[A-Z]{3}$/.test(record.currency) ||
    !Number.isSafeInteger(record.amount_cents) ||
    (record.amount_cents as number) < 0
  ) {
    throw new DriverSyncApiError("invalid-contract");
  }
}

function readText(value: unknown, maximumLength: number): string {
  if (
    typeof value !== "string" ||
    value.length < 1 ||
    value.length > maximumLength
  ) {
    throw new DriverSyncApiError("invalid-contract");
  }
  return value;
}

function readPositiveInteger(value: unknown): number {
  if (!Number.isSafeInteger(value) || (value as number) < 1) {
    throw new DriverSyncApiError("invalid-contract");
  }
  return value as number;
}

function readOperationalStatus(value: unknown): DriverOperationalStatus {
  if (
    typeof value !== "string" ||
    ![
      "ASSIGNED",
      "AT_PICKUP",
      "PICKED_UP",
      "IN_TRANSIT",
      "DELIVERING",
      "DELIVERED",
    ].includes(value)
  ) {
    throw new DriverSyncApiError("invalid-contract");
  }
  return value as DriverOperationalStatus;
}

function readUtcTimestamp(value: unknown): string {
  if (
    typeof value !== "string" ||
    !/(?:Z|\+00:00)$/.test(value) ||
    !Number.isFinite(Date.parse(value))
  ) {
    throw new DriverSyncApiError("invalid-contract");
  }
  return new Date(value).toISOString();
}

function readNullableUtcTimestamp(value: unknown): string | null {
  return value === null ? null : readUtcTimestamp(value);
}
