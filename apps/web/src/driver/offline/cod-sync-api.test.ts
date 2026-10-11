import { describe, expect, it, vi } from "vitest";
import type { DriverSession } from "../session/driver-session";
import { createDriverOfflineOperation } from "./operation-contract";
import { createDriverSyncApi, DriverSyncApiError } from "./driver-sync-api";

/** UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: recordCodCollection from the offline queue. */
const session: DriverSession = {
  organizationId: "11111111-1111-4111-8111-111111111111",
  cacheNamespace: "opaque-synthetic-session",
  getAccessToken: () => "memory-only-token",
};
const orderId = "22222222-2222-4222-8222-222222222222";
const operationId = "33333333-3333-4333-8333-333333333333";
const reference = "ORD_abcdefghijklmnopqrstuv 2026-10-11T13:05:42-07:00";
const cod = createDriverOfflineOperation({
  partitionKey: "C".repeat(43),
  orderId,
  kind: "COD_COLLECTION",
  expectedVersion: 9,
  cod: { amountCents: 125_000, reference },
  now: () => new Date("2026-10-11T20:05:42.000Z"),
  randomUuid: () => operationId,
});

function receiptBody(overrides: Record<string, unknown> = {}) {
  return {
    id: "44444444-4444-4444-8444-444444444444",
    order_id: orderId,
    amount_cents: 125_000,
    status: "RECORDED",
    recorded_at: "2026-10-11T20:05:43+00:00",
    reconciled_at: null,
    ...overrides,
  };
}

function api(fetch: typeof globalThis.fetch) {
  return createDriverSyncApi({ baseUrl: "https://api.synthetic.test", session, fetch });
}

describe("recordCodCollection from the driver queue", () => {
  it("sends exactly the expected amount and the app's reference with the collection key", async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(json(receiptBody(), 201));

    await expect(api(fetch).recordCodCollection(cod)).resolves.toEqual({
      id: "44444444-4444-4444-8444-444444444444",
      orderId,
      amountCents: 125_000,
      status: "RECORDED",
    });

    const [url, init] = fetch.mock.calls[0] as [URL, RequestInit];
    expect(url.toString()).toBe(`https://api.synthetic.test/api/v1/orders/${orderId}/cod-records`);
    expect(init.method).toBe("POST");
    expect(JSON.parse(init.body as string)).toEqual({ amount_cents: 125_000, reference });
    const headers = new Headers(init.headers);
    expect(headers.get("Idempotency-Key")).toBe(`drv2-${operationId}-cod`);
    expect(headers.get("X-Organization-Id")).toBe(session.organizationId);
    expect(init.cache).toBe("no-store");
  });

  it("refuses anything but a cash collection operation", async () => {
    const fetch = vi.fn<typeof globalThis.fetch>();
    const transition = createDriverOfflineOperation({
      partitionKey: "C".repeat(43),
      orderId,
      kind: "START_DELIVERY",
      expectedVersion: 8,
      randomUuid: () => operationId,
    });
    await expect(api(fetch).recordCodCollection(transition)).rejects.toMatchObject({
      category: "invalid-contract",
    });
    expect(fetch).not.toHaveBeenCalled();
  });

  it.each([
    ["extra member", receiptBody({ reference })],
    ["fractional amount", receiptBody({ amount_cents: 12.5 })],
    ["unknown status", receiptBody({ status: "PAID" })],
    ["local time", receiptBody({ recorded_at: "2026-10-11T13:05:43-07:00" })],
    ["missing member", { ...receiptBody(), reconciled_at: undefined }],
  ])("fails closed on a receipt with %s", async (_name, body) => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(json(body, 201));
    await expect(api(fetch).recordCodCollection(cod)).rejects.toMatchObject({
      category: "invalid-contract",
    });
  });

  it("keeps the finance rule code of a 409 for the queue", async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(
      new Response(
        JSON.stringify({ type: "about:blank", title: "Conflict.", status: 409, code: "COD_AMOUNT_MISMATCH" }),
        { status: 409, headers: { "Content-Type": "application/problem+json" } },
      ),
    );
    const error = await api(fetch).recordCodCollection(cod).catch((value: unknown) => value);
    expect(error).toBeInstanceOf(DriverSyncApiError);
    expect(error).toMatchObject({ category: "conflict", publicCode: "COD_AMOUNT_MISMATCH" });
  });
});

function json(value: unknown, status: number): Response {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}
