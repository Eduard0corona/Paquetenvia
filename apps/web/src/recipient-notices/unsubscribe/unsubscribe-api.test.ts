import { afterEach, describe, expect, it, vi } from "vitest";
import { createRecipientUnsubscribeApi } from "./unsubscribe-api";

const token = "B".repeat(87) + "_";

function respond(status: number, headers: Record<string, string> = {}) {
  return vi.fn<(input: URL | RequestInfo, init?: RequestInit) => Promise<Response>>(
    async () => new Response(null, { status, headers }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the unsubscribe client (AI-05 unsubscribeRecipientNotices)", () => {
  it("sends one POST with {} and the token, without credentials, referrer, cache or redirects", async () => {
    const fetch = respond(204);
    vi.stubGlobal("fetch", fetch);
    const api = createRecipientUnsubscribeApi({ baseUrl: "https://web.example.test/avisos/baja/x?y=1" });

    await expect(api.unsubscribe(token)).resolves.toEqual({ kind: "recorded" });

    expect(fetch).toHaveBeenCalledTimes(1);
    const [url, init] = fetch.mock.calls[0];
    expect(String(url)).toBe(`https://web.example.test/api/v1/recipient-notices/unsubscribe/${token}`);
    expect(init?.method).toBe("POST");
    expect(init?.body).toBe("{}");
    expect(init?.headers).toEqual({ Accept: "application/problem+json", "Content-Type": "application/json" });
    expect(init?.credentials).toBe("omit");
    expect(init?.cache).toBe("no-store");
    expect(init?.redirect).toBe("error");
    expect(init?.referrerPolicy).toBe("no-referrer");
  });

  it("maps 404 to the invalid link, 429 to its wait and everything else to the failure", async () => {
    const api = createRecipientUnsubscribeApi({ baseUrl: "https://web.example.test" });
    const cases: Array<[ReturnType<typeof respond>, unknown]> = [
      [respond(404), { kind: "invalid" }],
      [respond(429, { "Retry-After": "42" }), { kind: "wait", seconds: 42 }],
      [respond(429), { kind: "wait", seconds: 60 }],
      [respond(429, { "Retry-After": "0" }), { kind: "wait", seconds: 60 }],
      [respond(429, { "Retry-After": "99999" }), { kind: "wait", seconds: 3600 }],
      [respond(503), { kind: "failed" }],
      [respond(400), { kind: "failed" }],
      [respond(413), { kind: "failed" }],
      [respond(200), { kind: "failed" }],
      [respond(302, { Location: "https://elsewhere.example.test" }), { kind: "failed" }],
    ];
    for (const [fetch, expected] of cases) {
      vi.stubGlobal("fetch", fetch);
      await expect(api.unsubscribe(token)).resolves.toEqual(expected);
    }
  });

  it("fails on a network error and sends nothing for a malformed token", async () => {
    const api = createRecipientUnsubscribeApi({ baseUrl: "https://web.example.test" });
    vi.stubGlobal("fetch", vi.fn(async () => Promise.reject(new TypeError("network"))));
    await expect(api.unsubscribe(token)).resolves.toEqual({ kind: "failed" });

    const fetch = respond(204);
    vi.stubGlobal("fetch", fetch);
    for (const malformed of ["", token.slice(1), `${token}A`, `${token.slice(1)}=`, `${token.slice(2)}/x`]) {
      await expect(api.unsubscribe(malformed)).resolves.toEqual({ kind: "invalid" });
    }
    expect(fetch).not.toHaveBeenCalled();
  });

  it("refuses an unsupported base address or timeout", () => {
    expect(() => createRecipientUnsubscribeApi({ baseUrl: "ftp://web.example.test" })).toThrow();
    expect(() => createRecipientUnsubscribeApi({ baseUrl: "https://web.example.test", timeoutMilliseconds: 10 })).toThrow();
  });
});
