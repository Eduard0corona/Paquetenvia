import type { OperationsSession } from "../session/operations-session";
import {
  isRouteCursor,
  parseManualRoute,
  parseManualRouteDetail,
  parseManualRoutePage,
  routeDateText,
  type ManualRoute,
  type ManualRouteDetail,
  type ManualRoutePage,
} from "../contracts/manual-route";
import { normalizePublicIdInput } from "../contracts/order-transitions";
import { createOperationsApi } from "./operations-api";
import { createOrderActionsApi } from "./order-actions-api";
import { assertUuid, createTenantRequester, readJson, TenantApiError } from "./tenant-request";

/**
 * The AI-05 Routing operations of the route planner (`/ops/routes`): listRoutes, getRoute, createRoute,
 * addRouteStop, removeRouteStop and reorderRouteStops.
 *
 * UI-PHASE3-ROUTES-NO-IDS-2026-10-12 ("Sí, todo (Recommended)"): failures are {@link TenantApiError}s from the
 * shared tenant requester, like the inbox, so the problem code (VERSION_CONFLICT, DRIVER_INELIGIBLE…) and
 * MFA_REQUIRED reach the screen, which says why a change was refused and offers the MFA step-up. "Agregar entrega"
 * finds the order by its guía with listOrders?public_id= (the top-bar search client) and each stop shows its guía
 * from getOrder (the client of the order detail). Every answer outside the request fails closed; no identifier,
 * typed text or server message reaches an error.
 */
export interface CreateRouteInput {
  readonly driverId: string;
  readonly cityId: string;
  readonly serviceAreaId: string | null;
  /** `YYYY-MM-DD`, or null for a route without a date. */
  readonly scheduledFor: string | null;
}

export interface RoutesApi {
  /** One page of listRoutes, in the server's order (newest first). */
  list(cursor: string | null, signal?: AbortSignal): Promise<ManualRoutePage>;
  get(routeId: string, signal?: AbortSignal): Promise<ManualRouteDetail>;
  create(input: CreateRouteInput, idempotencyKey: string, signal?: AbortSignal): Promise<ManualRoute>;
  addStop(routeId: string, orderId: string, expectedVersion: number, idempotencyKey: string, signal?: AbortSignal): Promise<ManualRouteDetail>;
  removeStop(routeId: string, stopId: string, expectedVersion: number, idempotencyKey: string, signal?: AbortSignal): Promise<ManualRouteDetail>;
  reorder(routeId: string, stopIds: readonly string[], expectedVersion: number, idempotencyKey: string, signal?: AbortSignal): Promise<ManualRouteDetail>;
  /** listOrders?public_id=: the order id of an exact guía, or null when the organization has no such order. */
  findOrderIdByPublicId(publicId: string, signal?: AbortSignal): Promise<string | null>;
  /** getOrder: the order's guía (Order.public_id, `ORD_` and 22 characters); anything else fails closed. */
  getOrderGuide(orderId: string, signal?: AbortSignal): Promise<string>;
}

