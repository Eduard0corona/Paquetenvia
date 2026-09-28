import { afterEach, describe, expect, it, vi } from "vitest";
import type { OpenIncidentBody } from "../contracts/incident";
import {
  bearerSession,
  incidentId,
  incidentResponse,
  jsonResponse,
  orderId,
  problem,
  proofId,
  syntheticKey,
} from "../contracts/ui-001-screens.fixtures";
import { createIncidentsApi } from "./incidents-api";

const key = syntheticKey(2);
const body: OpenIncidentBody = {
  type: "FAILED_ATTEMPT",
  severity: "HIGH",
  description: "Sin acceso al fraccionamiento.",
  reason_code: "ACCESS_RESTRICTED",
  next_action: "RETURNING",
  occurred_at: "2026-09-28T16:00:00.000Z",
  evidence_proof_ids: [proofId],
};

function lastCall(fetchMock: ReturnType<typeof vi.fn>): [URL, RequestInit] {
  return fetchMock.mock.calls.at(-1) as [URL, RequestInit];
}

afterEach(() => vi.unstubAllGlobals());

describe("incidents api", () => {
  it("opens with the Idempotency-Key and the exact JSON body", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(201, incidentResponse()));
    vi.stubGlobal("fetch", fetchMock);
    const incident = await createIncidentsApi("https://api.synthetic.test", bearerSession()).open(orderId, body, key);
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe(`/api/v1/orders/${orderId}/incidents`);
    const headers = init.headers as Record<string, string>;
    expect(headers["Idempotency-Key"]).toBe(key);
    expect(headers["Content-Type"]).toBe("application/json");
    expect(JSON.parse(String(init.body))).toEqual(body);
    expect(incident.id).toBe(incidentId);
  });

  it("resolves through the resolution path", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(200, incidentResponse({ status: "RESOLVED" })));
    vi.stubGlobal("fetch", fetchMock);
    await createIncidentsApi("https://api.synthetic.test", bearerSession()).resolve(
      incidentId,
      { outcome: "RESOLVED", reason: "Cliente localizado" },
      key,
    );
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe(`/api/v1/incidents/${incidentId}/resolution`);
    expect(JSON.parse(String(init.body))).toEqual({ outcome: "RESOLVED", reason: "Cliente localizado" });
  });

  it("refuses a non-UUID path parameter without calling the API", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    await expect(
      createIncidentsApi("https://api.synthetic.test", bearerSession()).open("../orders", body, key),
    ).rejects.toMatchObject({ category: "invalid" });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("keeps the IncidentConflictProblem code", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(409, "OFFLINE_OPERATION_EXPIRED")));
    await expect(
      createIncidentsApi("https://api.synthetic.test", bearerSession()).open(orderId, body, key),
    ).rejects.toMatchObject({ category: "conflict", code: "OFFLINE_OPERATION_EXPIRED" });
  });

  it("reports MFA_REQUIRED as a step-up", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(403, "MFA_REQUIRED")));
    await expect(
      createIncidentsApi("https://api.synthetic.test", bearerSession()).resolve(
        incidentId,
        { outcome: "REJECTED", reason: "Duplicada" },
        key,
      ),
    ).rejects.toMatchObject({ category: "forbidden", mfaRequired: true });
  });
});
