import { afterEach, describe, expect, it, vi } from "vitest";
import {
  bearerSession,
  codId,
  codTransactionResponse,
  financialsResponse,
  jsonResponse,
  orderId,
  orgA,
  pendingOrderResponse,
  problem,
  syntheticKey,
} from "../../operations/contracts/ui-001-screens.fixtures";
import { createCodApi } from "./cod-api";

const key = syntheticKey(3);

function lastCall(fetchMock: ReturnType<typeof vi.fn>): [URL, RequestInit] {
  return fetchMock.mock.calls.at(-1) as [URL, RequestInit];
}

afterEach(() => vi.unstubAllGlobals());

describe("COD api", () => {
  it("reads order financials with the tenant header and no cache", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(200, financialsResponse()));
    vi.stubGlobal("fetch", fetchMock);
    const financials = await createCodApi("https://api.synthetic.test", bearerSession()).financials(orderId);
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe(`/api/v1/orders/${orderId}/financials`);
    expect(init).toMatchObject({ method: "GET", cache: "no-store" });
    expect((init.headers as Record<string, string>)["X-Organization-Id"]).toBe(orgA);
    expect(financials.cod.expected_cents).toBe(25_050);
  });

  it("lists pending collections through listOrders with the COD filter and the cursor", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(200, { items: [pendingOrderResponse(orderId, "PQ-000123")], next_cursor: "next" }))
      .mockResolvedValueOnce(jsonResponse(200, { items: [], next_cursor: null }));
    vi.stubGlobal("fetch", fetchMock);
    const api = createCodApi("https://api.synthetic.test", bearerSession());
    const first = await api.pendingReconciliation(null);
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe("/api/v1/orders");
    expect([...url.searchParams.entries()]).toEqual([["cod_pending_reconciliation", "true"]]);
    expect(init).toMatchObject({ method: "GET", cache: "no-store" });
    expect((init.headers as Record<string, string>)["X-Organization-Id"]).toBe(orgA);
    expect(first.items.map((order) => order.id)).toEqual([orderId]);
    await api.pendingReconciliation("next");
    const [nextUrl] = lastCall(fetchMock);
    expect(nextUrl.searchParams.get("cod_pending_reconciliation")).toBe("true");
    expect(nextUrl.searchParams.get("cursor")).toBe("next");
  });

  it("keeps the 403 of a role the COD filter refuses", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(403, "MFA_REQUIRED")));
    await expect(
      createCodApi("https://api.synthetic.test", bearerSession()).pendingReconciliation(null),
    ).rejects.toMatchObject({ category: "forbidden", mfaRequired: true });
  });

  it("records with integer cents and the Idempotency-Key", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(201, codTransactionResponse()));
    vi.stubGlobal("fetch", fetchMock);
    await createCodApi("https://api.synthetic.test", bearerSession()).record(
      orderId,
      { amount_cents: 25_050, reference: "Recibo 17" },
      key,
    );
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe(`/api/v1/orders/${orderId}/cod-records`);
    expect((init.headers as Record<string, string>)["Idempotency-Key"]).toBe(key);
    expect(String(init.body)).toBe('{"amount_cents":25050,"reference":"Recibo 17"}');
  });

  it("reconciles without a body", async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      jsonResponse(200, codTransactionResponse({ status: "RECONCILED", reconciled_at: "2026-09-28T18:00:00Z" })),
    );
    vi.stubGlobal("fetch", fetchMock);
    await createCodApi("https://api.synthetic.test", bearerSession()).reconcile(codId, key);
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe(`/api/v1/cod-records/${codId}/reconcile`);
    expect(init.body).toBeUndefined();
    expect((init.headers as Record<string, string>)["Content-Type"]).toBeUndefined();
  });

  it("refuses a non-integer amount without calling the API", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    await expect(
      createCodApi("https://api.synthetic.test", bearerSession()).record(orderId, { amount_cents: 250.5, reference: "R" }, key),
    ).rejects.toMatchObject({ category: "invalid" });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("keeps the FinanceConflictProblem code", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(409, "COD_AMOUNT_MISMATCH")));
    await expect(
      createCodApi("https://api.synthetic.test", bearerSession()).record(orderId, { amount_cents: 1, reference: "R" }, key),
    ).rejects.toMatchObject({ category: "conflict", code: "COD_AMOUNT_MISMATCH" });
  });

  it("renders a missing or foreign order as the uniform not-found", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(404)));
    await expect(
      createCodApi("https://api.synthetic.test", bearerSession()).financials(orderId),
    ).rejects.toMatchObject({ category: "not_found" });
  });
});
