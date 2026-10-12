import { afterEach, describe, expect, it, vi } from "vitest";
import { bearerSession, jsonResponse, problem, syntheticUuid } from "../contracts/ui-001-screens.fixtures";
import { createLocationsApi } from "./locations-api";
import { TenantApiError } from "./tenant-request";

/** UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11: listCities and listServiceAreas for the route creation. */
const base = "https://api.synthetic.test";
const cityId = syntheticUuid(0x900);
const city = { id: cityId, name: "Culiacán", state_code: "SIN", country_code: "MX", timezone: "America/Mazatlan" };

function lastCall(fetchMock: ReturnType<typeof vi.fn>): [URL, RequestInit] {
  return fetchMock.mock.calls.at(-1) as [URL, RequestInit];
}

afterEach(() => vi.unstubAllGlobals());

describe("locations api (UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11)", () => {
  it("lists the cities of the active organization", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(200, [city]));
    vi.stubGlobal("fetch", fetchMock);
    expect(await createLocationsApi(base, bearerSession()).listCities()).toEqual([city]);
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe("/api/v1/cities");
    expect(url.search).toBe("");
    expect(init.method).toBe("GET");
    expect((init.headers as Record<string, string>)["X-Organization-Id"]).toBe(bearerSession().organizationId);
  });

  it("lists the service areas of one city, asking for that city only", async () => {
    const areas = [{ id: syntheticUuid(0x951), city_id: cityId, name: "Centro", status: "ACTIVE" }];
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(200, areas));
    vi.stubGlobal("fetch", fetchMock);
    expect(await createLocationsApi(base, bearerSession()).listServiceAreas(cityId)).toEqual(areas);
    const [url] = lastCall(fetchMock);
    expect(url.pathname).toBe("/api/v1/service-areas");
    expect([...url.searchParams.entries()]).toEqual([["city_id", cityId]]);
  });

  it("fails closed on an answer outside the contract or for another city", async () => {
    const api = createLocationsApi(base, bearerSession());
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(200, [{ ...city, extra: 1 }])));
    await expect(api.listCities()).rejects.toMatchObject({ category: "invalid" });
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(200, "Culiacán", "text/plain")));
    await expect(api.listCities()).rejects.toMatchObject({ category: "invalid" });
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(jsonResponse(200, [{ id: syntheticUuid(0x951), city_id: syntheticUuid(0x901), name: "Norte", status: "ACTIVE" }])),
    );
    await expect(api.listServiceAreas(cityId)).rejects.toMatchObject({ category: "invalid" });
  });

  it("keeps the failure category and the MFA flag, and checks the city id before calling", async () => {
    const api = createLocationsApi(base, bearerSession());
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(403, "MFA_REQUIRED")));
    await expect(api.listCities()).rejects.toMatchObject({ category: "forbidden", mfaRequired: true });
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(404)));
    await expect(api.listServiceAreas(cityId)).rejects.toMatchObject({ category: "not_found" });
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(503)));
    await expect(api.listCities()).rejects.toMatchObject({ category: "unavailable" });
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    await expect(api.listServiceAreas("not-an-id")).rejects.toBeInstanceOf(TenantApiError);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
