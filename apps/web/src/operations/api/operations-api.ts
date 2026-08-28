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
  publishExternalOffer(
    input: PublishExternalOfferInput,
    idempotencyKey: string,
    signal?: AbortSignal,
  ): Promise<void>;
}

export interface PublishExternalOfferInput {
  readonly orderId: string;
  readonly commissionCents: number;
  readonly expiresAt: string;
  readonly vehicleType: "MOTORCYCLE" | "CAR" | "VAN" | "BICYCLE" | "WALKER";
}

export class OperationsApiError extends Error {
  public constructor(
    public readonly category:
      | "unauthorized"
      | "forbidden"
      | "not_found"
      | "invalid"
      | "conflict"
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
    const timeoutController = new AbortController();
    const timeoutId = setTimeout(
      () =>
        timeoutController.abort(
          new DOMException("The operations request timed out.", "TimeoutError"),
        ),
      timeoutMilliseconds,
    );
    const combined = signal
      ? AbortSignal.any([signal, timeoutController.signal])
      : timeoutController.signal;
    try {
      const token = await waitForAbort(
        () => session.getAccessToken(),
        combined,
      );
      throwIfAborted(combined);
      if (typeof token !== "string" || token.length < 1 || token.length > 8192) {
        throw new OperationsApiError("unauthorized");
      }
      const url = new URL(path, base);
      url.search = search.toString();
      let response: Response;
      try {
        throwIfAborted(combined);
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
      } catch {
        if (combined.aborted) throw abortReason(combined);
        throw new OperationsApiError("network");
      }
      throwIfAborted(combined);
      if (!response.ok) throw classify(response.status);
      const contentType = response.headers.get("content-type") ?? "";
      if (!contentType.toLowerCase().includes("application/json")) {
        throw new OperationsApiError("invalid");
      }
      const body = await waitForAbort(() => response.json(), combined);
      throwIfAborted(combined);
      return parser(body);
    } finally {
      clearTimeout(timeoutId);
    }
  }

  async function postExternalOffer(
    input: PublishExternalOfferInput,
    idempotencyKey: string,
    signal?: AbortSignal,
  ): Promise<void> {
    assertUuid(input.orderId);
    if (!Number.isSafeInteger(input.commissionCents) || input.commissionCents < 0)
      throw new OperationsApiError("invalid");
    const token = await session.getAccessToken();
    if (!token) throw new OperationsApiError("unauthorized");
    let response: Response;
    try {
      response = await fetch(new URL("/api/v1/external-offers", base), {
        method: "POST",
        headers: {
          Authorization: `Bearer ${token}`,
          "X-Organization-Id": session.organizationId,
          "Idempotency-Key": idempotencyKey,
          Accept: "application/json",
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          order_id: input.orderId,
          commission_cents: input.commissionCents,
          expires_at: input.expiresAt,
          eligible_constraints: { vehicle_types: [input.vehicleType] },
        }),
        cache: "no-store",
        credentials: "omit",
        referrerPolicy: "no-referrer",
        signal,
      });
    } catch {
      if (signal?.aborted) throw signal.reason;
      throw new OperationsApiError("network");
    }
    if (!response.ok) throw classify(response.status);
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
    publishExternalOffer: postExternalOffer,
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

function waitForAbort<T>(
  operation: () => T | PromiseLike<T>,
  signal: AbortSignal,
): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    if (signal.aborted) {
      reject(abortReason(signal));
      return;
    }

    let value: T | PromiseLike<T>;
    try {
      value = operation();
    } catch (error: unknown) {
      reject(error);
      return;
    }

    let settled = false;
    const finish = (callback: () => void) => {
      if (settled) return;
      settled = true;
      signal.removeEventListener("abort", onAbort);
      callback();
    };
    const onAbort = () => finish(() => reject(abortReason(signal)));
    signal.addEventListener("abort", onAbort, { once: true });
    if (signal.aborted) onAbort();
    Promise.resolve(value).then(
      (result) => finish(() => resolve(result)),
      (error: unknown) => finish(() => reject(error)),
    );
  });
}

function throwIfAborted(signal: AbortSignal): void {
  if (signal.aborted) throw abortReason(signal);
}

function abortReason(signal: AbortSignal): unknown {
  return (
    signal.reason ??
    new DOMException("The operations request was aborted.", "AbortError")
  );
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
  if (status === 409) return new OperationsApiError("conflict");
  if (status === 400) return new OperationsApiError("invalid");
  return new OperationsApiError("unavailable");
}