export function createRoutesApi(baseUrl: string, session: OperationsSession): RoutesApi {
  const send = createTenantRequester(baseUrl, session);
  const orders = createOrderActionsApi(baseUrl, session);
  const operations = createOperationsApi(baseUrl, session);

  async function detail(response: Response, routeId: string): Promise<ManualRouteDetail> {
    const route = (await readJson(response, parseManualRouteDetail)) as ManualRouteDetail;
    if (route.id !== routeId) throw new TenantApiError("invalid");
    return route;
  }

  return {
    async list(cursor, signal) {
      const search = new URLSearchParams();
      if (cursor !== null) {
        if (!isRouteCursor(cursor)) throw new TenantApiError("invalid");
        search.set("cursor", cursor);
      }
      const response = await send({ method: "GET", path: "/api/v1/routes", search, signal });
      if (response.status !== 200) throw new TenantApiError("invalid");
      const page = (await readJson(response, parseManualRoutePage)) as ManualRoutePage;
      if (page.next_cursor !== null && !isRouteCursor(page.next_cursor)) throw new TenantApiError("invalid");
      return page;
    },
    async get(routeId, signal) {
      assertUuid(routeId);
      const response = await send({ method: "GET", path: `/api/v1/routes/${encodeURIComponent(routeId)}`, signal });
      if (response.status !== 200) throw new TenantApiError("invalid");
      return await detail(response, routeId);
    },
    async create(input, idempotencyKey, signal) {
      assertUuid(input.driverId);
      assertUuid(input.cityId);
      if (input.serviceAreaId !== null) assertUuid(input.serviceAreaId);
      if (input.scheduledFor !== null && routeDateText(input.scheduledFor) === null) throw new TenantApiError("invalid");
      const response = await send({
        method: "POST",
        path: "/api/v1/routes",
        body: {
          driver_id: input.driverId,
          city_id: input.cityId,
          service_area_id: input.serviceAreaId,
          scheduled_for: input.scheduledFor,
        },
        idempotencyKey,
        signal,
      });
      if (response.status !== 201) throw new TenantApiError("invalid");
      const route = (await readJson(response, parseManualRoute)) as ManualRoute;
      // The created route: DRAFT at version 1, without stops, for what was asked.
      if (
        route.status !== "DRAFT" ||
        route.version !== 1 ||
        route.driver_id !== input.driverId ||
        route.city_id !== input.cityId ||
        route.service_area_id !== input.serviceAreaId ||
        route.scheduled_for !== input.scheduledFor ||
        route.stop_count !== 0 ||
        route.assignment_cost_cents_total !== 0
      )
        throw new TenantApiError("invalid");
      return route;
    },
    async addStop(routeId, orderId, expectedVersion, idempotencyKey, signal) {
      assertUuid(routeId);
      assertUuid(orderId);
      assertVersion(expectedVersion);
      const response = await send({
        method: "POST",
        path: `/api/v1/routes/${encodeURIComponent(routeId)}/stops`,
        body: { order_id: orderId, expected_version: expectedVersion },
        idempotencyKey,
        signal,
      });
      if (response.status !== 201) throw new TenantApiError("invalid");
      const route = await detail(response, routeId);
      // The authoritative route after the stop: still DRAFT, one version later, holding the order.
      if (
        route.status !== "DRAFT" ||
        route.version !== expectedVersion + 1 ||
        !route.stops.some((stop) => stop.order_id === orderId)
      )
        throw new TenantApiError("invalid");
      return route;
    },
    async removeStop(routeId, stopId, expectedVersion, idempotencyKey, signal) {
      assertUuid(routeId);
      assertUuid(stopId);
      assertVersion(expectedVersion);
      const response = await send({
        method: "DELETE",
        path: `/api/v1/routes/${encodeURIComponent(routeId)}/stops/${encodeURIComponent(stopId)}`,
        search: new URLSearchParams({ expected_version: String(expectedVersion) }),
        idempotencyKey,
        signal,
      });
      if (response.status !== 200) throw new TenantApiError("invalid");
      const route = await detail(response, routeId);
      // One version later, without the stop.
      if (route.version !== expectedVersion + 1 || route.stops.some((stop) => stop.id === stopId))
        throw new TenantApiError("invalid");
      return route;
    },
    async reorder(routeId, stopIds, expectedVersion, idempotencyKey, signal) {
      assertUuid(routeId);
      for (const stopId of stopIds) assertUuid(stopId);
      if (new Set(stopIds).size !== stopIds.length) throw new TenantApiError("invalid");
      assertVersion(expectedVersion);
      const response = await send({
        method: "PUT",
        path: `/api/v1/routes/${encodeURIComponent(routeId)}/stops/order`,
        body: { expected_version: expectedVersion, stop_ids: stopIds },
        idempotencyKey,
        signal,
      });
      if (response.status !== 200) throw new TenantApiError("invalid");
      const route = await detail(response, routeId);
      // One version later, with the stops in the order sent.
      if (
        route.version !== expectedVersion + 1 ||
        route.stops.length !== stopIds.length ||
        route.stops.some((stop, index) => stop.id !== stopIds[index])
      )
        throw new TenantApiError("invalid");
      return route;
    },
    findOrderIdByPublicId: (publicId, signal) => orders.findOrderIdByPublicId(publicId, signal),
    async getOrderGuide(orderId, signal) {
      const order = await operations.getOrder(orderId, signal);
      if (order.id !== orderId || normalizePublicIdInput(order.public_id) !== order.public_id)
        throw new TenantApiError("invalid");
      return order.public_id;
    },
  };
}

function assertVersion(value: number): void {
  if (!Number.isSafeInteger(value) || value < 1) throw new TenantApiError("invalid");
}
