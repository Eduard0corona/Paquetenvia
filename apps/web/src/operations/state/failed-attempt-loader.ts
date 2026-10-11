import type { IncidentsApi } from "../api/incidents-api";
import { TenantApiError } from "../api/tenant-request";
import { failedAttemptView, type FailedAttemptView } from "../contracts/failed-attempt";

/**
 * INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11: reads the reason of an order's failed attempt
 * through listIncidents (DISPATCHER and PLATFORM_ADMIN with MFA). A caller without that read
 * (VIEWER, or a PLATFORM_ADMIN without MFA) sees nothing instead of an error: "Siguiente paso"
 * is never blocked by this read. Nothing is stored or logged.
 */
export type FailedAttemptState =
  | { readonly kind: "hidden" }
  | { readonly kind: "loading" }
  | { readonly kind: "unavailable" }
  | { readonly kind: "ready"; readonly view: FailedAttemptView };

export const failedAttemptUnavailableMessage = "No fue posible leer el motivo del intento fallido.";

export async function loadFailedAttempt(
  api: Pick<IncidentsApi, "list">,
  orderId: string,
  signal?: AbortSignal,
): Promise<FailedAttemptState> {
  try {
    const page = await api.list({ orderId }, null, signal);
    const view = failedAttemptView(page.items);
    return view === null ? { kind: "hidden" } : { kind: "ready", view };
  } catch (error: unknown) {
    if (
      error instanceof TenantApiError &&
      (error.category === "forbidden" || error.category === "unauthorized" || error.category === "not_found")
    ) {
      return { kind: "hidden" };
    }
    return { kind: "unavailable" };
  }
}
