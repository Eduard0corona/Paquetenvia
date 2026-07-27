import type {
  OperationsDashboardFilters,
  OperationsDashboardResponse,
  OperationsOrderDetail,
  OperationsOrganizationContext,
} from "../contracts/operations-dashboard";
import {
  parseOperationsDashboard,
  parseOperationsOrderDetail,
  parseOrganizationContexts,
} from "../contracts/operations-parsers";
import type { OperationsSession } from "../session/operations-session";

export interface OperationsDashboardApi {
  list(
    filters: OperationsDashboardFilters,
    signal?: AbortSignal,
  ): Promise<OperationsDashboardResponse>;
  getOrder(
    orderId: string,
    signal?: AbortSignal,
  ): Promise<OperationsOrderDetail>;
  organizationContexts(
    signal?: AbortSignal,
  ): Promise<readonly OperationsOrganizationContext[]>;
}

export class OperationsApiError extends Error {
  public constructor(
    public readonly category:
      | "unauthorized"
      | "forbidden"
      | "not_found"
      | "invalid"
      | "unavailable"
      | "network",
  ) {
    super("La consulta de operaciones no está disponible.");
    this.name = "OperationsApiError";
  }
}

export function createOperationsApi(
  baseUrl: string,
  session: OperationsSession,
  timeoutMilliseconds = 10_000,
): OperationsDashboardApi {
  const base = new URL(baseUrl);
  if (!["http:", "https:"].includes(base.protocol)) {
    throw new Error("Operations API base URL must use HTTP or HTTPS.");
  }

  async function get(
    path: string,
    search: URLSearchParams,
    parser: (value: unknown) => unknown,
    signal?: AbortSignal,
  ): Promise<unknown> {
    const timeout = AbortSignal.timeout(timeoutMilliseconds);
    const combined = signal
      ? AbortSignal.any([signal, timeout])
      : AbortSignal.any([timeout]);
    const token = await session.getAccessToken();
    if (typeof token !== "string" || token.length < 1 || token.length > 8192) {
      throw new OperationsApiError("unauthorized");
    }
    const url = new URL(path, base);
    url.search = search.toString();
    let response: Response;
    try {
      response = await fetch(url, {
        method: "GET",
        headers: {
          Authorization: `Bearer ${token}`,
          "X-Organization-Id": session.organizationId,
          Accept: "application/json",
        },
        cache: "no-store",
        credentials: "omit",
        referrerPolicy: "no-referrer",
        signal: combined,
      });
    } catch (error: unknown) {
      if (combined.aborted) throw error;
      throw new OperationsApiError("network");
    }
    if (!response.ok) throw classify(response.status);
    const contentType = response.headers.get("content-type") ?? "";
    if (!contentType.toLowerCase().includes("application/json")) {
      throw new OperationsApiError("invalid");
    }
    return parser(await response.json());
  }

  return {
    async list(filters, signal) {
      return (await get(
        "/api/v1/operations/dashboard",
        buildOperationsDashboardSearch(filters),
        parseOperationsDashboard,
        signal,
      )) as OperationsDashboardResponse;
    },
    async getOrder(orderId, signal) {
      assertUuid(orderId);
      return (await get(
        `/api/v1/orders/${encodeURIComponent(orderId)}`,
        new URLSearchParams(),
        parseOperationsOrderDetail,
        signal,
      )) as OperationsOrderDetail;
    },
    async organizationContexts(signal) {
      return (await get(
        "/api/v1/me/organization-contexts",
        new URLSearchParams(),
        parseOrganizationContexts,
        signal,
      )) as readonly OperationsOrganizationContext[];
    },
  };
}

export function buildOperationsDashboardSearch(
  filters: OperationsDashboardFilters,
): URLSearchParams {
  const search = new URLSearchParams();
  append(search, "order_id", filters.orderId);
  append(search, "status", filters.status);
  append(search, "delivery_zone_id", filters.deliveryZoneId);
  append(search, "client_account_id", filters.clientAccountId);
  append(search, "owner_org_id", filters.ownerOrganizationId);
  append(search, "operator_org_id", filters.operatorOrganizationId);
  append(search, "service_type", filters.serviceType);
  append(search, "created_from", filters.createdFrom);
  append(search, "created_to", filters.createdTo);
  if (filters.unassigned !== undefined)
    search.set("unassigned", String(filters.unassigned));
  append(search, "cursor", filters.cursor);
  return search;
}

function append(search: URLSearchParams, name: string, value?: string): void {
  if (value !== undefined && value !== "") search.set(name, value);
}

function assertUuid(value: string): void {
  if (
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(
      value,
    ) ||
    value === "00000000-0000-0000-0000-000000000000"
  )
    throw new OperationsApiError("invalid");
}

function classify(status: number): OperationsApiError {
  if (status === 401) return new OperationsApiError("unauthorized");
  if (status === 403) return new OperationsApiError("forbidden");
  if (status === 404) return new OperationsApiError("not_found");
  if (status === 400) return new OperationsApiError("invalid");
  return new OperationsApiError("unavailable");
}
