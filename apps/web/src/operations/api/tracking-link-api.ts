import type { OperationsOrganizationContext } from "../contracts/operations-dashboard";
import type { OperationsSession } from "../session/operations-session";
import { resolveRequestAuthorization } from "../../auth/request-credentials";
import { OperationsApiError } from "./operations-api";

/**
 * TRK-002-ISSUE-ENDPOINT: issueTrackingLink and revokeTrackingLink (AI-05).
 *
 * The plaintext token exists only in the value this module returns. It is never
 * written to browser storage, never logged and never placed in an error: every
 * failure is an {@link OperationsApiError} carrying a category only.
 */
export interface PublicTrackingLink {
  readonly tokenId: string;
  readonly orderId: string;
  readonly token: string;
  readonly expiresAt: string;
}

export interface TrackingLinkApi {
  issue(
    orderId: string,
    idempotencyKey: string,
    signal?: AbortSignal,
  ): Promise<PublicTrackingLink>;
  revoke(
    orderId: string,
    idempotencyKey: string,
    signal?: AbortSignal,
  ): Promise<void>;
}

/** Roles that hold issueTrackingLink and revokeTrackingLink in AI-05 x-capability-matrix. */
export const trackingLinkRoles: readonly string[] = [
  "DISPATCHER",
  "PLATFORM_ADMIN",
];

const uuidPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const tokenPattern = /^[A-Za-z0-9_-]{43}$/;
const nilUuid = "00000000-0000-0000-0000-000000000000";

/**
 * Whether the tracking link actions are offered. The backend and RLS remain the
 * barrier; this only hides actions the server would refuse: the selected
 * organization must own the order and the member must hold one of the roles.
 */
export function canManageTrackingLink(
  contexts: readonly OperationsOrganizationContext[],
  organizationId: string,
  ownerOrganizationId: string,
): boolean {
  return (
    organizationId === ownerOrganizationId &&
    contexts.some(
      (context) =>
        context.organization_id === organizationId &&
        trackingLinkRoles.includes(context.role),
    )
  );
}

/** One key per click: a retry of a failed request is a new request. */
export function createTrackingLinkIdempotencyKey(
  randomUuid: () => string = () => crypto.randomUUID(),
): string {
  return `tracking-link-${randomUuid()}`;
}

/** The public page for a token, on the origin that serves /track (PILOT-SAME-ORIGIN-ROUTING). */
export function publicTrackingUrl(origin: string, token: string): string {
  if (!tokenPattern.test(token)) throw new OperationsApiError("invalid");
  const base = new URL(origin);
  if (!["http:", "https:"].includes(base.protocol))
    throw new OperationsApiError("invalid");
  return new URL(`/track/${token}`, base).toString();
}

export function parsePublicTrackingLink(value: unknown): PublicTrackingLink {
  if (typeof value !== "object" || value === null || Array.isArray(value))
    throw new OperationsApiError("invalid");
  const object = value as Record<string, unknown>;
  const keys = Object.keys(object).sort();
  if (keys.join(",") !== "expires_at,order_id,token,token_id")
    throw new OperationsApiError("invalid");
  const { token_id, order_id, token, expires_at } = object;
  if (
    typeof token_id !== "string" ||
    !uuidPattern.test(token_id) ||
    typeof order_id !== "string" ||
    !uuidPattern.test(order_id) ||
    typeof token !== "string" ||
    !tokenPattern.test(token) ||
    typeof expires_at !== "string" ||
    Number.isNaN(Date.parse(expires_at))
  )
    throw new OperationsApiError("invalid");
  return {
    tokenId: token_id,
    orderId: order_id,
    token,
    expiresAt: new Date(expires_at).toISOString(),
  };
}

export function createTrackingLinkApi(
  baseUrl: string,
  session: OperationsSession,
): TrackingLinkApi {
  const base = new URL(baseUrl);
  if (!["http:", "https:"].includes(base.protocol)) {
    throw new Error("Operations API base URL must use HTTP or HTTPS.");
  }

  async function post(
    path: string,
    idempotencyKey: string,
    signal?: AbortSignal,
  ): Promise<Response> {
    let authorization: Awaited<ReturnType<typeof resolveRequestAuthorization>>;
    try {
      authorization = await resolveRequestAuthorization(session, "POST");
    } catch {
      throw new OperationsApiError("unauthorized");
    }
    let response: Response;
    try {
      response = await fetch(new URL(path, base), {
        method: "POST",
        headers: {
          ...authorization.headers,
          "X-Organization-Id": session.organizationId,
          "Idempotency-Key": idempotencyKey,
          Accept: "application/json",
        },
        cache: "no-store",
        credentials: authorization.credentials,
        referrerPolicy: "no-referrer",
        signal,
      });
    } catch {
      if (signal?.aborted) throw signal.reason;
      throw new OperationsApiError("network");
    }
    if (!response.ok) throw classify(response.status);
    return response;
  }

  return {
    async issue(orderId, idempotencyKey, signal) {
      assertUuid(orderId);
      const response = await post(
        `/api/v1/orders/${encodeURIComponent(orderId)}/tracking-link`,
        idempotencyKey,
        signal,
      );
      if (response.status !== 201) throw new OperationsApiError("invalid");
      const contentType = response.headers.get("content-type") ?? "";
      if (!contentType.toLowerCase().includes("application/json"))
        throw new OperationsApiError("invalid");
      let body: unknown;
      try {
        body = await response.json();
      } catch {
        throw new OperationsApiError("invalid");
      }
      const link = parsePublicTrackingLink(body);
      if (link.orderId !== orderId) throw new OperationsApiError("invalid");
      return link;
    },
    async revoke(orderId, idempotencyKey, signal) {
      assertUuid(orderId);
      const response = await post(
        `/api/v1/orders/${encodeURIComponent(orderId)}/tracking-link/revoke`,
        idempotencyKey,
        signal,
      );
      if (response.status !== 204) throw new OperationsApiError("invalid");
    },
  };
}

function assertUuid(value: string): void {
  if (!uuidPattern.test(value) || value === nilUuid)
    throw new OperationsApiError("invalid");
}

function classify(status: number): OperationsApiError {
  if (status === 401) return new OperationsApiError("unauthorized");
  if (status === 403) return new OperationsApiError("forbidden");
  if (status === 404) return new OperationsApiError("not_found");
  if (status === 409) return new OperationsApiError("conflict");
  if (status === 400) return new OperationsApiError("invalid");
  return new OperationsApiError("unavailable");
}
