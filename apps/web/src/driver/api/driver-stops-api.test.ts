import { describe, expect, it, vi } from "vitest";
import type { DriverSession } from "../session/driver-session";
import {
  createDriverStopsApi,
  DriverStopsApiError,
} from "./driver-stops-api";

const session: DriverSession = {
  organizationId: "11111111-1111-1111-1111-111111111111",
  cacheNamespace: "opaque-session-0001",
  getAccessToken: () => "memory-only-token",
};
const validPayload = [
  {
    order_id: "22222222-2222-2222-2222-222222222222",
    aggregate_version: 1,
    order_public_id: "ORD_abcdefghijklmnopqrstuv",
    stop_type: "DELIVERY",
    status: "IN_TRANSIT",
    address_summary: "Las Quintas, Culiacán",
  },
];

describe("createDriverStopsApi", () => {
  it("sends only current authorization, organization and accept headers", async () => {
    const fetchMock = vi.fn(async (_input: URL | RequestInfo, init?: RequestInit) => {
      expect(init?.cache).toBe("no-store");
      expect(init?.method).toBe("GET");
      expect(init?.headers).toEqual({
        Accept: "application/json",
        Authorization: "Bearer memory-only-token",
        "X-Organization-Id": session.organizationId,
      });
      expect(JSON.stringify(init)).not.toContain("Idempotency-Key");
      return jsonResponse(validPayload);
    });
    const api = createDriverStopsApi({
      baseUrl: "https://api.synthetic.test",
      session,
      fetch: fetchMock as typeof fetch,
    });
    expect(await api.listStops()).toEqual(validPayload);
    expect(fetchMock).toHaveBeenCalledOnce();
  });

  it.each([
    [401, "unauthorized"],
    [403, "forbidden"],
    [500, "recoverable"],
    [503, "recoverable"],
    [404, "invalid-contract"],
  ] as const)("classifies HTTP %i", async (status, category) => {
    const api = createDriverStopsApi({
      baseUrl: "https://api.synthetic.test",
      session,
      fetch: (async () => new Response(null, { status })) as typeof fetch,
    });
    await expectCategory(api.listStops(), category);
  });

  it("fails closed for non-JSON content", async () => {
    const api = createDriverStopsApi({
      baseUrl: "https://api.synthetic.test",
      session,
      fetch: (async () =>
        new Response("[]", {
          status: 200,
          headers: { "Content-Type": "text/plain" },
        })) as typeof fetch,
    });
    await expectCategory(api.listStops(), "invalid-contract");
  });

  it("fails closed for invalid JSON and extra DTO properties", async () => {
    const responses = [
      new Response("{", {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
      jsonResponse([{ ...validPayload[0], telephone: "hidden" }]),
    ];
    for (const response of responses) {
      const api = createDriverStopsApi({
        baseUrl: "https://api.synthetic.test",
        session,
        fetch: (async () => response) as typeof fetch,
      });
      await expectCategory(api.listStops(), "invalid-contract");
    }
  });

  it("classifies network failure and timeout as recoverable", async () => {
    const network = createDriverStopsApi({
      baseUrl: "https://api.synthetic.test",
      session,
      fetch: (async () => {
        throw new TypeError("network");
      }) as typeof fetch,
    });
    await expectCategory(network.listStops(), "recoverable");

    const timeout = createDriverStopsApi({
      baseUrl: "https://api.synthetic.test",
      session,
      timeoutMilliseconds: 1,
      fetch: ((_input, init) =>
        new Promise((_resolve, reject) => {
          init?.signal?.addEventListener("abort", () =>
            reject(new DOMException("aborted", "AbortError")),
          );
        })) as typeof fetch,
    });
    await expectCategory(timeout.listStops(), "recoverable");
  });

  it("does not issue a request without an in-memory token", async () => {
    const fetchMock = vi.fn();
    const api = createDriverStopsApi({
      baseUrl: "https://api.synthetic.test",
      session: { ...session, getAccessToken: () => "" },
      fetch: fetchMock as typeof fetch,
    });
    await expectCategory(api.listStops(), "unauthorized");
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("classifies caller cancellation without returning partial data", async () => {
    const controller = new AbortController();
    controller.abort();
    const api = createDriverStopsApi({
      baseUrl: "https://api.synthetic.test",
      session,
      fetch: vi.fn() as typeof fetch,
    });
    await expectCategory(api.listStops(controller.signal), "cancelled");
  });
});

function jsonResponse(value: unknown): Response {
  return new Response(JSON.stringify(value), {
    status: 200,
    headers: { "Content-Type": "application/json; charset=utf-8" },
  });
}

async function expectCategory(
  promise: Promise<unknown>,
  category: DriverStopsApiError["category"],
): Promise<void> {
  try {
    await promise;
    throw new Error("Expected a DriverStopsApiError.");
  } catch (error) {
    expect(error).toBeInstanceOf(DriverStopsApiError);
    expect((error as DriverStopsApiError).category).toBe(category);
  }
}
