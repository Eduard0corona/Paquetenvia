import { describe, expect, it, vi } from "vitest";
import type { DriverSession } from "../session/driver-session";
import { createDriverSyncApi } from "./driver-sync-api";
import {
  failedDeliveryOperation,
  orderId,
  photoSha256,
  reportId,
  reportedAt,
  uuid,
} from "./failed-delivery.fixtures";

/**
 * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: the exact requests of a
 * failed-delivery report (AI-05 openIncident for a DRIVER and transitionOrder
 * to FAILED_ATTEMPT) and the strict reading of their answers.
 */
const session: DriverSession = {
  organizationId: "11111111-1111-4111-8111-111111111111",
  cacheNamespace: "opaque-synthetic-session",
  getAccessToken: () => "memory-only-token",
};
const proofId = uuid(0x600);
const incidentId = uuid(0x700);
const withProof = failedDeliveryOperation({ proofId }, "SECURITY_RISK");
const withIncident = failedDeliveryOperation({
  proofId,
  failedDelivery: {
    reasonCode: "SECURITY_RISK",
    incidentIdempotencyKey: `drv2-${reportId}-incident`,
    incidentId,
  },
});

function api(fetch: typeof globalThis.fetch) {
  return createDriverSyncApi({ baseUrl: "https://api.synthetic.test", session, fetch });
}

function incidentBody(patch: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: incidentId,
    order_id: orderId,
    status: "OPEN",
    severity: "HIGH",
    reason_code: "SECURITY_RISK",
    next_action: null,
    custody_acquired: true,
    occurred_at: "2026-10-12T18:30:00.000+00:00",
    sla_due_at: "2026-10-13T02:30:00.000+00:00",
    evidence_proof_ids: [proofId],
    ...patch,
  };
}

describe("openIncident for a driver report", () => {
  it("sends only type, reason, capture instant and the photo, with the report's own key", async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(jsonResponse(incidentBody(), 201));

    await expect(api(fetch).openFailedDeliveryIncident(withProof)).resolves.toEqual({
      id: incidentId,
      orderId,
      reasonCode: "SECURITY_RISK",
      nextAction: null,
      occurredAt: reportedAt.toISOString(),
      evidenceProofIds: [proofId],
    });
    const [url, init] = fetch.mock.calls[0];
    expect(String(url)).toBe(`https://api.synthetic.test/api/v1/orders/${orderId}/incidents`);
    expect(init?.method).toBe("POST");
    expect(JSON.parse(init?.body as string)).toEqual({
      type: "FAILED_DELIVERY_ATTEMPT",
      reason_code: "SECURITY_RISK",
      occurred_at: reportedAt.toISOString(),
      evidence_proof_ids: [proofId],
    });
    const body = init?.body as string;
    for (const forbidden of ["severity", "description", "next_action"]) {
      expect(body).not.toContain(forbidden);
    }
    const headers = new Headers(init?.headers);
    expect(headers.get("Idempotency-Key")).toBe(`drv2-${reportId}-incident`);
    expect(headers.get("X-Organization-Id")).toBe(session.organizationId);
    expect(init?.credentials).toBe("omit");
  });

  it("never opens an incident without the finalized photo or for another action", async () => {
    const fetch = vi.fn<typeof globalThis.fetch>();
    await expect(api(fetch).openFailedDeliveryIncident(failedDeliveryOperation())).rejects.toMatchObject({
      category: "invalid-contract",
    });
    await expect(
      api(fetch).openFailedDeliveryIncident({ ...withProof, kind: "DELIVERY_PROOF", failedDelivery: null }),
    ).rejects.toMatchObject({ category: "invalid-contract" });
    expect(fetch).not.toHaveBeenCalled();
  });

  it.each<[string, Record<string, unknown>]>([
    ["an extra member", { description: "Intento fallido" }],
    ["a reason outside the list", { reason_code: "OTHER" }],
    ["an unknown severity", { severity: "URGENT" }],
    ["an unknown status", { status: "CLOSED" }],
    ["an unknown next action", { next_action: "DELIVERING" }],
    ["a non-canonical id", { id: "incident-1" }],
    ["an offset occurred_at", { occurred_at: "2026-10-12T11:30:00.000-07:00" }],
    ["no evidence", { evidence_proof_ids: [] }],
    ["repeated evidence", { evidence_proof_ids: [proofId, proofId] }],
    ["a non-boolean custody", { custody_acquired: "yes" }],
  ])("refuses an answer with %s", async (_name, patch) => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(jsonResponse(incidentBody(patch), 201));
    await expect(api(fetch).openFailedDeliveryIncident(withProof)).rejects.toMatchObject({
      category: "invalid-contract",
    });
  });

  it("refuses an answer without a member", async () => {
    const body = incidentBody();
    delete body.sla_due_at;
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(jsonResponse(body, 201));
    await expect(api(fetch).openFailedDeliveryIncident(withProof)).rejects.toMatchObject({
      category: "invalid-contract",
    });
  });

  it.each([
    [409, "INVALID_REQUEST", "conflict"],
    [409, "EVIDENCE_NOT_AVAILABLE", "conflict"],
    [409, "OFFLINE_OPERATION_EXPIRED", "conflict"],
    [401, null, "unauthorized"],
    [403, null, "forbidden"],
    [404, null, "not-found"],
    [503, null, "recoverable"],
  ] as const)("classifies %i %s", async (status, code, category) => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(
      new Response(JSON.stringify({ type: "about:blank", title: "Conflict", status, code }), {
        status,
        headers: { "Content-Type": "application/problem+json" },
      }),
    );
    await expect(api(fetch).openFailedDeliveryIncident(withProof)).rejects.toMatchObject({
      category,
      publicCode: status === 409 ? code : null,
    });
  });
});

