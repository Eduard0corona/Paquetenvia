import {
  hasUsableCredentials,
  type SessionCredentials,
} from "../../auth/request-credentials";
import { asUuid } from "../../realtime/envelope";

export type DriverSession = SessionCredentials & {
  readonly organizationId: string;
  readonly cacheNamespace: string;
};

declare global {
  interface Window {
    __paquetenviaDriverSession?: DriverSession;
  }
}

export const driverSessionChangedEvent = "paquetenvia:driver-session-changed";

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
