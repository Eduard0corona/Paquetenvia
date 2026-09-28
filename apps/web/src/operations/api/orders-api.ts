import {
  parseCreatedOrder,
  parseQuote,
  type CreateOrderBody,
  type CreateQuoteBody,
  type CreatedOrder,
  type Quote,
} from "../contracts/create-order";
import type { OperationsSession } from "../session/operations-session";
import { createTenantRequester, readJson } from "./tenant-request";

/** AI-05 createQuote (POST /quotes) and createOrder (POST /orders). */
export interface OrdersApi {
  createQuote(body: CreateQuoteBody, idempotencyKey: string, signal?: AbortSignal): Promise<Quote>;
  createOrder(body: CreateOrderBody, idempotencyKey: string, signal?: AbortSignal): Promise<CreatedOrder>;
}

export function createOrdersApi(baseUrl: string, session: OperationsSession): OrdersApi {
  const send = createTenantRequester(baseUrl, session);
  return {
    async createQuote(body, idempotencyKey, signal) {
      const response = await send({
        method: "POST",
        path: "/api/v1/quotes",
        body,
        idempotencyKey,
        signal,
      });
      return (await readJson(response, parseQuote)) as Quote;
    },
    async createOrder(body, idempotencyKey, signal) {
      const response = await send({
        method: "POST",
        path: "/api/v1/orders",
        body,
        idempotencyKey,
        signal,
      });
      return (await readJson(response, parseCreatedOrder)) as CreatedOrder;
    },
  };
}
