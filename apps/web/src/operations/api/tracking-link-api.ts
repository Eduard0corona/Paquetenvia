import type { OperationsOrganizationContext } from "../contracts/operations-dashboard";
import { capabilityMatrix } from "../contracts/capabilities";
import type { OperationsSession } from "../session/operations-session";
import {
  assertUuid,
  createTenantRequester,
  TenantApiError,
} from "./tenant-request";

/**
 * TRK-002-ISSUE-ENDPOINT: issueTrackingLink and revokeTrackingLink (AI-05).
 *
 * The plaintext token exists only in the value this module returns. It is never
 * written to browser storage, never logged and never placed in an error: every
 * failure is a {@link TenantApiError} carrying a category (and, for a 403 whose
 * only unmet requirement is a second factor, `mfaRequired`) only.
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

/**
 * Roles that hold issueTrackingLink and revokeTrackingLink in AI-05 x-capability-matrix
 * tracking_link_operations. PLATFORM_ADMIN also needs a satisfied MFA challenge; the
 * API answers 403 MFA_REQUIRED and the panel offers the step-up.
 */
export const trackingLinkRoles: readonly string[] = capabilityMatrix.issueTrackingLink;

const uuidPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const tokenPattern = /^[A-Za-z0-9_-]{43}$/;

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
  if (!tokenPattern.test(token)) throw new TenantApiError("invalid");
  const base = new URL(origin);
  if (!["http:", "https:"].includes(base.protocol))
    throw new TenantApiError("invalid");
  return new URL(`/track/${token}`, base).toString();
}

export function parsePublicTrackingLink(value: unknown): PublicTrackingLink {
  if (typeof value !== "object" || value === null || Array.isArray(value))
    throw new TenantApiError("invalid");
  const object = value as Record<string, unknown>;
  const keys = Object.keys(object).sort();
  if (keys.join(",") !== "expires_at,order_id,token,token_id")
    throw new TenantApiError("invalid");
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
    throw new TenantApiError("invalid");
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
  const request = createTenantRequester(baseUrl, session);

  const post = (path: string, idempotencyKey: string, signal?: AbortSignal) =>
    request({ method: "POST", path, idempotencyKey, signal });

  return {
    async issue(orderId, idempotencyKey, signal) {
      assertUuid(orderId);
      const response = await post(
        `/api/v1/orders/${encodeURIComponent(orderId)}/tracking-link`,
        idempotencyKey,
        signal,
      );
      if (response.status !== 201) throw new TenantApiError("invalid");
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
