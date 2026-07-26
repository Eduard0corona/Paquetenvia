import { describe, expect, it, vi } from "vitest";
import type { DriverSession } from "../session/driver-session";
import { createDriverOfflineOperation } from "./operation-contract";
import {
  createDriverSyncApi,
  uploadDriverProof,
} from "./driver-sync-api";

const session: DriverSession = {
  organizationId: "11111111-1111-4111-8111-111111111111",
  cacheNamespace: "opaque-synthetic-session",
  getAccessToken: () => "memory-only-token",
};
const operation = createDriverOfflineOperation({
  partitionKey: "C".repeat(43),
  orderId: "22222222-2222-4222-8222-222222222222",
  kind: "DELIVERY_PROOF",
  expectedVersion: 9,
  proof: {
    contentType: "image/png",
    sizeBytes: 8,
    sha256: "a".repeat(64),
  },
  now: () => new Date("2026-07-26T12:00:00.000Z"),
  randomUuid: () => "33333333-3333-4333-8333-333333333333",
});
const hash = "a".repeat(64);

describe("driver sync API", () => {
  it("sends the exact transition body and stable key without client timestamp", async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(
      jsonResponse(
        transitionResponse(),
        200,
      ),
    );
    const api = createDriverSyncApi({
      baseUrl: "https://api.synthetic.test",
      session,
      fetch,
    });
    await expect(api.transitionOrder(operation)).resolves.toEqual({
      id: operation.orderId,
      status: "DELIVERED",
      version: 10,
    });
    const init = fetch.mock.calls[0][1] as RequestInit;
    expect(JSON.parse(init.body as string)).toEqual({
      target_status: "DELIVERED",
      reason: "DRIVER_DELIVERY_CONFIRMED",
      expected_version: 9,
      metadata: {},
    });
    expect(init.body).not.toContain(operation.clientOccurredAt);
    expect(init.credentials).toBe("omit");
    expect(new Headers(init.headers).get("Idempotency-Key")).toBe(
      `drv2-${operation.id}-transition`,
    );
  });

  it("keeps a signed URL memory-only and sends only required PUT headers", async () => {
    const grant = {
      id: "44444444-4444-4444-8444-444444444444",
      status: "CREATED",
      upload_url:
        "https://objects.synthetic.test/quarantine?X-Amz-Signature=synthetic",
      object_key: "discarded-sensitive-object-key",
      expires_at: "2026-07-26T12:05:00.000+00:00",
      required_headers: {
        "Content-Type": "image/png",
        "x-amz-meta-session-id": "synthetic",
      },
    };
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(
      jsonResponse(grant, 201),
    );
    const api = createDriverSyncApi({
      baseUrl: "https://api.synthetic.test",
      session,
      fetch,
      production: true,
    });
    const parsed = await api.createProofUploadSession(
      operation,
      { contentType: "image/png", sizeBytes: 4, sha256: hash },
    );
    expect(parsed).not.toHaveProperty("objectKey");
    expect(parsed.expiresAt).toBe("2026-07-26T12:05:00.000Z");
    const sessionRequest = fetch.mock.calls[0][1] as RequestInit;
    expect(JSON.parse(sessionRequest.body as string)).toEqual({
      proof_type: "DELIVERY_PHOTO",
      content_type: "image/png",
      size_bytes: 4,
      sha256: hash,
    });
    const sessionHeaders = new Headers(sessionRequest.headers);
    expect(sessionHeaders.get("Authorization")).toBe(
      "Bearer memory-only-token",
    );
    expect(sessionHeaders.get("X-Organization-Id")).toBe(
      session.organizationId,
    );
    expect(sessionHeaders.get("Idempotency-Key")).toBe(
      `drv2-${operation.id}-session-1`,
    );

    const put = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValue(new Response(null, { status: 200 }));
    const blob = new Blob([new Uint8Array([1, 2, 3, 4])], {
      type: "image/png",
    });
    await uploadDriverProof(parsed, blob, { fetch: put });
    const init = put.mock.calls[0][1] as RequestInit;
    expect(init.method).toBe("PUT");
    expect(init.credentials).toBe("omit");
    expect(init.cache).toBe("no-store");
    expect(init.referrerPolicy).toBe("no-referrer");
    expect(Object.fromEntries(new Headers(init.headers))).toEqual({
      "content-type": "image/png",
      "x-amz-meta-session-id": "synthetic",
    });
    expect(new Headers(init.headers).has("Authorization")).toBe(false);
    expect(new Headers(init.headers).has("X-Organization-Id")).toBe(false);
  });

  it("sends the exact proof finalization body without location or recipient data", async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(
      jsonResponse(
        {
          id: "55555555-5555-4555-8555-555555555555",
          proof_type: "DELIVERY_PHOTO",
          sha256: hash,
          captured_at: operation.clientOccurredAt,
        },
        201,
      ),
    );
    const api = createDriverSyncApi({
      baseUrl: "https://api.synthetic.test",
      session,
      fetch,
    });

    await api.finalizeProof(
      operation,
      "44444444-4444-4444-8444-444444444444",
      hash,
    );

    const init = fetch.mock.calls[0][1] as RequestInit;
    expect(JSON.parse(init.body as string)).toEqual({
      upload_session_id: "44444444-4444-4444-8444-444444444444",
      proof_type: "DELIVERY_PHOTO",
      captured_at: operation.clientOccurredAt,
      sha256: hash,
    });
    expect(init.body).not.toContain("latitude");
    expect(init.body).not.toContain("longitude");
    expect(init.body).not.toContain("recipient");
    expect(new Headers(init.headers).get("Idempotency-Key")).toBe(
      `drv2-${operation.id}-finalize-1`,
    );
  });

  it("classifies only the inherited custody waiting codes as inspectable conflicts", async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(
      new Response(JSON.stringify({ code: "PROOF_OBJECT_NOT_READY" }), {
        status: 409,
        headers: { "Content-Type": "application/problem+json" },
      }),
    );
    const api = createDriverSyncApi({
      baseUrl: "https://api.synthetic.test",
      session,
      fetch,
    });
    await expect(
      api.finalizeProof(
        operation,
        "44444444-4444-4444-8444-444444444444",
        hash,
      ),
    ).rejects.toMatchObject({
      category: "conflict",
      publicCode: "PROOF_OBJECT_NOT_READY",
    });
  });

  it("rejects drift in transition receipts and Problem Details", async () => {
    const invalidTransition = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValue(
        jsonResponse({ ...transitionResponse(), unexpected: true }, 200),
      );
    await expect(
      createDriverSyncApi({
        baseUrl: "https://api.synthetic.test",
        session,
        fetch: invalidTransition,
      }).transitionOrder(operation),
    ).rejects.toMatchObject({ category: "invalid-contract" });

    const invalidProblem = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValue(
        new Response(
          JSON.stringify({
            code: "PROOF_OBJECT_NOT_READY",
            internal_exception: "must not be inspected",
          }),
          {
            status: 409,
            headers: { "Content-Type": "application/problem+json" },
          },
        ),
      );
    await expect(
      createDriverSyncApi({
        baseUrl: "https://api.synthetic.test",
        session,
        fetch: invalidProblem,
      }).finalizeProof(
        operation,
        "44444444-4444-4444-8444-444444444444",
        hash,
      ),
    ).rejects.toMatchObject({ category: "conflict", publicCode: null });
  });

  it("rejects insecure production grants and invalid response content types", async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(
      jsonResponse(
        {
          id: "44444444-4444-4444-8444-444444444444",
          status: "CREATED",
          upload_url: "http://objects.synthetic.test/grant",
          object_key: "sensitive",
          expires_at: "2026-07-26T12:05:00.000Z",
          required_headers: { "Content-Type": "image/png" },
        },
        201,
      ),
    );
    const api = createDriverSyncApi({
      baseUrl: "https://api.synthetic.test",
      session,
      fetch,
      production: true,
    });
    await expect(
      api.createProofUploadSession(operation, {
        contentType: "image/png",
        sizeBytes: 4,
        sha256: hash,
      }),
    ).rejects.toMatchObject({ category: "invalid-contract" });
  });

  it.each([
    "https://user:secret@objects.synthetic.test/grant",
    "https://objects.synthetic.test/grant#fragment",
    "ftp://objects.synthetic.test/grant",
  ])("rejects an unsafe signed URL: %s", async (uploadUrl) => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(
      jsonResponse(
        {
          id: "44444444-4444-4444-8444-444444444444",
          status: "CREATED",
          upload_url: uploadUrl,
          object_key: "sensitive",
          expires_at: "2026-07-26T12:05:00.000Z",
          required_headers: { "Content-Type": "image/png" },
        },
        201,
      ),
    );
    const api = createDriverSyncApi({
      baseUrl: "https://api.synthetic.test",
      session,
      fetch,
    });

    await expect(
      api.createProofUploadSession(operation, {
        contentType: "image/png",
        sizeBytes: 4,
        sha256: hash,
      }),
    ).rejects.toMatchObject({ category: "invalid-contract" });
  });

  it("classifies an expired signed upload grant without creating a new session", async () => {
    await expect(
      uploadDriverProof(
        {
          id: "33333333-3333-4333-8333-333333333333",
          status: "CREATED",
          uploadUrl: "https://storage.example/proof?signature=opaque",
          expiresAt: "2026-07-26T12:30:00.000Z",
          requiredHeaders: { "Content-Type": "image/png" },
        },
        new Blob([new Uint8Array([1])], { type: "image/png" }),
        {
          fetch: vi.fn().mockResolvedValue(new Response(null, { status: 403 })),
        },
      ),
    ).rejects.toMatchObject({ category: "session-expired" });
  });
});

function jsonResponse(value: unknown, status: number): Response {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function transitionResponse(): Record<string, unknown> {
  return {
    id: operation.orderId,
    public_id: "ORD_SYNTHETIC",
    owner_org_id: session.organizationId,
    operator_org_id: session.organizationId,
    status: "DELIVERED",
    price_net: { currency: "MXN", amount_cents: 10_000 },
    version: 10,
    origin_location_id: "66666666-6666-4666-8666-666666666666",
    destination_location_id: "77777777-7777-4777-8777-777777777777",
    service_type: "LOCAL",
    quote_id: "88888888-8888-4888-8888-888888888888",
    city_id: "99999999-9999-4999-8999-999999999999",
    service_area_id: null,
    pricing_tier: "STANDARD",
    total: { currency: "MXN", amount_cents: 11_600 },
    claim_window_ends_at: null,
    finalized_at: "2026-07-26T12:00:00.000Z",
  };
}
