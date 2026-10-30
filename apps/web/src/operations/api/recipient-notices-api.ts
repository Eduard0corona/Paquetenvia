import type { OperationsOrganizationContext } from "../contracts/operations-dashboard";
import { recipientNoticeOperationsMatrix } from "../contracts/capabilities";
import {
  recipientNoticeChannels,
  recipientNoticeKinds,
  recipientNoticeLastCodes,
  recipientNoticeStatuses,
  type RecipientNotice,
  type RecipientNoticeChannel,
  type RecipientNoticeList,
  type RecipientNoticeStopResult,
} from "../contracts/recipient-notices";
import type { OperationsSession } from "../session/operations-session";
import { assertUuid, createTenantRequester, readJson, TenantApiError } from "./tenant-request";

/**
 * NTF-RECIPIENT-NOTICE-PANEL-2026-10-30 (AI-08 NTF-007): listOrderRecipientNotices and stopOrderRecipientNotices
 * (AI-05). Both answers are parsed strictly - exactly the AI-05 keys and closed values - so nothing the contract does
 * not name (a phone, an e-mail, a link) can reach the panel. Nothing is written to browser storage or logged; every
 * failure is a {@link TenantApiError} with a category, the stable problem code and, for a 403 whose only unmet
 * requirement is a second factor, `mfaRequired`.
 */
export interface RecipientNoticesApi {
  list(orderId: string, signal?: AbortSignal): Promise<RecipientNoticeList>;
  stop(orderId: string, idempotencyKey: string, signal?: AbortSignal): Promise<RecipientNoticeStopResult>;
}

/** AI-05 x-capability-matrix recipient_notice_operations: the roles of the panel (PLATFORM_ADMIN with MFA). */
export const recipientNoticeRoles: readonly string[] = recipientNoticeOperationsMatrix.listOrderRecipientNotices;

/**
 * Whether the panel is mounted (AI-07 shown_when): the selected organization owns the order and the member holds one
 * of the roles. The backend and RLS remain the barrier; an operator organization never sees the panel.
 */
export function canManageRecipientNotices(
  contexts: readonly OperationsOrganizationContext[],
  organizationId: string,
  ownerOrganizationId: string,
): boolean {
  return (
    organizationId === ownerOrganizationId &&
    contexts.some(
      (context) => context.organization_id === organizationId && recipientNoticeRoles.includes(context.role),
    )
  );
}

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

function exactObject(value: unknown, keys: readonly string[]): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) throw new TenantApiError("invalid");
  const object = value as Record<string, unknown>;
  if (Object.keys(object).sort().join(",") !== [...keys].sort().join(",")) throw new TenantApiError("invalid");
  return object;
}

function uuid(value: unknown): string {
  if (typeof value !== "string" || !uuidPattern.test(value.toLowerCase())) throw new TenantApiError("invalid");
  return value.toLowerCase();
}

function instant(value: unknown): string {
  if (typeof value !== "string" || Number.isNaN(Date.parse(value))) throw new TenantApiError("invalid");
  return new Date(value).toISOString();
}

function optionalInstant(value: unknown): string | null {
  return value === null ? null : instant(value);
}

function oneOf<T extends string>(value: unknown, allowed: readonly T[]): T {
  if (typeof value !== "string" || !(allowed as readonly string[]).includes(value)) throw new TenantApiError("invalid");
  return value as T;
}

function parseRecipientNotice(value: unknown): RecipientNotice {
  const object = exactObject(value, [
    "notification_id",
    "notice",
    "channel",
    "status",
    "attempts",
    "last_code",
    "sent_at",
    "delivered_at",
    "updated_at",
  ]);
  const attempts = object.attempts;
  if (typeof attempts !== "number" || !Number.isSafeInteger(attempts) || attempts < 0)
    throw new TenantApiError("invalid");
  return {
    notificationId: uuid(object.notification_id),
    notice: oneOf(object.notice, recipientNoticeKinds),
    channel: oneOf(object.channel, recipientNoticeChannels),
    status: oneOf(object.status, recipientNoticeStatuses),
    attempts,
    lastCode: object.last_code === null ? null : oneOf(object.last_code, recipientNoticeLastCodes),
    sentAt: optionalInstant(object.sent_at),
    deliveredAt: optionalInstant(object.delivered_at),
    updatedAt: instant(object.updated_at),
  };
}

/** AI-05 RecipientNoticeList: at most 100 items, exactly its keys. */
export function parseRecipientNoticeList(value: unknown): RecipientNoticeList {
  const object = exactObject(value, ["items", "whatsapp_consent_attested", "email_on_file"]);
  const { items, whatsapp_consent_attested: consent, email_on_file: email } = object;
  if (!Array.isArray(items) || items.length > 100 || typeof consent !== "boolean" || typeof email !== "boolean")
    throw new TenantApiError("invalid");
  return {
    items: items.map(parseRecipientNotice),
    whatsappConsentAttested: consent,
    emailOnFile: email,
  };
}

/** AI-05 RecipientNoticeStopResult: one or two distinct channels. */
export function parseRecipientNoticeStopResult(value: unknown): RecipientNoticeStopResult {
  const object = exactObject(value, ["order_id", "channels", "recorded_at"]);
  const channels = object.channels;
  if (!Array.isArray(channels) || channels.length < 1 || channels.length > 2) throw new TenantApiError("invalid");
  const parsed = channels.map((channel) => oneOf(channel, recipientNoticeChannels));
  if (new Set(parsed).size !== parsed.length) throw new TenantApiError("invalid");
  return {
    orderId: uuid(object.order_id),
    channels: parsed as readonly RecipientNoticeChannel[],
    recordedAt: instant(object.recorded_at),
  };
}

export function createRecipientNoticesApi(baseUrl: string, session: OperationsSession): RecipientNoticesApi {
  const request = createTenantRequester(baseUrl, session);
  const path = (orderId: string) => `/api/v1/orders/${encodeURIComponent(orderId)}/recipient-notices`;

  return {
    async list(orderId, signal) {
      assertUuid(orderId);
      const response = await request({ method: "GET", path: path(orderId), signal });
      if (response.status !== 200) throw new TenantApiError("invalid");
      return (await readJson(response, parseRecipientNoticeList)) as RecipientNoticeList;
    },
    async stop(orderId, idempotencyKey, signal) {
      assertUuid(orderId);
      // No body: AI-05 refuses one (409 INVALID_REQUEST).
      const response = await request({ method: "POST", path: `${path(orderId)}/stop`, idempotencyKey, signal });
      if (response.status !== 200) throw new TenantApiError("invalid");
      const result = (await readJson(response, parseRecipientNoticeStopResult)) as RecipientNoticeStopResult;
      if (result.orderId !== orderId) throw new TenantApiError("invalid");
      return result;
    },
  };
}
