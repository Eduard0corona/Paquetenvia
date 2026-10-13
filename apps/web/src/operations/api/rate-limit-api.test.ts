import { afterEach, describe, expect, it, vi } from "vitest";
import { createCodApi } from "../../finance/api/cod-api";
import { createSettlementsApi } from "../../finance/api/settlements-api";
import { rateLimitWaits, type RateLimitedOperation } from "../../lib/rate-limit-wait";
import type { OperationsSession } from "../session/operations-session";
import { createAssignmentApi } from "./assignment-api";
import { createCsvImportApi } from "./csv-import-api";
import { createOperationsApi, OperationsApiError } from "./operations-api";
import { createOrderActionsApi } from "./order-actions-api";
import { createOrderListApi } from "./order-list-api";
import { createOrdersApi } from "./orders-api";
import { TenantApiError } from "./tenant-request";

const base = "https://api.synthetic.test";
const session: OperationsSession = {
  organizationId: "11111111-1111-4111-8111-111111111111",
  sessionNamespace: "synthetic",
  getAccessToken: () => "ephemeral-token",
};
const orderId = "0a1b2c3d-1111-4222-8333-944445555666";
const settlementId = "6e9f2c6d-8b4a-4d1f-8b62-3a4d5c6b7e81";
const guide = "ORD_AbCdEfGhIjKlMnOpQrSt_-";

/** The API's 429 (AI-05 TooManyRequests): problem+json with Retry-After. */
function tooManyRequests(retryAfter: string | null): Response {
  const headers = new Headers({ "content-type": "application/problem+json", "cache-control": "no-store" });
  if (retryAfter !== null) headers.set("Retry-After", retryAfter);
  return new Response(
    JSON.stringify({ type: "about:blank", title: "Too Many Requests", status: 429, traceId: "0".repeat(32) }),
    { status: 429, headers },
  );
}

const calls: readonly (readonly [string, RateLimitedOperation, () => Promise<unknown>])[] = [
  ["previewOrderCsv", "csv_import", () => createCsvImportApi(base, session).preview(new Blob(["a\n"]))],
  ["commitOrderCsv", "csv_import", () => createCsvImportApi(base, session).commit(new Blob(["a\n"]), "d".repeat(64), "key-1")],
  ["exportSettlementCsv", "settlement_export", () => createSettlementsApi(base, session).exportCsv(settlementId)],
  ["createQuote", "quote", () => createOrdersApi(base, session).createQuote({} as never, "key-2")],
  ["listOrders (Órdenes)", "order_list", () => createOrderListApi(base, session).list({ status: null, publicId: null, createdFrom: null, createdTo: null }, null)],
  ["listOrders (Buscar guía)", "order_list", () => createOrderActionsApi(base, session).findOrderIdByPublicId(guide)],
  ["listOrders (cobros pendientes)", "order_list", () => createCodApi(base, session).pendingReconciliation(null)],
  ["listAssignableDrivers", "assignable_drivers", () => createAssignmentApi(base, session).listAssignableDrivers(orderId)],
  ["operations dashboard", "dashboard", () => createOperationsApi(base, session).list({})],
  ["getOperationsQueueCounts", "queue_counts", () => createOperationsApi(base, session).queueCounts()],
];

afterEach(() => {
  vi.unstubAllGlobals();
  rateLimitWaits.clear();
});

/**
 * SEC-004 (owner answer Q6, "Mensaje común con espera"): every panel call of a limited operation reads a 429 as a
 * wait of that operation, from Retry-After, so its screen shows the countdown and holds its buttons.
 */
describe("panel API clients and the SEC-004 429", () => {
  it.each(calls)("%s records the server's wait for its operation", async (_name, operation, call) => {
    vi.stubGlobal("fetch", vi.fn(async () => tooManyRequests("17")));
    const failure = await call().then(
      () => null,
      (error: unknown) => error,
    );
    expect(failure instanceof TenantApiError || failure instanceof OperationsApiError).toBe(true);
    expect((failure as TenantApiError).category).toBe("rate_limited");
    expect((failure as TenantApiError).retryAfterSeconds).toBe(17);
    expect(rateLimitWaits.secondsLeft([operation])).toBe(17);
    const others = calls.map(([, other]) => other).filter((other) => other !== operation);
    expect(rateLimitWaits.isWaiting(others)).toBe(false);
  });

  it("waits the API's window when a 429 has no usable Retry-After", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => tooManyRequests("soon")));
    await createOperationsApi(base, session).queueCounts().catch(() => undefined);
    expect(rateLimitWaits.secondsLeft(["queue_counts"])).toBe(60);
  });

  it("a 429 is worth sending again with the same key once the wait has passed", () => {
    expect(new TenantApiError("rate_limited", null, false, 5).retryable).toBe(true);
  });
});
