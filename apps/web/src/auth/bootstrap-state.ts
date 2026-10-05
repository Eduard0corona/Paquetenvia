import { isBffAuthenticationEnabled } from "./auth-mode";

/**
 * Fired once the BFF bootstrap of a full page load has finished, whatever its outcome. The
 * app shell waits for it before concluding there is no session, so it never flashes the
 * signed-out state while the session cookie is still being exchanged.
 */
export const sessionBootstrapSettledEvent = "paquetenvia:session-bootstrap-settled";

declare global {
  interface Window {
    __paquetenviaSessionBootstrapSettled?: boolean;
  }
}

export function markSessionBootstrapSettled(): void {
  window.__paquetenviaSessionBootstrapSettled = true;
  window.dispatchEvent(new Event(sessionBootstrapSettledEvent));
}

/** Local Mock sessions (non-BFF) are installed by hand, so there is nothing to wait for. */
export function isSessionBootstrapSettled(): boolean {
  return (
    !isBffAuthenticationEnabled(process.env.NEXT_PUBLIC_AUTH_MODE) ||
    window.__paquetenviaSessionBootstrapSettled === true
  );
}
