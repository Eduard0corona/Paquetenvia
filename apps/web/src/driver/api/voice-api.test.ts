import { describe, expect, it, vi } from "vitest";
import type { DriverSession } from "../session/driver-session";
import { createDriverVoiceApi, DriverVoiceApiError } from "./voice-api";

const session: DriverSession = {
  organizationId: "11111111-1111-1111-1111-111111111111",
  cacheNamespace: "voice-driver",
  getAccessToken: () => "memory-token",
};
const base = "https://api.synthetic.test";
const orderId = "33333333-3333-4333-8333-333333333333";
const callRequestId = "44444444-4444-4444-8444-444444444444";

function json(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ...headers } });
}

describe("VOICE-001 driver API", () => {
  it("asks for a call with one key, no body and no number, and reads only the request and its status", async () => {
    const fetcher = vi.fn(async (input: URL | RequestInfo, init?: RequestInit) => {
      expect(String(input)).toBe(`${base}/api/v1/driver/me/stops/${orderId}/recipient-call`);
      expect(init?.method).toBe("POST");
      expect(init?.body).toBeUndefined();
      expect(init?.cache).toBe("no-store");
      expect(init?.headers).toMatchObject({
        "Idempotency-Key": "voice-001-web-key-0001",
        "X-Organization-Id": session.organizationId,
        Authorization: "Bearer memory-token",
      });
      return json({ call_request_id: callRequestId, status: "PLACED" }, 202);
    });

    const result = await createDriverVoiceApi(base, session, fetcher as typeof fetch)
      .requestRecipientCall(orderId, "voice-001-web-key-0001");

    expect(result).toEqual({ call_request_id: callRequestId, status: "PLACED" });
    expect(fetcher).toHaveBeenCalledOnce();
  });

  it("registers the ten digits with the consent and never expects the number back", async () => {
    const fetcher = vi.fn(async (input: URL | RequestInfo, init?: RequestInit) => {
      expect(String(input)).toBe(`${base}/api/v1/driver/me/phone`);
      expect(init?.method).toBe("PUT");
      expect(JSON.parse(String(init?.body))).toEqual({
        phone: "5511112222",
        consent_accepted: true,
        consent_version: "VOICE-001-CONSENT-V1",
      });
      expect(init?.headers).toMatchObject({ "Content-Type": "application/json" });
      return json({
        voice_calls_enabled: true,
        registered: true,
        consent_version: "VOICE-001-CONSENT-V1",
        consented_at: "2026-10-11T16:00:00+00:00",
      });
    });

    const status = await createDriverVoiceApi(base, session, fetcher as typeof fetch).registerPhone("5511112222");

    expect(status.registered).toBe(true);
    expect(JSON.stringify(status)).not.toContain("5511112222");
  });

  it("reads and removes the phone status and reads availability with GET", async () => {
    const calls: string[] = [];
    const fetcher = vi.fn(async (input: URL | RequestInfo, init?: RequestInit) => {
      calls.push(`${init?.method} ${new URL(String(input)).pathname}`);
      if (String(input).endsWith("/recipient-call")) return json({ available: false, reason: "DRIVER_PHONE_REQUIRED" });
      return json({ voice_calls_enabled: true, registered: false, consent_version: null, consented_at: null });
    });
    const api = createDriverVoiceApi(base, session, fetcher as typeof fetch);

    await api.getPhone();
    await api.removePhone();
    expect(await api.getRecipientCallAvailability(orderId)).toEqual({ available: false, reason: "DRIVER_PHONE_REQUIRED" });
    expect(calls).toEqual([
      "GET /api/v1/driver/me/phone",
      "DELETE /api/v1/driver/me/phone",
      `GET /api/v1/driver/me/stops/${orderId}/recipient-call`,
    ]);
  });

  it.each([
    [401, "unauthorized"],
    [403, "forbidden"],
    [404, "not-found"],
    [503, "unavailable"],
    [500, "unavailable"],
    [400, "invalid-contract"],
  ] as const)("classifies %s as %s", async (status, category) => {
    const fetcher = vi.fn(async () => new Response(null, { status }));
    await expect(createDriverVoiceApi(base, session, fetcher as typeof fetch).requestRecipientCall(orderId, "voice-001-web-key-0001"))
      .rejects.toMatchObject({ category } satisfies Partial<DriverVoiceApiError>);
  });

  it("reads the closed 409 code and the Retry-After of 429", async () => {
    const conflict = vi.fn(async () => json({ status: 409, title: "Conflict.", code: "DRIVER_PHONE_REQUIRED" }, 409));
    await expect(createDriverVoiceApi(base, session, conflict as typeof fetch).requestRecipientCall(orderId, "voice-001-web-key-0001"))
      .rejects.toMatchObject({ category: "conflict", code: "DRIVER_PHONE_REQUIRED" });

    const unknown = vi.fn(async () => json({ code: "SOMETHING" }, 409));
    await expect(createDriverVoiceApi(base, session, unknown as typeof fetch).requestRecipientCall(orderId, "voice-001-web-key-0001"))
      .rejects.toMatchObject({ category: "conflict", code: null });

    const limited = vi.fn(async () => json({ status: 429 }, 429, { "Retry-After": "120" }));
    await expect(createDriverVoiceApi(base, session, limited as typeof fetch).requestRecipientCall(orderId, "voice-001-web-key-0001"))
      .rejects.toMatchObject({ category: "rate-limited", retryAfterSeconds: 120 });
  });

  it("tells a request that never got an answer apart from one the server refused", async () => {
    const failing = vi.fn(async () => {
      throw new TypeError("Failed to fetch");
    });
    await expect(createDriverVoiceApi(base, session, failing as typeof fetch).requestRecipientCall(orderId, "voice-001-web-key-0001"))
      .rejects.toMatchObject({ category: "network" });

    const controller = new AbortController();
    controller.abort();
    const aborted = vi.fn(async () => {
      throw new DOMException("aborted", "AbortError");
    });
    await expect(createDriverVoiceApi(base, session, aborted as typeof fetch)
      .requestRecipientCall(orderId, "voice-001-web-key-0001", controller.signal))
      .rejects.toMatchObject({ category: "cancelled" });
  });

  it("refuses an answer outside the contract", async () => {
    const leaking = vi.fn(async () => json({ call_request_id: callRequestId, status: "PLACED", recipient_phone: "+523312345678" }, 202));
    await expect(createDriverVoiceApi(base, session, leaking as typeof fetch).requestRecipientCall(orderId, "voice-001-web-key-0001"))
      .rejects.toMatchObject({ category: "invalid-contract" });
  });
});
