import type { OrderActionsApi } from "../api/order-actions-api";
import { normalizePublicIdInput } from "../contracts/order-transitions";
import type { RateLimitedOperation } from "../../lib/rate-limit-wait";
import { isRateLimited } from "./tenant-error-messages";

/**
 * SEC-004, owner answer Q13 "Mensaje común": "Buscar guía" is listOrders (120 per minute per user, shared with
 * "Órdenes", the pending COD list and "Agregar entrega"). During the server's wait "Buscar" stays disabled, the typed
 * guía stays, and nothing is sent again by itself.
 */
export const orderSearchRateLimit: readonly RateLimitedOperation[] = ["order_list"];

/**
 * UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05: "Buscar guía" in the app shell. Only the exact
 * tracking number is searched (listOrders public_id); a malformed, missing or
 * other-organization number all end as `not_found`, shown with the same message. The typed
 * text is never stored, logged or put in an error.
 */
export type OrderSearchOutcome =
  | { readonly kind: "found"; readonly orderId: string }
  | { readonly kind: "not_found" }
  | { readonly kind: "failed" }
  /** SEC-004: a 429; the common message counts down next to the search. */
  | { readonly kind: "rate_limited" };

export async function searchOrderByPublicId(
  api: Pick<OrderActionsApi, "findOrderIdByPublicId">,
  typed: string,
  signal?: AbortSignal,
): Promise<OrderSearchOutcome> {
  const publicId = normalizePublicIdInput(typed);
  if (publicId === null) return { kind: "not_found" };
  try {
    const orderId = await api.findOrderIdByPublicId(publicId, signal);
    return orderId === null ? { kind: "not_found" } : { kind: "found", orderId };
  } catch (error: unknown) {
    if (signal?.aborted) throw error;
    return isRateLimited(error) ? { kind: "rate_limited" } : { kind: "failed" };
  }
}

export const orderSearchFailedMessage = "No pudimos buscar la guía. Intenta de nuevo.";

/** The order detail route for a found order. */
export function orderDetailPath(orderId: string): string {
  return `/ops/orders/${encodeURIComponent(orderId)}`;
}
