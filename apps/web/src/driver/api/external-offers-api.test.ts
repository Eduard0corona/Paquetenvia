import { describe, expect, it, vi } from "vitest";
import type { DriverSession } from "../session/driver-session";
import { createExternalOffersApi, ExternalOffersApiError } from "./external-offers-api";

const session: DriverSession = {
  organizationId: "11111111-1111-1111-1111-111111111111",
  cacheNamespace: "external-driver",
  getAccessToken: () => "memory-token",
};
const response = {
  items: [{
    id: "22222222-2222-2222-2222-222222222222",
    order_id: "33333333-3333-3333-3333-333333333333",
    status: "OPEN",
    commission: { currency: "MXN", amount_cents: 5000 },
    expires_at: "2026-08-28T02:00:00Z",
    accepted_by_driver_id: null,
    accepted_at: null,
    version: 1,
  }],
  next_cursor: null,
};

describe("external offers API", () => {
  it("derives the driver and uses GET without driver_id", async () => {
    const fetcher = vi.fn(async (input: URL | RequestInfo, init?: RequestInit) => {
      expect(String(input)).toBe("https://api.synthetic.test/api/v1/driver/me/external-offers");
      expect(init?.method).toBe("GET");
      return new Response(JSON.stringify(response), { status: 200 });
    });
    await createExternalOffersApi("https://api.synthetic.test", session, fetcher as typeof fetch).list();
    expect(fetcher).toHaveBeenCalledOnce();
  });

  it("uses one supplied idempotency key and classifies a lost race", async () => {
    const fetcher = vi.fn(async (_input: URL | RequestInfo, init?: RequestInit) => {
      expect(init?.headers).toMatchObject({ "Idempotency-Key": "stable-key" });
      return new Response(null, { status: 409 });
    });
    const result = createExternalOffersApi("https://api.synthetic.test", session, fetcher as typeof fetch)
      .accept("22222222-2222-2222-2222-222222222222", "stable-key");
    await expect(result).rejects.toMatchObject({ category: "conflict" } satisfies Partial<ExternalOffersApiError>);
  });
});
