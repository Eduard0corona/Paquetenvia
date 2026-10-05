import {
  normalizePublicIdInput,
  transitionRequestBody,
  type NextStepAction,
} from "../contracts/order-transitions";
import { orderStatuses } from "../contracts/operations-dashboard";
import { array, boundedString, exactObject, fail, integer, oneOf, uuid } from "../contracts/strict-json";
import type { OperationsSession } from "../session/operations-session";
import { assertUuid, createTenantRequester, readJson, TenantApiError } from "./tenant-request";

/**
 * UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05: AI-05 listOrders by exact tracking number and
 * transitionOrder. Failures are {@link TenantApiError}s with only a category and a stable
 * code; no identifier, typed text or server message reaches an error message or a log.
 */
export interface OrderActionsApi {
  /** The order id for an exact tracking number, or null when the organization has no such order. */
  findOrderIdByPublicId(publicId: string, signal?: AbortSignal): Promise<string | null>;
  transitionOrder(
    orderId: string,
    action: NextStepAction,
    reason: string,
    expectedVersion: number,
    restrictedGoodsAcknowledged: boolean,
    idempotencyKey: string,
    signal?: AbortSignal,
  ): Promise<TransitionedOrder>;
}

export interface TransitionedOrder {
  readonly id: string;
  readonly status: string;
  readonly version: number;
}

/** Order fields other than the ones read here; the response must not carry anything else. */
const otherOrderFields = [
  "owner_org_id",
  "operator_org_id",
  "price_net",
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
  "service_window",
] as const;

export function parseOrderSearchPage(value: unknown, publicId: string): string | null {
  const page = exactObject(value, ["items", "next_cursor"]);
  const items = array(page.items, 1);
  if (page.next_cursor !== null) fail();
  if (items.length === 0) return null;
  const order = exactObject(items[0], ["id", "public_id", "status", "version"], otherOrderFields);
  if (boundedString(order.public_id, 26, 26) !== publicId) fail();
  oneOf(order.status, orderStatuses);
  integer(order.version, 1);
  return uuid(order.id);
}

export function parseTransitionedOrder(value: unknown): TransitionedOrder {
  const order = exactObject(value, ["id", "public_id", "status", "version"], otherOrderFields);
  boundedString(order.public_id, 1, 128);
  return {
    id: uuid(order.id),
    status: oneOf(order.status, orderStatuses),
    version: integer(order.version, 1),
  };
}

export function createOrderActionsApi(baseUrl: string, session: OperationsSession): OrderActionsApi {
  const send = createTenantRequester(baseUrl, session);
  return {
    async findOrderIdByPublicId(publicId, signal) {
      const normalized = normalizePublicIdInput(publicId);
      // A value that cannot be a tracking number is never sent; it reads like an unknown one.
      if (normalized === null) return null;
      const response = await send({
        method: "GET",
        path: "/api/v1/orders",
        search: new URLSearchParams({ public_id: normalized }),
        signal,
      });
      if (response.status !== 200) throw new TenantApiError("invalid");
      return (await readJson(response, (body) => parseOrderSearchPage(body, normalized))) as string | null;
    },
    async transitionOrder(orderId, action, reason, expectedVersion, restrictedGoodsAcknowledged, idempotencyKey, signal) {
      assertUuid(orderId);
      let body;
      try {
        body = transitionRequestBody(action, reason, expectedVersion, restrictedGoodsAcknowledged);
      } catch {
        throw new TenantApiError("invalid");
      }
      const response = await send({
        method: "POST",
        path: `/api/v1/orders/${encodeURIComponent(orderId)}/transitions`,
        body,
        idempotencyKey,
        signal,
      });
      if (response.status !== 200) throw new TenantApiError("invalid");
      const order = (await readJson(response, parseTransitionedOrder)) as TransitionedOrder;
      if (order.id !== orderId || order.status !== action.target) throw new TenantApiError("invalid");
      return order;
    },
  };
}
