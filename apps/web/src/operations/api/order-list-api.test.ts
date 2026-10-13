import { afterEach, describe, expect, it, vi } from "vitest";
import type { OperationsSession } from "../session/operations-session";
import { createOrderListApi } from "./order-list-api";

const organizationId = "11111111-1111-4111-8111-111111111111";
const session: OperationsSession = {
  organizationId,
  sessionNamespace: "synthetic",
  getAccessToken: () => "ephemeral-token",
};
const guide = "ORD_AbCdEfGhIjKlMnOpQrSt_-";
const order = {
  id: "0a1b2c3d-1111-4222-8333-944445555666",
  public_id: guide,
  owner_org_id: organizationId,
  operator_org_id: null,
  status: "IN_TRANSIT",
  price_net: { currency: "MXN", amount_cents: 10_000 },
  version: 3,
  origin_location_id: "61000000-0000-4000-8000-000000000001",
  destination_location_id: "62000000-0000-4000-8000-000000000001",
  service_type: "SAME_DAY",
  quote_id: "63000000-0000-4000-8000-000000000001",
  city_id: "33333333-3333-4333-8333-333333333333",
  service_area_id: null,
  pricing_tier: "OCCASIONAL",
  total: { currency: "MXN", amount_cents: 11_600 },
  claim_window_ends_at: null,
  finalized_at: null,
  service_window: null,
};
const noFilters = { status: null, publicId: null, createdFrom: null, createdTo: null } as const;

function json(status: number, body: unknown, type = "application/json"): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": type } });
}

afterEach(() => vi.unstubAllGlobals());

/** UI-PHASE3-ORDERS-LIST-2026-10-12: AI-05 listOrders as the "Órdenes" screen calls it. */
describe("order list api", () => {
  it("lists with the tenant header, no cache and the filters, then the next page with the same filters", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(json(200, { items: [order], next_cursor: "bzI6NjM5" }))
      .mockResolvedValueOnce(json(200, { items: [], next_cursor: null }));
    vi.stubGlobal("fetch", fetchMock);
    const api = createOrderListApi("https://api.synthetic.test", session);
    const query = {
      status: "IN_TRANSIT",
      publicId: null,
      createdFrom: "2026-10-01T07:00:00.000Z",
      createdTo: "2026-10-08T06:59:59.999999Z",
    } as const;

    const page = await api.list(query, null);
    const [url, init] = fetchMock.mock.calls[0] as [URL, RequestInit];
    expect(url.origin + url.pathname).toBe("https://api.synthetic.test/api/v1/orders");
    expect(Object.fromEntries(url.searchParams)).toEqual({
      status: "IN_TRANSIT",
      created_from: "2026-10-01T07:00:00.000Z",
      created_to: "2026-10-08T06:59:59.999999Z",
    });
    expect(init).toMatchObject({ method: "GET", cache: "no-store", referrerPolicy: "no-referrer" });
    expect((init.headers as Record<string, string>)["X-Organization-Id"]).toBe(organizationId);
    expect(page).toEqual({
      items: [{ id: order.id, public_id: guide, status: "IN_TRANSIT", total_cents: 11_600, created_at: null }],
      next_cursor: "bzI6NjM5",
    });

    await api.list(query, page.next_cursor);
    const next = (fetchMock.mock.calls[1] as [URL])[0];
    expect(Object.fromEntries(next.searchParams)).toEqual({
      status: "IN_TRANSIT",
      created_from: "2026-10-01T07:00:00.000Z",
      created_to: "2026-10-08T06:59:59.999999Z",
      cursor: "bzI6NjM5",
    });
  });

  it("searches the exact guía and refuses anything else before any request", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json(200, { items: [order], next_cursor: null }));
    vi.stubGlobal("fetch", fetchMock);
    const api = createOrderListApi("https://api.synthetic.test", session);
    await api.list({ ...noFilters, publicId: guide }, null);
    expect((fetchMock.mock.calls[0] as [URL])[0].search).toBe(`?public_id=${guide}`);
    await expect(api.list({ ...noFilters, publicId: "6670000000" }, null)).rejects.toMatchObject({ category: "invalid" });
    await expect(api.list(noFilters, "a&b")).rejects.toMatchObject({ category: "invalid" });
    expect(fetchMock).toHaveBeenCalledOnce();
  });

  // ORD-ORDER-CREATED-AT-2026-10-12: the API now sends Order.created_at as System.Text.Json writes a UTC DateTimeOffset
  // read from PostgreSQL (microseconds or whole seconds, +00:00); the list keeps it exactly as sent.
  it("keeps the created_at the server sends on every Order", async () => {
    const sent = ["2026-10-12T17:04:05.123456+00:00", "2026-10-05T06:30:00+00:00"];
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        json(200, {
          items: sent.map((created_at, index) => ({ ...order, id: `0a1b2c3d-1111-4222-8333-94444555566${index}`, created_at })),
          next_cursor: null,
        }),
      ),
    );
    const page = await createOrderListApi("https://api.synthetic.test", session).list(noFilters, null);
    expect(page.items.map((item) => item.created_at)).toEqual(sent);
  });

  it("fails closed on a response outside the contract", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json(200, { items: [{ ...order, email: "x" }], next_cursor: null })));
    const api = createOrderListApi("https://api.synthetic.test", session);
    await expect(api.list(noFilters, null)).rejects.toMatchObject({ category: "invalid" });
  });

  it("reports 401, MFA_REQUIRED, the uniform 403, INVALID_REQUEST and an unavailable list by category only", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(json(401, { status: 401, title: "Unauthorized." }, "application/problem+json"))
        .mockResolvedValueOnce(json(403, { status: 403, title: "Forbidden.", code: "MFA_REQUIRED" }, "application/problem+json"))
        .mockResolvedValueOnce(json(403, { status: 403, title: "Forbidden." }, "application/problem+json"))
        .mockResolvedValueOnce(json(409, { status: 409, title: "Conflict.", code: "INVALID_REQUEST" }, "application/problem+json"))
        .mockResolvedValueOnce(json(503, { status: 503, title: "Service unavailable." }, "application/problem+json")),
    );
    const api = createOrderListApi("https://api.synthetic.test", session);
    await expect(api.list(noFilters, null)).rejects.toMatchObject({ category: "unauthorized" });
    await expect(api.list(noFilters, null)).rejects.toMatchObject({ category: "forbidden", mfaRequired: true });
    await expect(api.list(noFilters, null)).rejects.toMatchObject({ category: "forbidden", mfaRequired: false });
    await expect(api.list(noFilters, null)).rejects.toMatchObject({ category: "conflict", code: "INVALID_REQUEST" });
    await expect(api.list(noFilters, null)).rejects.toMatchObject({ category: "unavailable", retryable: true });
  });
});
