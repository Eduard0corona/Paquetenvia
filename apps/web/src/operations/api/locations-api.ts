import { parseCities, parseServiceAreas, type City, type ServiceArea } from "../contracts/locations";
import type { OperationsSession } from "../session/operations-session";
import { assertUuid, createTenantRequester, readJson, TenantApiError } from "./tenant-request";

/**
 * UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11: the AI-05 Locations reads behind the route creation - listCities (the
 * orders' city named in the inbox step, the city selector of Rutas) and listServiceAreas (the service area selector of
 * Rutas, once a city is chosen). Failures are {@link TenantApiError}s with only a category, the stable problem code and
 * the MFA flag; no identifier or server text reaches a message.
 */
export interface LocationsApi {
  listCities(signal?: AbortSignal): Promise<readonly City[]>;
  listServiceAreas(cityId: string, signal?: AbortSignal): Promise<readonly ServiceArea[]>;
}

export function createLocationsApi(baseUrl: string, session: OperationsSession): LocationsApi {
  const send = createTenantRequester(baseUrl, session);
  return {
    async listCities(signal) {
      const response = await send({ method: "GET", path: "/api/v1/cities", signal });
      if (response.status !== 200) throw new TenantApiError("invalid");
      return (await readJson(response, parseCities)) as readonly City[];
    },
    async listServiceAreas(cityId, signal) {
      assertUuid(cityId);
      const search = new URLSearchParams({ city_id: cityId });
      const response = await send({ method: "GET", path: "/api/v1/service-areas", search, signal });
      if (response.status !== 200) throw new TenantApiError("invalid");
      return (await readJson(response, (value) => parseServiceAreas(value, cityId))) as readonly ServiceArea[];
    },
  };
}
