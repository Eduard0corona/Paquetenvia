"use client";

import { useEffect } from "react";
import { isBffAuthenticationEnabled } from "../auth-mode";
import { bootstrapBffSession } from "../bff-session-installation";
import { markSessionBootstrapSettled } from "../bootstrap-state";
import { watchDriverSessionConfirmation } from "../driver-session-confirmation";

/** The driver PWA's pages, where the phone may open the driver it remembers (DRV-SESSION-EXPIRY-OFFLINE-2026-10-19). */
export function isDriverPagePath(path: string): boolean {
  return path === "/driver" || path.startsWith("/driver/");
}

/** Re-establishes the in-memory session objects from the BFF cookie on every full page load. */
export function BffSessionBootstrap() {
  useEffect(() => {
    if (!isBffAuthenticationEnabled(process.env.NEXT_PUBLIC_AUTH_MODE)) {
      return;
    }
    const path = window.location.pathname;
    // /login runs its own bootstrap; public tracking never touches the private session.
    if (path === "/login" || path === "/track" || path.startsWith("/track/")) {
      return;
    }
    // DRV-SESSION-EXPIRY-OFFLINE-2026-10-19 (E3): on the driver pages a page load the server
    // cannot answer opens the remembered driver locally, and the session is checked again when
    // the connection returns until the server confirms it.
    const rememberDriver = isDriverPagePath(path);
    const initial = bootstrapBffSession(window, fetch, null, { rememberDriver }).finally(
      markSessionBootstrapSettled,
    );
    if (!rememberDriver) {
      return;
    }
    return watchDriverSessionConfirmation({
      host: window,
      initial,
      check: () => bootstrapBffSession(window, fetch, null, { rememberDriver }),
    });
  }, []);

  return null;
}
