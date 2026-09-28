import {
  assertUuid,
  createTenantRequester,
  readJson,
  TenantApiError,
} from "../../operations/api/tenant-request";
import type { OperationsSession } from "../../operations/session/operations-session";
import {
  isValidCodReference,
  parseCodTransaction,
  parseOrderFinancials,
  type CodTransaction,
  type OrderFinancials,
  type RecordCodBody,
} from "../contracts/cod";

/** AI-05 getOrderFinancials, recordCodCollection and reconcileCod (FIN-001). */
export interface CodApi {
  financials(orderId: string, signal?: AbortSignal): Promise<OrderFinancials>;
  record(orderId: string, body: RecordCodBody, idempotencyKey: string, signal?: AbortSignal): Promise<CodTransaction>;
  reconcile(codId: string, idempotencyKey: string, signal?: AbortSignal): Promise<CodTransaction>;
}

export function createCodApi(baseUrl: string, session: OperationsSession): CodApi {
  const send = createTenantRequester(baseUrl, session);
  return {
    async financials(orderId, signal) {
      assertUuid(orderId);
      const response = await send({
        method: "GET",
        path: `/api/v1/orders/${encodeURIComponent(orderId)}/financials`,
        signal,
      });
      return (await readJson(response, parseOrderFinancials)) as OrderFinancials;
    },
    async record(orderId, body, idempotencyKey, signal) {
      assertUuid(orderId);
      if (!Number.isSafeInteger(body.amount_cents) || body.amount_cents < 1 || !isValidCodReference(body.reference))
        throw new TenantApiError("invalid", "INVALID_REQUEST");
      const response = await send({
        method: "POST",
        path: `/api/v1/orders/${encodeURIComponent(orderId)}/cod-records`,
        body,
        idempotencyKey,
        signal,
      });
      return (await readJson(response, parseCodTransaction)) as CodTransaction;
    },
    // reconcileCod carries no request body.
    async reconcile(codId, idempotencyKey, signal) {
      assertUuid(codId);
      const response = await send({
        method: "POST",
        path: `/api/v1/cod-records/${encodeURIComponent(codId)}/reconcile`,
        idempotencyKey,
        signal,
      });
      return (await readJson(response, parseCodTransaction)) as CodTransaction;
    },
  };
}
