import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
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
const settlementId = "5a1b2c3d-1111-4222-8333-944445555666";
const guide = "ORD_AbCdEfGhIjKlMnOpQrSt_-";

/** AI-05 TooManyRequests, as the API sends it (and ADR-OBS-001 for the dashboard). */
function tooManyRequests(retryAfter: string | null): Response {
  const headers = new Headers({ "content-type": "application/problem+json", "cache-control": "no-store" });
  if (retryAfter !== null) headers.set("Retry-After", retryAfter);
  return new Response(
    JSON.stringify({ type: "about:blank", title: "Too Many Requests", status: 429, traceId: "0".repeat(32) }),
    { status: 429, headers },
  );
}

const limitedCalls: ReadonlyArray<readonly [string, RateLimitedOperation, () => Promise<unknown>]> = [
  ["listOrders (Órdenes)", "order_list", () => createOrderListApi(base, session).list(
    { status: null, publicId: null, createdFrom: null, createdTo: null }, null)],
  ["listOrders (cobros pendientes)", "order_list", () => createCodApi(base, session).pendingReconciliation(null)],
  ["listOrders (Buscar guía, Agregar entrega)", "order_list", () =>
    createOrderActionsApi(base, session).findOrderIdByPublicId(guide)],
  ["listAssignableDrivers", "assignable_drivers", () => createAssignmentApi(base, session).listAssignableDrivers(orderId)],
  ["previewOrderCsv", "csv_import", () => createCsvImportApi(base, session).preview(new Blob(["a"]))],
  ["commitOrderCsv", "csv_import", () =>
    createCsvImportApi(base, session).commit(new Blob(["a"]), "a".repeat(64), "csv-key")],
  ["exportSettlementCsv", "settlement_export", () => createSettlementsApi(base, session).exportCsv(settlementId)],
  ["createQuote", "quote", () => createOrdersApi(base, session).createQuote({} as never, "quote-key")],
  ["operations dashboard", "dashboard", () => createOperationsApi(base, session).list({})],
  ["getOperationsQueueCounts", "queue_counts", () => createOperationsApi(base, session).queueCounts()],
];

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(new Date("2026-10-13T12:00:00Z"));
  rateLimitWaits.clear();
});
afterEach(() => {
  rateLimitWaits.clear();
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe("SEC-004: a 429 of a limited panel operation", () => {
  it.each(limitedCalls)("%s is rate_limited with its Retry-After and records the wait of %s", async (_, operation, call) => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(tooManyRequests("2")));
    const error = await call().then(
      () => null,
      (caught: unknown) => caught,
    );
    expect(error instanceof TenantApiError || error instanceof OperationsApiError).toBe(true);
    const failure = error as TenantApiError | OperationsApiError;
    expect(failure.category).toBe("rate_limited");
    expect(failure.retryAfterSeconds).toBe(2);
    expect(rateLimitWaits.secondsLeft([operation])).toBe(2);
    // Only its own limit waits.
    const others = limitedCalls.map(([, other]) => other).filter((other) => other !== operation);
    expect(rateLimitWaits.isWaiting(others)).toBe(false);
  });

  it("waits 60 seconds when Retry-After is missing or not a positive whole number", async () => {
    for (const header of [null, "0", "soon", "1.5"]) {
      rateLimitWaits.clear();
      vi.stubGlobal("fetch", vi.fn().mockResolvedValue(tooManyRequests(header)));
      const error = (await createOrderListApi(base, session)
        .list({ status: null, publicId: null, createdFrom: null, createdTo: null }, null)
        .catch((caught: unknown) => caught)) as TenantApiError;
      expect(error.category).toBe("rate_limited");
      expect(error.retryAfterSeconds).toBeNull();
      expect(rateLimitWaits.secondsLeft(["order_list"])).toBe(60);
    }
  });

  it("keeps the Idempotency-Key rule of a request without a valid answer (the handler never ran)", () => {
    expect(new TenantApiError("rate_limited", null, false, 2).retryable).toBe(true);
    expect(new TenantApiError("conflict").retryable).toBe(false);
  });

  it("records nothing for an operation outside the limited list", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(tooManyRequests("2")));
    const error = (await createOperationsApi(base, session).getOrder(orderId).catch((caught: unknown) => caught)) as OperationsApiError;
    expect(error.category).toBe("rate_limited");
    expect(rateLimitWaits.newest()).toBeNull();
  });
});
