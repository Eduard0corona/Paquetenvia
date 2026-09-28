import { isMfaRequiredProblem } from "../../auth/step-up";
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
  | "network";

/**
 * Failure of an AI-05 tenant operation. `code` is the stable problem code when the
 * API sent one (for example `SETTLEMENT_STATE_CONFLICT`); `mfaRequired` is true
 * only for a 403 whose problem code is `MFA_REQUIRED` (AUTH-001-MFA-STEP-UP). The
 * message never carries server text, identifiers or personal data.
 */
export class TenantApiError extends Error {
  public constructor(
    public readonly category: TenantApiErrorCategory,
    public readonly code: string | null = null,
    public readonly mfaRequired = false,
  ) {
    super("La operación no está disponible.");
    this.name = "TenantApiError";
  }

  /** Worth retrying with the same Idempotency-Key and the same payload. */
  public get retryable(): boolean {
    return this.category === "network" || this.category === "unavailable";
  }
}

export interface TenantRequest {
  readonly method: "GET" | "POST";
  readonly path: string;
  readonly search?: URLSearchParams;
  readonly body?: unknown;
  readonly idempotencyKey?: string;
  readonly accept?: string;
  readonly signal?: AbortSignal;
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
        body: request.body === undefined ? undefined : JSON.stringify(request.body),
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
    if (!response.ok) throw await classifyFailure(response);
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

export async function classifyFailure(response: Response): Promise<TenantApiError> {
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
