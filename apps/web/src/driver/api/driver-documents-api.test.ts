import { describe, expect, it, vi } from "vitest";
import type { DriverSession } from "../session/driver-session";
import { createDriverDocumentsApi, DriverDocumentsApiError, MyDriverDocumentsPath } from "./driver-documents-api";

const session: DriverSession = {
  organizationId: "11111111-1111-1111-1111-111111111111",
  cacheNamespace: "documents-driver",
  getAccessToken: () => "memory-token",
};
const base = "https://api.synthetic.test";

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

/** DRV-DOC-001-IN-PERSON-2026-11-02: AI-05 listMyDriverDocuments as "Cuenta > Documentos" calls it. */
describe("driver documents api", () => {
  it("reads the driver's own documents with a GET, the organization, no body and no cache", async () => {
    const fetcher = vi.fn(async (input: URL | RequestInfo, init?: RequestInit) => {
      expect(String(input)).toBe(`${base}/api/v1/driver/me/documents`);
      expect(init?.method).toBe("GET");
      expect(init?.body).toBeUndefined();
      expect(init?.cache).toBe("no-store");
      expect(init?.headers).toMatchObject({
        "X-Organization-Id": session.organizationId,
        Authorization: "Bearer memory-token",
      });
      return json({ documents_enabled: false, documents: [] });
    });
    const api = createDriverDocumentsApi(base, session, fetcher as typeof fetch);
    expect(await api.getMyDocuments()).toEqual({ documents_enabled: false, documents: [] });
    expect(fetcher).toHaveBeenCalledOnce();
    expect(MyDriverDocumentsPath).toBe("/api/v1/driver/me/documents");
  });

  it("sends nothing with a remembered session", async () => {
    const fetcher = vi.fn();
    await expect(
      createDriverDocumentsApi(base, { ...session, remembered: true }, fetcher as typeof fetch).getMyDocuments(),
    ).rejects.toMatchObject({ category: "network" });
    expect(fetcher).not.toHaveBeenCalled();
  });

  it.each([
    [401, "unauthorized"],
    [403, "forbidden"],
    [503, "unavailable"],
    [404, "invalid-contract"],
    [204, "invalid-contract"],
  ])("maps %i to %s", async (status, category) => {
    const fetcher = vi.fn(async () => new Response(status === 204 ? null : "{}", { status, headers: { "Content-Type": "application/json" } }));
    await expect(createDriverDocumentsApi(base, session, fetcher as typeof fetch).getMyDocuments()).rejects.toMatchObject({
      category,
    });
  });

  it("fails closed on an answer outside the contract or no answer", async () => {
    const outside = vi.fn(async () => json({ documents_enabled: true, documents: [], phone: "6671234567" }));
    await expect(createDriverDocumentsApi(base, session, outside as typeof fetch).getMyDocuments()).rejects.toBeInstanceOf(
      DriverDocumentsApiError,
    );
    const offline = vi.fn(async () => {
      throw new TypeError("Failed to fetch");
    });
    await expect(createDriverDocumentsApi(base, session, offline as typeof fetch).getMyDocuments()).rejects.toMatchObject({
      category: "network",
    });
  });
});
