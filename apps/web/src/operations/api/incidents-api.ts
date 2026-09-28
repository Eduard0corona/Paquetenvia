import {
  parseIncident,
  type Incident,
  type OpenIncidentBody,
  type ResolveIncidentBody,
} from "../contracts/incident";
import type { OperationsSession } from "../session/operations-session";
import { assertUuid, createTenantRequester, readJson } from "./tenant-request";

/** AI-05 openIncident and resolveIncident (INC-001). */
export interface IncidentsApi {
  open(orderId: string, body: OpenIncidentBody, idempotencyKey: string, signal?: AbortSignal): Promise<Incident>;
  resolve(
    incidentId: string,
    body: ResolveIncidentBody,
    idempotencyKey: string,
    signal?: AbortSignal,
  ): Promise<Incident>;
}

export function createIncidentsApi(baseUrl: string, session: OperationsSession): IncidentsApi {
  const send = createTenantRequester(baseUrl, session);
  return {
    async open(orderId, body, idempotencyKey, signal) {
      assertUuid(orderId);
      const response = await send({
        method: "POST",
        path: `/api/v1/orders/${encodeURIComponent(orderId)}/incidents`,
        body,
        idempotencyKey,
        signal,
      });
      return (await readJson(response, parseIncident)) as Incident;
    },
    async resolve(incidentId, body, idempotencyKey, signal) {
      assertUuid(incidentId);
      const response = await send({
        method: "POST",
        path: `/api/v1/incidents/${encodeURIComponent(incidentId)}/resolution`,
        body,
        idempotencyKey,
        signal,
      });
      return (await readJson(response, parseIncident)) as Incident;
    },
  };
}
