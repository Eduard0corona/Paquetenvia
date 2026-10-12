import {
  organizationDriverSearch,
  parseOrganizationDriverPage,
  type OrganizationDriverPage,
} from "../contracts/organization-driver";
import type { OperationsSession } from "../session/operations-session";
import { createTenantRequester, readJson, TenantApiError } from "./tenant-request";

/**
 * UI-PHASE3-DRIVERS-2026-10-11: AI-05 listOrganizationDrivers for the "Repartidores" screen. Failures are
 * {@link TenantApiError}s carrying only a category and the stable problem code; no identifier or server text reaches
 * the message.
 */
export interface DriversApi {
  list(cursor?: string | null, signal?: AbortSignal): Promise<OrganizationDriverPage>;
}

export function createDriversApi(baseUrl: string, session: OperationsSession): DriversApi {
  const send = createTenantRequester(baseUrl, session);
  return {
    async list(cursor, signal) {
      let search: URLSearchParams;
      try {
        search = organizationDriverSearch(cursor);
      } catch {
        throw new TenantApiError("invalid");
      }
      const response = await send({ method: "GET", path: "/api/v1/drivers", search, signal });
      if (response.status !== 200) throw new TenantApiError("invalid");
      return (await readJson(response, parseOrganizationDriverPage)) as OrganizationDriverPage;
    },
  };
}
