import {
  hasUsableCredentials,
  type SessionCredentials,
} from "../../auth/request-credentials";
import { asUuid } from "../../realtime/envelope";

export type DriverSession = SessionCredentials & {
  readonly organizationId: string;
  readonly cacheNamespace: string;
  /**
   * DRV-SESSION-EXPIRY-OFFLINE-2026-10-19 (owner answer E3): a session the
   * phone remembered (driver-session-marker) because `GET /auth/session` could
   * not be answered on this page load. It opens the saved partition only: the
   * stops screen reads and queues locally and makes no request, so nothing is
   * sent until the server confirms the same driver and organization and a
   * confirmed session (without this flag) replaces it.
   */
  readonly remembered?: true;
};

/** Whether the server confirmed this session (every Mock session counts as confirmed). */
export function isConfirmedDriverSession(session: DriverSession | null): boolean {
  return session !== null && session.remembered !== true;
}

declare global {
  interface Window {
    __paquetenviaDriverSession?: DriverSession;
  }
}

export const driverSessionChangedEvent = "paquetenvia:driver-session-changed";

/**
 * DRV-SESSION-EXPIRY-OFFLINE-2026-10-19 (E3): the stops screen asks the BFF
 * bootstrap to check the session again now ("Sincronizar ahora" or
 * "Reintentar" while the session is only remembered).
 */
export const driverSessionCheckRequestedEvent =
  "paquetenvia:driver-session-check-requested";

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
