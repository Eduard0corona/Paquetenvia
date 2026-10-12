import { describe, expect, it, vi } from "vitest";
import type { DriverSession } from "../session/driver-session";
import {
  DriverDispatchContactApiError,
  MyDispatchContactPath,
  createDriverDispatchContactApi,
} from "./dispatch-contact-api";

/**
 * DRV-DISPATCH-CONTACT-2026-10-12: the driver app asks GET
 * /api/v1/driver/me/dispatch-contact for the active organization with the
 * session's credentials, never caches the answer in HTTP and classifies every
 * other answer, an older API's 404 included. The number is synthetic.
 */
const syntheticNumber = "+522000000001";
const session: DriverSession = {
  organizationId: "11111111-1111-4111-8111-111111111111",
  cacheNamespace: "opaque-dispatch-api-session",
  getAccessToken: () => "synthetic-token",
};

function api(response: () => Promise<Response>, timeoutMilliseconds?: number) {
  const fetcher = vi.fn<(input: URL | RequestInfo, init?: RequestInit) => Promise<Response>>(async () => response());
  return {
    fetcher,
    api: createDriverDispatchContactApi({
      baseUrl: "https://api.synthetic.test",
      session,
      fetch: fetcher as unknown as typeof fetch,
      timeoutMilliseconds,
    }),
  };
}

const json = (status: number, body: unknown, contentType = "application/json; charset=utf-8") => () =>
  Promise.resolve(new Response(JSON.stringify(body), { status, headers: { "Content-Type": contentType } }));

describe("getMyDispatchContact client", () => {
  it("asks for the active organization with the session's credentials and no HTTP cache", async () => {
    const { api: client, fetcher } = api(json(200, { phone_e164: syntheticNumber }));

    await expect(client.getMyDispatchContact()).resolves.toBe(syntheticNumber);

    expect(fetcher).toHaveBeenCalledOnce();
    const [url, init] = fetcher.mock.calls[0];
    expect(String(url)).toBe(`https://api.synthetic.test${MyDispatchContactPath}`);
    expect(MyDispatchContactPath).toBe("/api/v1/driver/me/dispatch-contact");
    expect(init).toMatchObject({ method: "GET", cache: "no-store" });
    expect(init?.headers).toMatchObject({
      Accept: "application/json",
      Authorization: "Bearer synthetic-token",
      "X-Organization-Id": session.organizationId,
    });
    expect(init?.body).toBeUndefined();
  });

  it("reads an organization without a number as null", async () => {
    const { api: client } = api(json(200, { phone_e164: null }));
    await expect(client.getMyDispatchContact()).resolves.toBeNull();
  });

  it.each<[string, () => Promise<Response>, string]>([
    ["401", json(401, { title: "Unauthorized" }, "application/problem+json"), "unauthorized"],
    ["403", json(403, { title: "Forbidden." }, "application/problem+json"), "forbidden"],
    ["404 of an API older than this app", json(404, { title: "Not Found" }, "application/problem+json"), "not-found"],
    ["503", json(503, { title: "Service unavailable." }, "application/problem+json"), "recoverable"],
    ["a 204", () => Promise.resolve(new Response(null, { status: 204 })), "invalid-contract"],
    ["a 200 that is not JSON", () => Promise.resolve(new Response("<html>", { status: 200, headers: { "Content-Type": "text/html" } })), "invalid-contract"],
    ["a 200 with broken JSON", () => Promise.resolve(new Response("{", { status: 200, headers: { "Content-Type": "application/json" } })), "invalid-contract"],
    ["a 200 without the member", json(200, {}), "invalid-contract"],
    ["a 200 with another member", json(200, { phone_e164: syntheticNumber, name: "Despacho" }), "invalid-contract"],
    ["a 200 with a number of another form", json(200, { phone_e164: "+12025550100" }), "invalid-contract"],
    ["no answer", () => Promise.reject(new TypeError("Failed to fetch")), "recoverable"],
  ])("classifies %s", async (_name, response, category) => {
    const { api: client } = api(response);
    await expect(client.getMyDispatchContact()).rejects.toMatchObject({ category });
  });

  it("gives up after the timeout and when the caller aborts", async () => {
    // Like fetch: an aborted signal rejects at once, a later abort rejects then.
    const fetcher = vi.fn(
      (_input: URL | RequestInfo, init?: RequestInit) =>
        new Promise<Response>((_resolve, reject) => {
          const abort = () => reject(new DOMException("aborted", "AbortError"));
          if (init?.signal?.aborted) abort();
          else init?.signal?.addEventListener("abort", abort);
        }),
    );
    const timed = createDriverDispatchContactApi({
      baseUrl: "https://api.synthetic.test",
      session,
      fetch: fetcher as unknown as typeof fetch,
      timeoutMilliseconds: 5,
    });
    await expect(timed.getMyDispatchContact()).rejects.toMatchObject({ category: "recoverable" });

    const caller = new AbortController();
    const pending = timed.getMyDispatchContact(caller.signal);
    caller.abort();
    await expect(pending).rejects.toEqual(new DriverDispatchContactApiError("cancelled"));
  });

  it("sends a BFF session's cookie and no token", async () => {
    const fetcher = vi.fn<(input: URL | RequestInfo, init?: RequestInit) => Promise<Response>>(async () =>
      new Response(JSON.stringify({ phone_e164: null }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );
    const cookieSession: DriverSession = {
      organizationId: session.organizationId,
      cacheNamespace: session.cacheNamespace,
      credentialMode: "cookie",
      getCsrfToken: () => "C".repeat(43),
    };
    const client = createDriverDispatchContactApi({
      baseUrl: "https://api.synthetic.test",
      session: cookieSession,
      fetch: fetcher as unknown as typeof fetch,
    });

    await expect(client.getMyDispatchContact()).resolves.toBeNull();
    const init = fetcher.mock.calls[0][1];
    expect(init?.credentials).toBe("include");
    expect(init?.headers).not.toHaveProperty("Authorization");
  });

  it("never logs, stores or queues the number itself", async () => {
    const { readFileSync } = await import("node:fs");
    const source = readFileSync("src/driver/api/dispatch-contact-api.ts", "utf8");
    for (const forbidden of ["console.", "localStorage", "sessionStorage", "indexedDB", "enqueue("]) {
      expect(source).not.toContain(forbidden);
    }
  });
});
