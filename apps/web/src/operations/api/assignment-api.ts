import {
  assignableDriverSearch,
  assignDriverBody,
  parseAssignableDriverPage,
  parseAssignment,
  type AssignableDriverPage,
  type Assignment,
} from "../contracts/assignable-driver";
import type { OperationsSession } from "../session/operations-session";
import { assertUuid, createTenantRequester, readJson, TenantApiError } from "./tenant-request";

/**
 * UI-PHASE2-DRIVER-PICKER-2026-10-05: AI-05 listAssignableDrivers and assignDriver for the
 * order detail. Failures are {@link TenantApiError}s carrying only a category and the
 * stable problem code; no identifier or server text reaches the message.
 */
export interface AssignmentApi {
  listAssignableDrivers(
    orderId: string,
    cursor?: string | null,
    signal?: AbortSignal,
  ): Promise<AssignableDriverPage>;
  assignDriver(
    orderId: string,
    driverId: string,
    costCents: number,
    idempotencyKey: string,
    signal?: AbortSignal,
  ): Promise<Assignment>;
}

export function createAssignmentApi(baseUrl: string, session: OperationsSession): AssignmentApi {
  const send = createTenantRequester(baseUrl, session);
  return {
    async listAssignableDrivers(orderId, cursor, signal) {
      assertUuid(orderId);
      let search: URLSearchParams;
      try {
        search = assignableDriverSearch(cursor);
      } catch {
        throw new TenantApiError("invalid");
      }
      const response = await send({
        method: "GET",
        path: `/api/v1/orders/${encodeURIComponent(orderId)}/assignable-drivers`,
        search,
        signal,
      });
      if (response.status !== 200) throw new TenantApiError("invalid");
      return (await readJson(response, parseAssignableDriverPage)) as AssignableDriverPage;
    },
    async assignDriver(orderId, driverId, costCents, idempotencyKey, signal) {
      assertUuid(orderId);
      assertUuid(driverId);
      let body;
      try {
        body = assignDriverBody(driverId, costCents);
      } catch {
        throw new TenantApiError("invalid");
      }
      const response = await send({
        method: "POST",
        path: `/api/v1/orders/${encodeURIComponent(orderId)}/assignments`,
        body,
        idempotencyKey,
        signal,
      });
      if (response.status !== 201) throw new TenantApiError("invalid");
      const assignment = (await readJson(response, parseAssignment)) as Assignment;
      if (
        assignment.order_id !== orderId ||
        assignment.driver_id !== driverId ||
        assignment.cost.amount_cents !== costCents
      )
        throw new TenantApiError("invalid");
      return assignment;
    },
  };
}
