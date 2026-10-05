import type { OperationsSession } from "../session/operations-session";
import { resolveRequestAuthorization, type RequestAuthorization } from "../../auth/request-credentials";
import type { ManualRoute, ManualRouteDetail, ManualRoutePage, RouteStatus } from "../contracts/manual-route";
import { parseManualRoute, parseManualRouteDetail, parseManualRoutePage } from "../contracts/manual-route";

export class RoutesApiError extends Error {
  public constructor(public readonly category: "unauthorized" | "forbidden" | "not_found" | "conflict" | "invalid" | "network") {
    super("La operación de rutas no está disponible.");
    this.name = "RoutesApiError";
  }
}

export interface CreateRouteInput {
  readonly driverId: string;
  readonly cityId: string;
  readonly serviceAreaId: string | null;
  readonly scheduledFor: string | null;
}

export interface RoutesApi {
  list(filters?: { status?: RouteStatus; driverId?: string; scheduledFor?: string; cursor?: string }, signal?: AbortSignal): Promise<ManualRoutePage>;
  get(routeId: string, signal?: AbortSignal): Promise<ManualRouteDetail>;
  create(input: CreateRouteInput, idempotencyKey: string, signal?: AbortSignal): Promise<ManualRoute>;
  addStop(routeId: string, orderId: string, expectedVersion: number, idempotencyKey: string, signal?: AbortSignal): Promise<ManualRouteDetail>;
  removeStop(routeId: string, stopId: string, expectedVersion: number, idempotencyKey: string, signal?: AbortSignal): Promise<ManualRouteDetail>;
  reorder(routeId: string, stopIds: readonly string[], expectedVersion: number, idempotencyKey: string, signal?: AbortSignal): Promise<ManualRouteDetail>;
}

export function createRoutesApi(baseUrl: string, session: OperationsSession): RoutesApi {
  const base = new URL(baseUrl);
  if (!['http:', 'https:'].includes(base.protocol)) throw new Error("Routes API base URL must use HTTP or HTTPS.");

  async function request(path: string, method: string, parser: (value: unknown) => unknown,
    body?: unknown, idempotencyKey?: string, signal?: AbortSignal): Promise<unknown> {
    let authorization: RequestAuthorization;
    try { authorization = await resolveRequestAuthorization(session, method); } catch { throw new RoutesApiError("unauthorized"); }
    const headers: Record<string, string> = {
      ...authorization.headers,
      "X-Organization-Id": session.organizationId,
      Accept: "application/json",
    };
    if (body !== undefined) headers["Content-Type"] = "application/json";
    if (idempotencyKey !== undefined) headers["Idempotency-Key"] = idempotencyKey;
    let response: Response;
    try {
      response = await fetch(new URL(path, base), {
        method, headers, body: body === undefined ? undefined : JSON.stringify(body),
        cache: "no-store", credentials: authorization.credentials, referrerPolicy: "no-referrer", signal,
      });
    } catch { if (signal?.aborted) throw signal.reason; throw new RoutesApiError("network"); }
    if (!response.ok) throw classify(response.status);
    const contentType = response.headers.get("content-type") ?? "";
    if (!contentType.toLowerCase().includes("application/json")) throw new RoutesApiError("invalid");
    try { return parser(await response.json()); } catch (error) {
      if (error instanceof RoutesApiError) throw error;
      throw new RoutesApiError("invalid");
    }
  }

  return {
    async list(filters = {}, signal) {
      const search = new URLSearchParams();
      if (filters.status) search.set("status", filters.status);
      if (filters.driverId) search.set("driver_id", filters.driverId);
      if (filters.scheduledFor) search.set("scheduled_for", filters.scheduledFor);
      if (filters.cursor) search.set("cursor", filters.cursor);
      return await request(`/api/v1/routes?${search}`, "GET", parseManualRoutePage, undefined, undefined, signal) as ManualRoutePage;
    },
    async get(routeId, signal) {
      return await request(`/api/v1/routes/${encodeURIComponent(routeId)}`, "GET", parseManualRouteDetail, undefined, undefined, signal) as ManualRouteDetail;
    },
    async create(input, key, signal) {
      return await request("/api/v1/routes", "POST", parseManualRoute, {
        driver_id: input.driverId, city_id: input.cityId,
        service_area_id: input.serviceAreaId, scheduled_for: input.scheduledFor,
      }, key, signal) as ManualRoute;
    },
    async addStop(routeId, orderId, expectedVersion, key, signal) {
      return await request(`/api/v1/routes/${encodeURIComponent(routeId)}/stops`, "POST", parseManualRouteDetail,
        { order_id: orderId, expected_version: expectedVersion }, key, signal) as ManualRouteDetail;
    },
    async removeStop(routeId, stopId, expectedVersion, key, signal) {
      return await request(`/api/v1/routes/${encodeURIComponent(routeId)}/stops/${encodeURIComponent(stopId)}?expected_version=${expectedVersion}`,
        "DELETE", parseManualRouteDetail, undefined, key, signal) as ManualRouteDetail;
    },
    async reorder(routeId, stopIds, expectedVersion, key, signal) {
      return await request(`/api/v1/routes/${encodeURIComponent(routeId)}/stops/order`, "PUT", parseManualRouteDetail,
        { expected_version: expectedVersion, stop_ids: stopIds }, key, signal) as ManualRouteDetail;
    },
  };
}

function classify(status: number): RoutesApiError {
  if (status === 401) return new RoutesApiError("unauthorized");
  if (status === 403) return new RoutesApiError("forbidden");
  if (status === 404) return new RoutesApiError("not_found");
  if (status === 409) return new RoutesApiError("conflict");
  return new RoutesApiError("network");
}