describe("the FAILED_ATTEMPT transition of a report", () => {
  it("names the incident, the fixed reason and the version the report was built on", async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(
      jsonResponse(orderResponse("FAILED_ATTEMPT", 9), 200),
    );

    await expect(api(fetch).transitionOrder(withIncident)).resolves.toEqual({
      id: orderId,
      status: "FAILED_ATTEMPT",
      version: 9,
    });
    const init = fetch.mock.calls[0][1];
    expect(JSON.parse(init?.body as string)).toEqual({
      target_status: "FAILED_ATTEMPT",
      reason: "DRIVER_DELIVERY_FAILED_REPORTED",
      expected_version: 8,
      metadata: { incident_id: incidentId },
      client_occurred_at: reportedAt.toISOString(),
    });
    expect(new Headers(init?.headers).get("Idempotency-Key")).toBe(`drv2-${reportId}-transition`);
  });

  it("never sends the transition before the incident exists", async () => {
    const fetch = vi.fn<typeof globalThis.fetch>();
    await expect(api(fetch).transitionOrder(withProof)).rejects.toMatchObject({
      category: "invalid-contract",
    });
    expect(fetch).not.toHaveBeenCalled();
  });

  it("uploads the place photo as a DELIVERY_PHOTO with the report's session key", async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(
      jsonResponse(
        {
          id: uuid(0x500),
          status: "CREATED",
          upload_url: "https://objects.synthetic.test/quarantine",
          object_key: "never-kept",
          expires_at: "2026-10-12T19:00:00.000+00:00",
          required_headers: { "Content-Type": "image/png" },
        },
        201,
      ),
    );
    await api(fetch).createProofUploadSession(failedDeliveryOperation(), {
      contentType: "image/png",
      sizeBytes: 3,
      sha256: photoSha256,
    });
    const init = fetch.mock.calls[0][1];
    expect(JSON.parse(init?.body as string)).toMatchObject({ proof_type: "DELIVERY_PHOTO" });
    expect(new Headers(init?.headers).get("Idempotency-Key")).toBe(`drv2-${reportId}-session-1`);
  });
});

function jsonResponse(value: unknown, status: number): Response {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function orderResponse(status: string, version: number): Record<string, unknown> {
  return {
    id: orderId,
    public_id: "ORD_SYNTHETIC",
    owner_org_id: session.organizationId,
    operator_org_id: session.organizationId,
    status,
    price_net: { currency: "MXN", amount_cents: 10_000 },
    version,
    origin_location_id: "66666666-6666-4666-8666-666666666666",
    destination_location_id: "77777777-7777-4777-8777-777777777777",
    service_type: "LOCAL",
    quote_id: "88888888-8888-4888-8888-888888888888",
    city_id: "99999999-9999-4999-8999-999999999999",
    service_area_id: null,
    pricing_tier: "STANDARD",
    total: { currency: "MXN", amount_cents: 11_600 },
    claim_window_ends_at: null,
    finalized_at: null,
    service_window: null,
  };
}
