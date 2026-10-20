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
const validStops = [
  {
    order_id: "22222222-2222-2222-2222-222222222222",
    aggregate_version: 1,
    order_public_id: "ORD_abcdefghijklmnopqrstuv",
    stop_type: "DELIVERY",
    status: "IN_TRANSIT",
    address_summary: "Las Quintas, Culiacán",
    details_available: true,
    contact_short_name: "M. Sintético",
    contact_full_name: null,
    zone_name: "Zona Sintética",
    service_window: null,
    cod: { expected: { currency: "MXN", amount_cents: 125_000 }, recorded: false },
    navigation: {
      latitude: 24.8,
      longitude: -107.39,
      address_text: "Calle Sintética 100",
      references: null,
    },
  },
];
const validPayload = { stops: validStops, completed_today: 2 };

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
    expect(await api.listStops()).toEqual({
      stops: validStops,
      completedToday: 2,
      droppedCount: 0,
      degradedCount: 0,
    });
    expect(fetchMock).toHaveBeenCalledOnce();
    expect(String(fetchMock.mock.calls[0][0])).toBe(
      "https://api.synthetic.test/api/v1/driver/me/stops",
    );
  });

  it("asks without the protected details only when told to (UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11)", async () => {
    const urls: string[] = [];
    const api = createDriverStopsApi({
      baseUrl: "https://api.synthetic.test",
      session,
      fetch: (async (input: URL | RequestInfo) => {
        urls.push(String(input));
        return jsonResponse({
          stops: [
            {
              ...validStops[0],
              details_available: false,
              contact_short_name: null,
              navigation: null,
            },
          ],
          completed_today: 0,
        });
      }) as typeof fetch,
    });

    const list = await api.listStops(undefined, { details: false });
    await api.listStops(undefined, { details: true });

    expect(urls).toEqual([
      "https://api.synthetic.test/api/v1/driver/me/stops?details=false",
      "https://api.synthetic.test/api/v1/driver/me/stops",
    ]);
    expect(list.stops[0]).toMatchObject({
      details_available: false,
      navigation: null,
    });
  });

  it.each([
    // DRV-SESSION-EXPIRY-OFFLINE-2026-10-19: the session ended; a missing credential stays "unauthorized".
    [401, "session-ended"],
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

  it("fails closed for invalid JSON, an extra answer member and the old bare list", async () => {
    const responses = [
      new Response("{", {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
      jsonResponse({ ...validPayload, telephone: "hidden" }),
      jsonResponse(validStops),
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
