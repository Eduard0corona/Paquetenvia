import { isMfaRequiredProblem } from "../../auth/step-up";
import { rateLimitWaits, type RateLimitedOperation } from "../../lib/rate-limit-wait";
import { readRetryAfterSeconds } from "../../lib/retry-after";
import {
  resolveRequestAuthorization,
  type RequestAuthorization,
} from "../../auth/request-credentials";
import type { OperationsSession } from "../session/operations-session";

export type TenantApiErrorCategory =
  | "unauthorized"
  | "forbidden"
  | "not_found"
  | "conflict"
  | "invalid"
  | "unavailable"
  | "network"
  /** SEC-004: a 429 (AI-05 TooManyRequests). The server applied nothing. */
  | "rate_limited";

/**
 * Failure of an AI-05 tenant operation. `code` is the stable problem code when the
 * API sent one (for example `SETTLEMENT_STATE_CONFLICT`); `mfaRequired` is true
 * only for a 403 whose problem code is `MFA_REQUIRED` (AUTH-001-MFA-STEP-UP);
 * `retryAfterSeconds` is the wait a 429 asked for (Retry-After), null when it
 * said nothing usable. The message never carries server text, identifiers or
 * personal data.
 */
export class TenantApiError extends Error {
  public constructor(
    public readonly category: TenantApiErrorCategory,
    public readonly code: string | null = null,
    public readonly mfaRequired = false,
    public readonly retryAfterSeconds: number | null = null,
  ) {
    super("La operación no está disponible.");
    this.name = "TenantApiError";
  }

  /**
   * Worth retrying with the same Idempotency-Key and the same payload. A 429 is
   * too, once its wait has passed: the request never reached the handler, so the
   * existing rule for a request without a valid answer applies (SEC-004).
   */
  public get retryable(): boolean {
    return this.category === "network" || this.category === "unavailable" || this.category === "rate_limited";
  }
}

export interface TenantRequest {
  /** PUT and DELETE only for operations AI-05 declares so (reorderRouteStops, removeRouteStop). */
  readonly method: "GET" | "POST" | "PUT" | "DELETE";
  readonly path: string;
  readonly search?: URLSearchParams;
  readonly body?: unknown;
  /**
   * multipart/form-data body, only for operations AI-05 declares as multipart
   * (previewOrderCsv, commitOrderCsv). The browser sets the boundary header.
   */
  readonly form?: FormData;
  /** Non-2xx statuses whose body the operation contract defines; returned, not thrown. */
  readonly passThroughStatuses?: readonly number[];
  readonly idempotencyKey?: string;
  readonly accept?: string;
  readonly signal?: AbortSignal;
  /**
   * SEC-004: the limited operation this request spends. A 429 records the
   * server's wait for it in the tab, so every screen that calls the operation
   * shows the countdown and sends nothing until it ends.
   */
  readonly rateLimit?: RateLimitedOperation;
}

const problemCodePattern = /^[A-Z][A-Z0-9_]{0,63}$/;

export function createTenantRequester(
  baseUrl: string,
  session: OperationsSession,
  timeoutMilliseconds = 15_000,
): (request: TenantRequest) => Promise<Response> {
  const base = new URL(baseUrl);
  if (!["http:", "https:"].includes(base.protocol)) {
    throw new Error("API base URL must use HTTP or HTTPS.");
  }

  return async (request) => {
    let authorization: RequestAuthorization;
    try {
      authorization = await resolveRequestAuthorization(session, request.method);
    } catch {
      throw new TenantApiError("unauthorized");
    }
    const headers: Record<string, string> = {
      ...authorization.headers,
      "X-Organization-Id": session.organizationId,
      Accept: request.accept ?? "application/json",
    };
    if (request.body !== undefined && request.form !== undefined) {
      throw new TenantApiError("invalid");
    }
    if (request.body !== undefined) headers["Content-Type"] = "application/json";
    if (request.idempotencyKey !== undefined)
      headers["Idempotency-Key"] = request.idempotencyKey;

    const url = new URL(request.path, base);
    if (request.search !== undefined) url.search = request.search.toString();

    const timeout = new AbortController();
    const timer = setTimeout(
      () => timeout.abort(new DOMException("The request timed out.", "TimeoutError")),
      timeoutMilliseconds,
    );
    const signal = request.signal
      ? AbortSignal.any([request.signal, timeout.signal])
      : timeout.signal;
    let response: Response;
    try {
      response = await fetch(url, {
        method: request.method,
        headers,
        body:
          request.form ??
          (request.body === undefined ? undefined : JSON.stringify(request.body)),
        cache: "no-store",
        credentials: authorization.credentials,
        referrerPolicy: "no-referrer",
        signal,
      });
    } catch {
      if (request.signal?.aborted) throw request.signal.reason;
      throw new TenantApiError("network");
    } finally {
      clearTimeout(timer);
    }
    if (!response.ok && !(request.passThroughStatuses ?? []).includes(response.status)) {
      const failure = await classifyFailure(response, request.rateLimit);
      if (failure.category === "rate_limited" && request.rateLimit !== undefined)
        rateLimitWaits.record(request.rateLimit, failure.retryAfterSeconds);
      throw failure;
    }
    return response;
  };
}

export async function readJson(
  response: Response,
  parser: (value: unknown) => unknown,
): Promise<unknown> {
  const contentType = response.headers.get("content-type") ?? "";
  if (!contentType.toLowerCase().includes("application/json")) {
    throw new TenantApiError("invalid");
  }
  let body: unknown;
  try {
    body = await response.json();
  } catch {
    throw new TenantApiError("invalid");
  }
  try {
    return parser(body);
  } catch {
    throw new TenantApiError("invalid");
  }
}

/**
 * The failure of a non-2xx answer. A 429 is `rate_limited` only for a limited operation (`rateLimit`, SEC-004), whose
 * wait the requester records; any other operation keeps reading a 429 as `unavailable`, as before SEC-004, so a screen
 * never waits for a limit it has no wait for.
 */
export async function classifyFailure(
  response: Response,
  rateLimit?: RateLimitedOperation,
): Promise<TenantApiError> {
  const body = await readProblem(response);
  const code =
    typeof body?.code === "string" && problemCodePattern.test(body.code)
      ? body.code
      : null;
  switch (response.status) {
    case 401:
      return new TenantApiError("unauthorized");
    case 403:
      return new TenantApiError(
        "forbidden",
        code,
        isMfaRequiredProblem(response.status, body),
      );
    case 404:
      return new TenantApiError("not_found");
    case 409:
      return new TenantApiError("conflict", code);
    case 400:
    case 422:
      return new TenantApiError("invalid", code);
    case 429:
      return rateLimit === undefined
        ? new TenantApiError("unavailable")
        : new TenantApiError("rate_limited", null, false, readRetryAfterSeconds(response.headers));
    default:
      return new TenantApiError("unavailable");
  }
}

async function readProblem(
  response: Response,
): Promise<Record<string, unknown> | null> {
  const contentType = response.headers.get("content-type") ?? "";
  if (!/^application\/(problem\+)?json\b/i.test(contentType)) return null;
  try {
    const body: unknown = await response.json();
    return typeof body === "object" && body !== null && !Array.isArray(body)
      ? (body as Record<string, unknown>)
      : null;
  } catch {
    return null;
  }
}

const uuidPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

export function isCanonicalUuid(value: string): boolean {
  return uuidPattern.test(value) && value !== "00000000-0000-0000-0000-000000000000";
}

export function assertUuid(value: string): void {
  if (!isCanonicalUuid(value)) throw new TenantApiError("invalid");
}
