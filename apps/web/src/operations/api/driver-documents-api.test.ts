import { afterEach, describe, expect, it, vi } from "vitest";
import { bearerSession, jsonResponse, problem, syntheticKey, syntheticUuid } from "../contracts/ui-001-screens.fixtures";
import { createDriverDocumentsApi } from "./driver-documents-api";

const driverId = syntheticUuid(0x601);
const documentId = syntheticUuid(0x6d1);
const key = syntheticKey(11);
const base = "https://api.synthetic.test";

function answer(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: documentId,
    document_type: "VEHICLE_CARD",
    status: "VALID",
    rejection_code: null,
    expires_at: "2027-03-02T07:00:00+00:00",
    valid_through: "2027-03-01",
    expiry_basis: "PRINTED_EXPIRY",
    recorded_at: "2026-11-02T18:00:00+00:00",
    revoked_at: null,
    ...overrides,
  };
}

function lastCall(fetchMock: ReturnType<typeof vi.fn>): [URL, RequestInit] {
  return fetchMock.mock.calls.at(-1) as [URL, RequestInit];
}

afterEach(() => vi.unstubAllGlobals());

/** DRV-DOC-001-IN-PERSON-2026-11-02: AI-05 listDriverDocuments, recordDriverDocument and revokeDriverDocument. */
describe("driver documents api", () => {
  it("lists the documents page of the active organization with only a cursor and no cache", async () => {
    const fetchMock = vi.fn(async () => jsonResponse(200, { documents_enabled: false, items: [], next_cursor: null }));
    vi.stubGlobal("fetch", fetchMock);
    const api = createDriverDocumentsApi(base, bearerSession());
    expect(await api.list()).toEqual({ documents_enabled: false, items: [], next_cursor: null });
    const [url, init] = lastCall(fetchMock);
    expect(url.toString()).toBe(`${base}/api/v1/drivers/documents`);
    expect(init).toMatchObject({ method: "GET", cache: "no-store", referrerPolicy: "no-referrer" });
    expect((init.headers as Record<string, string>)["X-Organization-Id"]).toBe(bearerSession().organizationId);
    expect(init.body).toBeUndefined();
    await api.list("BAAAAAAAAAAAAAAAAAAAAAA");
    expect(lastCall(fetchMock)[0].search).toBe("?cursor=BAAAAAAAAAAAAAAAAAAAAAA");
    await expect(api.list("a&b")).rejects.toMatchObject({ category: "invalid" });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("records with the Idempotency-Key and the exact JSON body, and checks the answer", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(201, answer()));
    vi.stubGlobal("fetch", fetchMock);
    const api = createDriverDocumentsApi(base, bearerSession());
    const recorded = await api.record(
      driverId,
      { documentType: "VEHICLE_CARD", decision: "VALID", permanentLicense: false, printedExpiry: "2027-03-01" },
      key,
    );
    expect(recorded.valid_through).toBe("2027-03-01");
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe(`/api/v1/drivers/${driverId}/documents`);
    expect(url.search).toBe("");
    expect(init.method).toBe("POST");
    const headers = init.headers as Record<string, string>;
    expect(headers["Idempotency-Key"]).toBe(key);
    expect(headers["Content-Type"]).toBe("application/json");
    expect(JSON.parse(init.body as string)).toEqual({
      document_type: "VEHICLE_CARD",
      decision: "VALID",
      expiry_basis: "PRINTED_EXPIRY",
      expires_on: "2027-03-01",
    });

    // An answer for another type or decision, a 200 or a shape outside the contract is not this record.
    for (const response of [
      jsonResponse(201, answer({ document_type: "INSURANCE" })),
      jsonResponse(201, answer({ status: "REJECTED", rejection_code: "ILLEGIBLE", expires_at: null, valid_through: null, expiry_basis: null })),
      jsonResponse(200, answer()),
      jsonResponse(201, { ...answer(), object_key: "drivers/doc" }),
    ]) {
      fetchMock.mockResolvedValueOnce(response);
      await expect(
        api.record(driverId, { documentType: "VEHICLE_CARD", decision: "VALID", permanentLicense: false, printedExpiry: "2027-03-01" }, key),
      ).rejects.toMatchObject({ category: "invalid" });
    }
    const calls = fetchMock.mock.calls.length;
    await expect(
      api.record("not-a-uuid", { documentType: "VEHICLE_CARD", decision: "REJECTED", rejectionCode: "ILLEGIBLE" }, key),
    ).rejects.toMatchObject({ category: "invalid" });
    expect(fetchMock).toHaveBeenCalledTimes(calls);
  });

  it("revokes with no body or query and checks the revoked answer", async () => {
    const revoked = answer({ status: "REVOKED", revoked_at: "2026-11-02T19:00:00+00:00" });
    const fetchMock = vi.fn(async () => jsonResponse(200, revoked));
    vi.stubGlobal("fetch", fetchMock);
    const api = createDriverDocumentsApi(base, bearerSession());
    expect((await api.revoke(driverId, documentId)).status).toBe("REVOKED");
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe(`/api/v1/drivers/${driverId}/documents/${documentId}/revoke`);
    expect(url.search).toBe("");
    expect(init.method).toBe("POST");
    expect(init.body).toBeUndefined();
    expect((init.headers as Record<string, string>)["Idempotency-Key"]).toBeUndefined();
    fetchMock.mockResolvedValueOnce(jsonResponse(200, answer()));
    await expect(api.revoke(driverId, documentId)).rejects.toMatchObject({ category: "invalid" });
    fetchMock.mockResolvedValueOnce(jsonResponse(200, { ...revoked, id: syntheticUuid(0x6d2) }));
    await expect(api.revoke(driverId, documentId)).rejects.toMatchObject({ category: "invalid" });
  });

  it("reports refusals by category and stable code only", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const api = createDriverDocumentsApi(base, bearerSession());
    const input = { documentType: "IDENTITY", decision: "REJECTED", rejectionCode: "ILLEGIBLE" } as const;
    fetchMock.mockResolvedValueOnce(problem(409, "EXPIRY_DATE_OUT_OF_RANGE"));
    await expect(api.record(driverId, input, key)).rejects.toMatchObject({ category: "conflict", code: "EXPIRY_DATE_OUT_OF_RANGE" });
    fetchMock.mockResolvedValueOnce(problem(404));
    await expect(api.record(driverId, input, key)).rejects.toMatchObject({ category: "not_found" });
    fetchMock.mockResolvedValueOnce(problem(403, "MFA_REQUIRED"));
    await expect(api.revoke(driverId, documentId)).rejects.toMatchObject({ category: "forbidden", mfaRequired: true });
    fetchMock.mockResolvedValueOnce(problem(503));
    await expect(api.list()).rejects.toMatchObject({ category: "unavailable", retryable: true });
  });
});
