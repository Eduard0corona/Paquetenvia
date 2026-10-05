import { isBffAuthenticationEnabled } from "./auth-mode";
import {
  logoutBffSession,
  navigateAfterLogout,
  type BffLogoutResult,
  type NavigationTarget,
} from "./bff-session";
import { clearInstalledSessions, type SessionHost } from "./bff-session-installation";
import { isCookieSession, type SessionCredentials } from "./request-credentials";

/**
 * "Cerrar sesión" (AUTH-001-RP-INITIATED-LOGOUT), shared by /login, the app shell and the
 * driver account area: POST /auth/logout with the session CSRF token, always drop the
 * in-memory session objects, then leave to AuthCenter's end-session URL (which returns to
 * /login) or straight to /login. Returns false when the logout failed, so the caller can
 * restore its view and offer a retry.
 */
export async function endBffSession(
  csrfToken: string,
  host: SessionHost,
  location: NavigationTarget,
  fetcher: typeof fetch = fetch,
): Promise<boolean> {
  let result: BffLogoutResult = { ok: false };
  try {
    result = await logoutBffSession(csrfToken, fetcher);
  } finally {
    clearInstalledSessions(host);
  }
  if (!result.ok) return false;
  navigateAfterLogout(result, location);
  return true;
}

/**
 * Logout from a screen that holds an installed session. In BFF mode it is
 * {@link endBffSession} with the session's own CSRF token; a local Mock session (non-BFF
 * development) has no server session, so dropping the in-memory objects is the logout.
 */
export async function signOutInstalledSession(
  credentials: SessionCredentials,
  host: SessionHost,
  location: NavigationTarget,
  authMode: string | undefined,
  fetcher: typeof fetch = fetch,
): Promise<boolean> {
  if (isBffAuthenticationEnabled(authMode) && isCookieSession(credentials)) {
    let csrfToken = "";
    try {
      csrfToken = credentials.getCsrfToken();
    } catch {
      // An unreadable token fails the logout below; the local objects are still dropped.
    }
    return endBffSession(csrfToken, host, location, fetcher);
  }
  clearInstalledSessions(host);
  location.assign("/login");
  return true;
}
