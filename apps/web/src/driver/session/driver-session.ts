import { asUuid } from "../../realtime/envelope";

export interface DriverSession {
  readonly organizationId: string;
  readonly cacheNamespace: string;
  getAccessToken(): string | Promise<string>;
}

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
  if (!session || typeof session.getAccessToken !== "function") {
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
