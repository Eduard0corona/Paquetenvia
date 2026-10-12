import {
  hasUsableCredentials,
  type SessionCredentials,
} from "../../auth/request-credentials";
import { asUuid } from "../../realtime/envelope";

export type DriverSession = SessionCredentials & {
  readonly organizationId: string;
  readonly cacheNamespace: string;
  /**
   * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12 (owner answer "Registrar desde
   * Despacho (Recommended)"): the person also holds DISPATCHER or
   * PLATFORM_ADMIN in this organization, read from the organization contexts
   * at sign-in and kept in memory only. The server then treats a report from
   * the phone as dispatch's, so "No se pudo entregar" is not offered. Only
   * `true` counts; absent means a driver only.
   */
  readonly alsoDispatch?: boolean;
};

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
