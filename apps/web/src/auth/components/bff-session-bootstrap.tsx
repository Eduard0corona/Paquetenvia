"use client";

import { useEffect } from "react";
import { isBffAuthenticationEnabled } from "../auth-mode";
import { bootstrapBffSession } from "../bff-session-installation";
import { markSessionBootstrapSettled } from "../bootstrap-state";

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
    void bootstrapBffSession(window).finally(markSessionBootstrapSettled);
  }, []);

  return null;
}
