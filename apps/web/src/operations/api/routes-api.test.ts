import { afterEach, describe, expect, it, vi } from "vitest";
import { createRoutesApi, RoutesApiError } from "./routes-api";

const session = {
  organizationId: "11111111-1111-1111-1111-111111111111",
  sessionNamespace: "test",
  getAccessToken: () => "token",
};
const detail = {
  id: "22222222-2222-2222-2222-222222222222", status: "DRAFT", version: 2,
  driver_id: "33333333-3333-3333-3333-333333333333",
  city_id: "44444444-4444-4444-4444-444444444444", service_area_id: null,
  scheduled_for: null, assignment_cost_cents_total: 4500, stop_count: 0, stops: [],
};

afterEach(() => vi.unstubAllGlobals());

describe("routes api", () => {
  it("sends authoritative reorder with expected version and tenant headers", async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify(detail), {
      status: 200, headers: { "content-type": "application/json" },
    }));
    vi.stubGlobal("fetch", fetch);
    await createRoutesApi("https://api.example.test", session).reorder(
      detail.id, [], 2, "idem-1");
    const [url, init] = fetch.mock.calls[0] as [URL, RequestInit];
    expect(url.pathname).toBe(`/api/v1/routes/${detail.id}/stops/order`);
    expect(init.method).toBe("PUT");
    expect(init.headers).toMatchObject({
      "X-Organization-Id": session.organizationId,
      "Idempotency-Key": "idem-1",
    });
    expect(JSON.parse(String(init.body))).toEqual({ expected_version: 2, stop_ids: [] });
  });

  it("classifies stale version as conflict", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(null, { status: 409 })));
    await expect(createRoutesApi("https://api.example.test", session).removeStop(
      detail.id, "55555555-5555-5555-5555-555555555555", 2, "idem-2"))
      .rejects.toEqual(expect.objectContaining<Partial<RoutesApiError>>({ category: "conflict" }));
  });
});
