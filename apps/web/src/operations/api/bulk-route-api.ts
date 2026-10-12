import { isDraftRouteOf, routeDateText } from "../contracts/bulk-route";
import type { City } from "../contracts/locations";
import {
  parseManualRoute,
  parseManualRouteDetail,
  parseManualRoutePage,
  type ManualRoute,
  type ManualRouteDetail,
  type ManualRoutePage,
} from "../contracts/manual-route";
import type { OperationsSession } from "../session/operations-session";
import { createLocationsApi } from "./locations-api";
import { createOperationsApi } from "./operations-api";
import { assertUuid, createTenantRequester, readJson, TenantApiError } from "./tenant-request";

/**
 * UI-PHASE3-INBOX-BULK-ADD-TO-ROUTE-2026-10-11: the AI-05 Routing operations "Agregar estas
 * órdenes a una ruta…" uses - listRoutes, asked only for the DRAFT routes of one driver, getRoute,
 * to check the route after a generic conflict, and addRouteStop. Unlike routes-api (the route
 * planner), failures are {@link TenantApiError}s that carry the problem code (VERSION_CONFLICT,
 * DRIVER_INELIGIBLE…) and MFA_REQUIRED, so each order gets its own es-MX result and the MFA step-up.
 * An answer outside the request fails closed.
 *
 * UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11: "Crear ruta nueva" also reads each order of the batch
 * with the existing getOrder client (operations-api) for its city_id, names that city with
 * listCities (locations-api) and creates the route with createRoute.
 */
export interface BulkRouteApi {
  listDraftRoutes(driverId: string, cursor: string | null, signal?: AbortSignal): Promise<ManualRoutePage>;
  getRoute(routeId: string, signal?: AbortSignal): Promise<ManualRouteDetail>;
  addRouteStop(
    routeId: string,
    orderId: string,
    expectedVersion: number,
    idempotencyKey: string,
    signal?: AbortSignal,
  ): Promise<ManualRouteDetail>;
  /** The order's city (Order.city_id); a failure is any rejection, never a guess. */
  getOrder(orderId: string, signal?: AbortSignal): Promise<{ readonly id: string; readonly city_id: string }>;
  listCities(signal?: AbortSignal): Promise<readonly City[]>;
  createRoute(body: CreateRouteBody, idempotencyKey: string, signal?: AbortSignal): Promise<ManualRoute>;
}

/** What "Crear ruta nueva" sends: the batch's driver, the orders' city and today in Mazatlán; never an area. */
export interface CreateRouteBody {
  readonly driverId: string;
  readonly cityId: string;
  /** `YYYY-MM-DD`. */
  readonly scheduledFor: string;
}

/** listRoutes cursors are opaque base64 text the server bounds to 256 characters. */
const routeCursorPattern = /^[A-Za-z0-9+/]{1,254}={0,2}$/;

function isRouteCursor(value: string): boolean {
  return value.length <= 256 && routeCursorPattern.test(value);
}

export function createBulkRouteApi(baseUrl: string, session: OperationsSession): BulkRouteApi {
  const send = createTenantRequester(baseUrl, session);
  const operations = createOperationsApi(baseUrl, session);
  const locations = createLocationsApi(baseUrl, session);
  return {
    async getOrder(orderId, signal) {
      const order = await operations.getOrder(orderId, signal);
      if (order.id !== orderId) throw new TenantApiError("invalid");
      return order;
    },
    listCities: (signal) => locations.listCities(signal),
    async createRoute(body, idempotencyKey, signal) {
      assertUuid(body.driverId);
      assertUuid(body.cityId);
      if (routeDateText(body.scheduledFor) === null) throw new TenantApiError("invalid");
      const response = await send({
        method: "POST",
        path: "/api/v1/routes",
        body: {
          driver_id: body.driverId,
          city_id: body.cityId,
          service_area_id: null,
          scheduled_for: body.scheduledFor,
        },
        idempotencyKey,
        signal,
      });
      if (response.status !== 201) throw new TenantApiError("invalid");
      const route = (await readJson(response, parseManualRoute)) as ManualRoute;
      // The created route, or the stored answer of its replay: DRAFT at version 1, of this driver, city and date,
      // for the whole city, without stops.
      if (
        route.status !== "DRAFT" ||
        route.version !== 1 ||
        route.driver_id !== body.driverId ||
        route.city_id !== body.cityId ||
        route.service_area_id !== null ||
        route.scheduled_for !== body.scheduledFor ||
        route.stop_count !== 0 ||
        route.assignment_cost_cents_total !== 0
      )
        throw new TenantApiError("invalid");
      return route;
    },
    async listDraftRoutes(driverId, cursor, signal) {
      assertUuid(driverId);
      const search = new URLSearchParams({ status: "DRAFT", driver_id: driverId });
      if (cursor !== null) {
        if (!isRouteCursor(cursor)) throw new TenantApiError("invalid");
        search.set("cursor", cursor);
      }
      const response = await send({ method: "GET", path: "/api/v1/routes", search, signal });
      if (response.status !== 200) throw new TenantApiError("invalid");
      const page = (await readJson(response, parseManualRoutePage)) as ManualRoutePage;
      // Only the DRAFT routes of this driver were asked for; any other row fails the whole read.
      if (
        !page.items.every((route) => isDraftRouteOf(route, driverId)) ||
        (page.next_cursor !== null && !isRouteCursor(page.next_cursor))
      )
        throw new TenantApiError("invalid");
      return page;
    },
    async getRoute(routeId, signal) {
      assertUuid(routeId);
      const response = await send({ method: "GET", path: `/api/v1/routes/${encodeURIComponent(routeId)}`, signal });
      if (response.status !== 200) throw new TenantApiError("invalid");
      const detail = (await readJson(response, parseManualRouteDetail)) as ManualRouteDetail;
      if (detail.id !== routeId) throw new TenantApiError("invalid");
      return detail;
    },
    async addRouteStop(routeId, orderId, expectedVersion, idempotencyKey, signal) {
      assertUuid(routeId);
      assertUuid(orderId);
      if (!Number.isSafeInteger(expectedVersion) || expectedVersion < 1) throw new TenantApiError("invalid");
      const response = await send({
        method: "POST",
        path: `/api/v1/routes/${encodeURIComponent(routeId)}/stops`,
        body: { order_id: orderId, expected_version: expectedVersion },
        idempotencyKey,
        signal,
      });
      if (response.status !== 201) throw new TenantApiError("invalid");
      const detail = (await readJson(response, parseManualRouteDetail)) as ManualRouteDetail;
      // The authoritative route after this stop (or its idempotent replay): the same DRAFT route,
      // one version later, holding the order.
      if (
        detail.id !== routeId ||
        detail.status !== "DRAFT" ||
        detail.version !== expectedVersion + 1 ||
        !detail.stops.some((stop) => stop.order_id === orderId)
      )
        throw new TenantApiError("invalid");
      return detail;
    },
  };
}
