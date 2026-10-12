import {
  hasUsableCredentials,
  type SessionCredentials,
} from "../../auth/request-credentials";
import { asUuid } from "../../realtime/envelope";

export type DriverSession = SessionCredentials & {
  readonly organizationId: string;
  readonly cacheNamespace: string;
  /**
   * ORG-CONTEXT-ROLES-2026-10-12: every role the person holds in this
   * organization, copied at sign-in from the `roles` of the selected context
   * of GET /me/organization-contexts and kept in memory only. Null or absent
   * means unknown (an API answer without `roles`, or a session installed
   * without them); see {@link driverReportRole}.
   */
  readonly organizationRoles?: readonly string[] | null;
};

/**
 * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12 with ORG-CONTEXT-ROLES-2026-10-12:
 * who files "No se pudo entregar" for this session.
 *
 * - `driver`: the roles are known, include DRIVER and neither DISPATCHER nor
 *   PLATFORM_ADMIN; the report is offered.
 * - `dispatch`: they include DISPATCHER or PLATFORM_ADMIN. The server treats a
 *   report from the phone as dispatch's and refuses the short one, so the stop
 *   says to register the attempt in Despacho (owner answer "Registrar desde
 *   Despacho (Recommended)").
 * - `unknown`: the roles are missing or malformed. The report is not offered
 *   (fails closed) and the stop says why; nothing else of the app changes.
 */
export type DriverReportRole = "driver" | "dispatch" | "unknown";

export function driverReportRole(
  session: Pick<DriverSession, "organizationRoles">,
): DriverReportRole {
  const roles: unknown = session.organizationRoles;
  if (
    !Array.isArray(roles) ||
    !roles.every((role) => typeof role === "string") ||
    !roles.includes("DRIVER")
  ) {
    return "unknown";
  }
  return roles.includes("DISPATCHER") || roles.includes("PLATFORM_ADMIN")
    ? "dispatch"
    : "driver";
}

declare global {
  interface Window {
    __paquetenviaDriverSession?: DriverSession;
  }
}

export const driverSessionChangedEvent = "paquetenvia:driver-session-changed";

/**
 * Dispatched by "Cerrar sesión" before it signs out, so the stops screen stops
 * every controller before the saved stop details are wiped
 * (UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11).
 */
export const driverLogoutStartedEvent = "paquetenvia:driver-logout-started";

export function readBrowserDriverSession(): DriverSession | null {
  if (typeof window === "undefined") {
    return null;
  }

  const session = window.__paquetenviaDriverSession;
  if (!session || !hasUsableCredentials(session)) {
    return null;
  }

  try {
    asUuid(session.organizationId);
  } catch {
    return null;
  }

  if (
    typeof session.cacheNamespace !== "string" ||
    !/^[A-Za-z0-9_-]{16,128}$/.test(session.cacheNamespace)
  ) {
    return null;
  }

  return session;
}

export function sessionIdentity(session: DriverSession | null): string {
  return session
    ? `${session.cacheNamespace.length}:${session.cacheNamespace}:${session.organizationId}`
    : "unavailable";
}
