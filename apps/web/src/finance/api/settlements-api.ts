import {
  assertUuid,
  createTenantRequester,
  readJson,
  TenantApiError,
} from "../../operations/api/tenant-request";
import type { OperationsSession } from "../../operations/session/operations-session";
import {
  buildSettlementSearch,
  isValidReason,
  parseSettlement,
  parseSettlementPage,
  type CreateSettlementBody,
  type Settlement,
  type SettlementFilters,
  type SettlementPage,
} from "../contracts/settlement";

export interface SettlementCsv {
  readonly filename: string;
  readonly content: Blob;
}

/** The eight AI-05 settlement operations (SET-001, AI05-LIST-SETTLEMENTS). */
export interface SettlementsApi {
  list(filters: SettlementFilters, signal?: AbortSignal): Promise<SettlementPage>;
  get(settlementId: string, signal?: AbortSignal): Promise<Settlement>;
  create(body: CreateSettlementBody, idempotencyKey: string, signal?: AbortSignal): Promise<Settlement>;
  addAdjustment(
    settlementId: string,
    body: { readonly amount_cents: number; readonly reason: string },
    idempotencyKey: string,
    signal?: AbortSignal,
  ): Promise<Settlement>;
  approve(settlementId: string, idempotencyKey: string, signal?: AbortSignal): Promise<Settlement>;
  markPaid(settlementId: string, idempotencyKey: string, signal?: AbortSignal): Promise<Settlement>;
  void(
    settlementId: string,
    body: { readonly reason: string },
    idempotencyKey: string,
    signal?: AbortSignal,
  ): Promise<Settlement>;
  exportCsv(settlementId: string, signal?: AbortSignal): Promise<SettlementCsv>;
}

export function createSettlementsApi(baseUrl: string, session: OperationsSession): SettlementsApi {
  const send = createTenantRequester(baseUrl, session);
  const path = (settlementId: string, suffix = "") => {
    assertUuid(settlementId);
    return `/api/v1/settlements/${encodeURIComponent(settlementId)}${suffix}`;
  };
  const post = async (
    target: string,
    idempotencyKey: string,
    body: unknown,
    signal?: AbortSignal,
  ): Promise<Settlement> => {
    const response = await send({ method: "POST", path: target, body, idempotencyKey, signal });
    return (await readJson(response, parseSettlement)) as Settlement;
  };

  return {
    async list(filters, signal) {
      const search = buildSettlementSearch(filters);
      if (search === null) throw new TenantApiError("invalid", "INVALID_REQUEST");
      const response = await send({ method: "GET", path: "/api/v1/settlements", search, signal });
      return (await readJson(response, parseSettlementPage)) as SettlementPage;
    },
    async get(settlementId, signal) {
      const response = await send({ method: "GET", path: path(settlementId), signal });
      return (await readJson(response, parseSettlement)) as Settlement;
    },
    create: async (body, key, signal) => post("/api/v1/settlements", key, body, signal),
    async addAdjustment(settlementId, body, key, signal) {
      if (!Number.isSafeInteger(body.amount_cents) || body.amount_cents === 0 || !isValidReason(body.reason))
        throw new TenantApiError("invalid", "INVALID_REQUEST");
      return post(path(settlementId, "/adjustments"), key, body, signal);
    },
    // approve and pay carry no request body (AI-05 approveSettlement, markSettlementPaid).
    approve: async (settlementId, key, signal) => post(path(settlementId, "/approve"), key, undefined, signal),
    markPaid: async (settlementId, key, signal) => post(path(settlementId, "/pay"), key, undefined, signal),
    async void(settlementId, body, key, signal) {
      if (!isValidReason(body.reason)) throw new TenantApiError("invalid", "INVALID_REQUEST");
      return post(path(settlementId, "/void"), key, body, signal);
    },
    async exportCsv(settlementId, signal) {
      const response = await send({
        method: "GET",
        path: path(settlementId, "/export.csv"),
        accept: "text/csv",
        signal,
      });
      const contentType = response.headers.get("content-type") ?? "";
      if (!contentType.toLowerCase().startsWith("text/csv")) throw new TenantApiError("invalid");
      // Held only in memory until the browser saves it; never cached or logged.
      const content = await response.blob();
      return { filename: `settlement-${settlementId}.csv`, content };
    },
  };
}
