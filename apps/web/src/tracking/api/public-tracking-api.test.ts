import { afterEach, describe, expect, it, vi } from "vitest";
import {
  createPublicTrackingApi,
} from "./public-tracking-api";

const token = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA";
const projection = {
  public_id: "ORD_0123456789abcdefghijkl",
  public_status: "CREATED",
  aggregate_version: 1,
  estimated_window: null,
  timeline: [],
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("public tracking API", () => {
  it("uses an anonymous no-store request and parses the response", async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(
      new Response(JSON.stringify(projection), {
        status: 200,
        headers: { "Content-Type": "application/json; charset=utf-8" },
      }),
    );
    vi.stubGlobal("fetch", fetch);

    const result = await createPublicTrackingApi({
      baseUrl: "https://api.synthetic.local",
    }).getProjection(token);

    expect(result).toEqual(projection);
    expect(fetch).toHaveBeenCalledWith(
      new URL(`https://api.synthetic.local/api/v1/tracking/${token}`),
      expect.objectContaining({
        method: "GET",
        headers: { Accept: "application/json" },
        cache: "no-store",
        credentials: "omit",
        referrerPolicy: "no-referrer",
      }),
    );
    const request = fetch.mock.calls[0][1] as RequestInit;
    expect(request.headers).not.toHaveProperty("Authorization");
    expect(request.headers).not.toHaveProperty("X-Organization-Id");
  });

  it.each([
    [404, "not-found"],
    [429, "rate-limited"],
    [503, "unavailable"],
  ] as const)("classifies HTTP %s safely", async (status, category) => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(new Response(null, { status })),
    );
    const promise = createPublicTrackingApi({
      baseUrl: "https://api.synthetic.local",
    }).getProjection(token);
    await expect(promise).rejects.toMatchObject({ category });
  });

  it("does not accept an expanded response", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        new Response(JSON.stringify({ ...projection, order_id: "private" }), {
          headers: { "Content-Type": "application/json" },
        }),
      ),
    );
    const promise = createPublicTrackingApi({
      baseUrl: "https://api.synthetic.local",
    }).getProjection(token);
    await expect(promise).rejects.toMatchObject({
      category: "invalid-response",
    });
  });
});
