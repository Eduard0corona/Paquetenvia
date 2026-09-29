import {
  incidentListSearch,
  parseIncident,
  parseIncidentPage,
  parseProofPage,
  type Incident,
  type IncidentListFilter,
  type IncidentPage,
  type OpenIncidentBody,
  type ProofPage,
  type ResolveIncidentBody,
} from "../contracts/incident";
import type { OperationsSession } from "../session/operations-session";
import { assertUuid, createTenantRequester, readJson, TenantApiError } from "./tenant-request";

/**
 * AI-05 openIncident and resolveIncident (INC-001), and the incident desk reads
 * listIncidents, getIncident and listOrderProofs (API-INC-LIST-PROOFS-2026-09-29).
 */
export interface IncidentsApi {
  list(filter: IncidentListFilter, cursor?: string | null, signal?: AbortSignal): Promise<IncidentPage>;
  get(incidentId: string, signal?: AbortSignal): Promise<Incident>;
  listOrderProofs(orderId: string, cursor?: string | null, signal?: AbortSignal): Promise<ProofPage>;
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
    async list(filter, cursor, signal) {
      let search: URLSearchParams;
      try {
        search = incidentListSearch(filter, cursor);
      } catch {
        throw new TenantApiError("invalid");
      }
      const response = await send({ method: "GET", path: "/api/v1/incidents", search, signal });
      return (await readJson(response, parseIncidentPage)) as IncidentPage;
    },
    async get(incidentId, signal) {
      assertUuid(incidentId);
      const response = await send({
        method: "GET",
        path: `/api/v1/incidents/${encodeURIComponent(incidentId)}`,
        signal,
      });
      return (await readJson(response, parseIncident)) as Incident;
    },
    async listOrderProofs(orderId, cursor, signal) {
      assertUuid(orderId);
      let search: URLSearchParams;
      try {
        search = incidentListSearch({}, cursor);
      } catch {
        throw new TenantApiError("invalid");
      }
      const response = await send({
        method: "GET",
        path: `/api/v1/orders/${encodeURIComponent(orderId)}/proofs`,
        search,
        signal,
      });
      return (await readJson(response, parseProofPage)) as ProofPage;
    },
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
