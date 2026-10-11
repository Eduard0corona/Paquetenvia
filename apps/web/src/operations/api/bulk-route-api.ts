import { isDraftRouteOf } from "../contracts/bulk-route";
import {
  parseManualRouteDetail,
  parseManualRoutePage,
  type ManualRouteDetail,
  type ManualRoutePage,
} from "../contracts/manual-route";
import type { OperationsSession } from "../session/operations-session";
import { assertUuid, createTenantRequester, readJson, TenantApiError } from "./tenant-request";

/**
 * UI-PHASE3-INBOX-BULK-ADD-TO-ROUTE-2026-10-11: the AI-05 Routing operations "Agregar estas
 * órdenes a una ruta…" uses - listRoutes, asked only for the DRAFT routes of one driver, and
 * addRouteStop. Unlike routes-api (the route planner), failures are {@link TenantApiError}s that
 * carry the problem code (VERSION_CONFLICT, DRIVER_INELIGIBLE…) and MFA_REQUIRED, so each order gets
 * its own es-MX result and the MFA step-up. An answer outside the request fails closed.
 */
export interface BulkRouteApi {
  listDraftRoutes(driverId: string, cursor: string | null, signal?: AbortSignal): Promise<ManualRoutePage>;
  addRouteStop(
    routeId: string,
    orderId: string,
    expectedVersion: number,
    idempotencyKey: string,
    signal?: AbortSignal,
  ): Promise<ManualRouteDetail>;
}

/** listRoutes cursors are opaque base64 text the server bounds to 256 characters. */
const routeCursorPattern = /^[A-Za-z0-9+/]{1,254}={0,2}$/;

function isRouteCursor(value: string): boolean {
  return value.length <= 256 && routeCursorPattern.test(value);
}

export function createBulkRouteApi(baseUrl: string, session: OperationsSession): BulkRouteApi {
  const send = createTenantRequester(baseUrl, session);
  return {
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
