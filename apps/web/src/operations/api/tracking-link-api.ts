import type { OperationsOrganizationContext } from "../contracts/operations-dashboard";
import { capabilityMatrix } from "../contracts/capabilities";
import type { OperationsSession } from "../session/operations-session";
import {
  assertUuid,
  createTenantRequester,
  TenantApiError,
} from "./tenant-request";

/**
 * TRK-002-AUTO-LINK: issueTrackingLink (get-or-create) and revokeTrackingLink (AI-05).
 *
 * Every order receives its public tracking link when it is created; issueTrackingLink
 * returns that same link, with its public URL, on every call and never rotates it.
 * The plaintext token and the URL exist only in the value this module returns. They
 * are never written to browser storage, never logged and never placed in an error:
 * every failure is a {@link TenantApiError} carrying a category, the stable problem
 * code (for example TRACKING_LINK_ORDER_FINISHED) and, for a 403 whose only unmet
 * requirement is a second factor, `mfaRequired`.
 */
export interface PublicTrackingLink {
  readonly tokenId: string;
  readonly orderId: string;
  readonly token: string;
  /** The public page, `{PublicTracking:PublicBaseUrl}/track/{token}`, built by the server. */
  readonly url: string;
  readonly generation: number;
  /** Null while the order is in progress; the end of the 24-hour grace once it is finished. */
  readonly validUntil: string | null;
}

export interface TrackingLinkApi {
  getOrCreate(
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

/** AI-05 TrackingLinkConflictProblem: the order is finished and gets no new link. */
export const trackingLinkOrderFinishedCode = "TRACKING_LINK_ORDER_FINISHED";

/**
 * Roles that hold issueTrackingLink and revokeTrackingLink in AI-05 x-capability-matrix
 * tracking_link_operations. PLATFORM_ADMIN also needs a satisfied MFA challenge; the
 * API answers 403 MFA_REQUIRED and the panel offers the step-up.
 */
export const trackingLinkRoles: readonly string[] = capabilityMatrix.issueTrackingLink;

const uuidPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const tokenPattern = /^[A-Za-z0-9_-]{43}$/;
const loopbackHosts = new Set(["localhost", "127.0.0.1", "[::1]"]);

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

/** One key per click: a retry of a failed request is a new request (and still the same link). */
export function createTrackingLinkIdempotencyKey(
  randomUuid: () => string = () => crypto.randomUUID(),
): string {
  return `tracking-link-${randomUuid()}`;
}

/**
 * The server-built public URL must be exactly `https://{host}/track/{token}` for the
 * token in the same response (an http loopback origin is accepted for local
 * development only). Anything else is refused rather than shown.
 */
export function isPublicTrackingUrl(value: string, token: string): boolean {
  if (!tokenPattern.test(token)) return false;
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return false;
  }
  const secure =
    url.protocol === "https:" ||
    (url.protocol === "http:" && loopbackHosts.has(url.hostname));
  return (
    secure &&
    url.username === "" &&
    url.password === "" &&
    url.search === "" &&
    url.hash === "" &&
    url.pathname === `/track/${token}` &&
    value === `${url.origin}/track/${token}`
  );
}

export function parsePublicTrackingLink(value: unknown): PublicTrackingLink {
  if (typeof value !== "object" || value === null || Array.isArray(value))
    throw new TenantApiError("invalid");
  const object = value as Record<string, unknown>;
  const keys = Object.keys(object).sort();
  if (keys.join(",") !== "generation,order_id,token,token_id,url,valid_until")
    throw new TenantApiError("invalid");
  const { token_id, order_id, token, url, generation, valid_until } = object;
  if (
    typeof token_id !== "string" ||
    !uuidPattern.test(token_id) ||
    typeof order_id !== "string" ||
    !uuidPattern.test(order_id) ||
    typeof token !== "string" ||
    !tokenPattern.test(token) ||
    typeof url !== "string" ||
    !isPublicTrackingUrl(url, token) ||
    typeof generation !== "number" ||
    !Number.isInteger(generation) ||
    generation < 1 ||
    (valid_until !== null &&
      (typeof valid_until !== "string" || Number.isNaN(Date.parse(valid_until))))
  )
    throw new TenantApiError("invalid");
  return {
    tokenId: token_id,
    orderId: order_id,
    token,
    url,
    generation,
    validUntil:
      valid_until === null ? null : new Date(valid_until as string).toISOString(),
  };
}

export function createTrackingLinkApi(
  baseUrl: string,
  session: OperationsSession,
): TrackingLinkApi {
  const request = createTenantRequester(baseUrl, session);

  const post = (path: string, idempotencyKey: string, signal?: AbortSignal) =>
    request({ method: "POST", path, idempotencyKey, signal });

  return {
    async getOrCreate(orderId, idempotencyKey, signal) {
      assertUuid(orderId);
      const response = await post(
        `/api/v1/orders/${encodeURIComponent(orderId)}/tracking-link`,
        idempotencyKey,
        signal,
      );
      if (response.status !== 200) throw new TenantApiError("invalid");
      const contentType = response.headers.get("content-type") ?? "";
      if (!contentType.toLowerCase().includes("application/json"))
        throw new TenantApiError("invalid");
      let body: unknown;
      try {
        body = await response.json();
      } catch {
        throw new TenantApiError("invalid");
      }
      const link = parsePublicTrackingLink(body);
      if (link.orderId !== orderId) throw new TenantApiError("invalid");
      return link;
    },
    async revoke(orderId, idempotencyKey, signal) {
      assertUuid(orderId);
      const response = await post(
        `/api/v1/orders/${encodeURIComponent(orderId)}/tracking-link/revoke`,
        idempotencyKey,
        signal,
      );
      if (response.status !== 204) throw new TenantApiError("invalid");
    },
  };
}
