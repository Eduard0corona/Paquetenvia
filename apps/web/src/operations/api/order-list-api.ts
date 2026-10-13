import {
  orderListSearch,
  parseOrderListPage,
  type OrderListPage,
  type OrderListQuery,
} from "../contracts/order-list";
import type { OperationsSession } from "../session/operations-session";
import { createTenantRequester, readJson, TenantApiError } from "./tenant-request";

/**
 * UI-PHASE3-ORDERS-LIST-2026-10-12: AI-05 listOrders for the "Órdenes" screen, with the guía, status and creation
 * period filters (ORD-LIST-DATE-FILTER-2026-10-12). A cursor is sent only together with the query that issued it.
 * Failures are {@link TenantApiError}s carrying only a category and the stable problem code; no identifier, typed
 * text or server message reaches an error.
 */
export interface OrderListApi {
  list(query: OrderListQuery, cursor: string | null, signal?: AbortSignal): Promise<OrderListPage>;
}

export function createOrderListApi(baseUrl: string, session: OperationsSession): OrderListApi {
  const send = createTenantRequester(baseUrl, session);
  return {
    async list(query, cursor, signal) {
      let search: URLSearchParams;
      try {
        search = orderListSearch(query, cursor);
      } catch {
        throw new TenantApiError("invalid");
      }
      const response = await send({ method: "GET", path: "/api/v1/orders", search, signal, rateLimit: "order_list" });
      if (response.status !== 200) throw new TenantApiError("invalid");
      return (await readJson(response, parseOrderListPage)) as OrderListPage;
    },
  };
}
