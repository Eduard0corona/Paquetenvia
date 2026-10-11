import { afterEach, describe, expect, it, vi } from "vitest";
import type { OperationsSession } from "../session/operations-session";
import { createDriversApi } from "./drivers-api";

const organizationId = "11111111-1111-4111-8111-111111111111";
const session: OperationsSession = {
  organizationId,
  sessionNamespace: "synthetic",
  getAccessToken: () => "ephemeral-token",
};
const driver = {
  driver_id: "d71e0000-0000-4000-8000-000000000002",
  driver_reference: "DRV-3d93b4c6",
  vehicle_type: "VAN",
  status: "SUSPENDED",
  active_stop_count: 0,
  last_position_at: null,
};

function json(status: number, body: unknown, type = "application/json"): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": type } });
}

afterEach(() => vi.unstubAllGlobals());

/** UI-PHASE3-DRIVERS-2026-10-11: AI-05 listOrganizationDrivers as the "Repartidores" screen calls it. */
describe("drivers api", () => {
  it("lists the selected organization's drivers with the tenant header, no cache and only a cursor", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(json(200, { items: [driver], next_cursor: "AgAAAAAAAAAAAAAAAAAAAAB" }))
      .mockResolvedValueOnce(json(200, { items: [], next_cursor: null }));
    vi.stubGlobal("fetch", fetchMock);
    const api = createDriversApi("https://api.synthetic.test", session);

    const page = await api.list();
    const [url, init] = fetchMock.mock.calls[0] as [URL, RequestInit];
    expect(url.toString()).toBe("https://api.synthetic.test/api/v1/drivers");
    expect(init).toMatchObject({ method: "GET", cache: "no-store", referrerPolicy: "no-referrer" });
    expect((init.headers as Record<string, string>)["X-Organization-Id"]).toBe(organizationId);
    expect(page).toEqual({ items: [driver], next_cursor: "AgAAAAAAAAAAAAAAAAAAAAB" });

    await api.list(page.next_cursor);
    expect((fetchMock.mock.calls[1] as [URL])[0].search).toBe("?cursor=AgAAAAAAAAAAAAAAAAAAAAB");
  });

  it("fails closed on a response outside the contract and refuses a cursor before any request", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json(200, { items: [{ ...driver, email: "x" }], next_cursor: null }));
    vi.stubGlobal("fetch", fetchMock);
    const api = createDriversApi("https://api.synthetic.test", session);
    await expect(api.list()).rejects.toMatchObject({ category: "invalid" });
    await expect(api.list("a&b")).rejects.toMatchObject({ category: "invalid" });
    expect(fetchMock).toHaveBeenCalledOnce();
  });

  it("reports MFA_REQUIRED, the uniform 403 and an unavailable list by category only", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(json(403, { status: 403, title: "Forbidden.", code: "MFA_REQUIRED" }, "application/problem+json"))
        .mockResolvedValueOnce(json(403, { status: 403, title: "Forbidden." }, "application/problem+json"))
        .mockResolvedValueOnce(json(503, { status: 503, title: "Service Unavailable." }, "application/problem+json")),
    );
    const api = createDriversApi("https://api.synthetic.test", session);
    await expect(api.list()).rejects.toMatchObject({ category: "forbidden", mfaRequired: true });
    await expect(api.list()).rejects.toMatchObject({ category: "forbidden", mfaRequired: false });
    await expect(api.list()).rejects.toMatchObject({ category: "unavailable", retryable: true });
  });
});
