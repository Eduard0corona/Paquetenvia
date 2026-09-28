import { afterEach, describe, expect, it, vi } from "vitest";
import { TenantApiError } from "../../operations/api/tenant-request";
import type { OperationsSession } from "../../operations/session/operations-session";
import { settlementId, settlementResponse } from "../contracts/settlement.fixtures";
import { createSettlementsApi } from "./settlements-api";

const organizationId = "11111111-1111-4111-8111-111111111111";
const bearer: OperationsSession = {
  organizationId,
  sessionNamespace: "synthetic",
  getAccessToken: () => "ephemeral-token",
};
const cookie: OperationsSession = {
  organizationId,
  sessionNamespace: "synthetic",
  credentialMode: "cookie",
  getCsrfToken: () => "c".repeat(43),
};
const key = "01234567-89ab-4cde-8f01-23456789abcd";

function json(status: number, body: unknown, type = "application/json"): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": type } });
}

function lastCall(fetchMock: ReturnType<typeof vi.fn>): [URL, RequestInit] {
  return fetchMock.mock.calls.at(-1) as [URL, RequestInit];
}

afterEach(() => vi.unstubAllGlobals());

describe("settlements api", () => {
  it("lists with tenant header, no cache and no credentials in bearer mode", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json(200, { items: [settlementResponse()], next_cursor: null }));
    vi.stubGlobal("fetch", fetchMock);
    const page = await createSettlementsApi("https://api.synthetic.test", bearer).list({ status: "CALCULATED" });
    const [url, init] = lastCall(fetchMock);
    expect(url.toString()).toBe("https://api.synthetic.test/api/v1/settlements?status=CALCULATED");
    expect(init).toMatchObject({ method: "GET", cache: "no-store", credentials: "omit", referrerPolicy: "no-referrer" });
    expect((init.headers as Record<string, string>)["X-Organization-Id"]).toBe(organizationId);
    expect(page.items[0].total_cents).toBe(4_000);
  });

  it("sends the Idempotency-Key and CSRF token on writes in cookie mode", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json(201, settlementResponse()));
    vi.stubGlobal("fetch", fetchMock);
    await createSettlementsApi("https://app.synthetic.test", cookie).addAdjustment(
      settlementId,
      { amount_cents: -500, reason: "Descuento acordado" },
      key,
    );
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe(`/api/v1/settlements/${settlementId}/adjustments`);
    const headers = init.headers as Record<string, string>;
    expect(headers["Idempotency-Key"]).toBe(key);
    expect(headers["X-AuthCenter-CSRF"]).toBe("c".repeat(43));
    expect(headers.Authorization).toBeUndefined();
    expect(init.credentials).toBe("include");
    expect(JSON.parse(String(init.body))).toEqual({ amount_cents: -500, reason: "Descuento acordado" });
  });

  it.each([
    ["approve", `/api/v1/settlements/${settlementId}/approve`],
    ["markPaid", `/api/v1/settlements/${settlementId}/pay`],
  ] as const)("%s posts without a body", async (method, path) => {
    const fetchMock = vi.fn().mockResolvedValue(json(200, settlementResponse({ status: "APPROVED" })));
    vi.stubGlobal("fetch", fetchMock);
    await createSettlementsApi("https://api.synthetic.test", bearer)[method](settlementId, key);
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe(path);
    expect(init.body).toBeUndefined();
    expect((init.headers as Record<string, string>)["Content-Type"]).toBeUndefined();
  });

  it("reports MFA_REQUIRED as a step-up, not a generic denial", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        json(403, { type: "about:blank", title: "Forbidden", status: 403, code: "MFA_REQUIRED" }, "application/problem+json"),
      ),
    );
    const error = await createSettlementsApi("https://api.synthetic.test", bearer)
      .approve(settlementId, key)
      .catch((caught: unknown) => caught);
    expect(error).toBeInstanceOf(TenantApiError);
    expect(error).toMatchObject({ category: "forbidden", mfaRequired: true });
  });

  it("keeps a generic 403 generic", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(json(403, { type: "about:blank", title: "Forbidden", status: 403 }, "application/problem+json")),
    );
    const error = await createSettlementsApi("https://api.synthetic.test", bearer)
      .approve(settlementId, key)
      .catch((caught: unknown) => caught);
    expect(error).toMatchObject({ category: "forbidden", mfaRequired: false, code: null });
  });

  it("surfaces the stable settlement conflict code", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        json(409, { type: "about:blank", title: "Conflict", status: 409, code: "CASH_PENDING" }, "application/problem+json"),
      ),
    );
    const error = await createSettlementsApi("https://api.synthetic.test", bearer)
      .approve(settlementId, key)
      .catch((caught: unknown) => caught);
    expect(error).toMatchObject({ category: "conflict", code: "CASH_PENDING", retryable: false });
  });

  it("fails closed when a response breaks the contract", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json(200, settlementResponse({ total_cents: 1 }))));
    await expect(createSettlementsApi("https://api.synthetic.test", bearer).get(settlementId)).rejects.toMatchObject({
      category: "invalid",
    });
  });

  it("classifies transport failures as retryable", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new TypeError("offline")));
    await expect(
      createSettlementsApi("https://api.synthetic.test", bearer).markPaid(settlementId, key),
    ).rejects.toMatchObject({ category: "network", retryable: true });
  });

  it("refuses invalid input before any request", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const api = createSettlementsApi("https://api.synthetic.test", bearer);
    await expect(api.get("../other")).rejects.toMatchObject({ category: "invalid" });
    await expect(api.addAdjustment(settlementId, { amount_cents: 0, reason: "x" }, key)).rejects.toMatchObject({ category: "invalid" });
    await expect(api.addAdjustment(settlementId, { amount_cents: 1.5, reason: "x" }, key)).rejects.toMatchObject({ category: "invalid" });
    await expect(api.void(settlementId, { reason: " padded " }, key)).rejects.toMatchObject({ category: "invalid" });
    await expect(api.list({ periodFrom: "2026-09-02", periodTo: "2026-09-01" })).rejects.toMatchObject({ category: "invalid" });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("exports the persisted CSV as an in-memory attachment", async () => {
    const csv = "settlement_id,payee_type\r\n";
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(csv, { status: 200, headers: { "content-type": "text/csv; charset=utf-8", "cache-control": "no-store" } }),
    );
    vi.stubGlobal("fetch", fetchMock);
    const result = await createSettlementsApi("https://api.synthetic.test", bearer).exportCsv(settlementId);
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe(`/api/v1/settlements/${settlementId}/export.csv`);
    expect((init.headers as Record<string, string>).Accept).toBe("text/csv");
    expect(init.cache).toBe("no-store");
    expect(result.filename).toBe(`settlement-${settlementId}.csv`);
    expect(await result.content.text()).toBe(csv);
  });
});
