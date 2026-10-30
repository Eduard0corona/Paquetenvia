"use client";

import { useEffect } from "react";
import { isRecipientUnsubscribePath } from "../security/security-headers";

/**
 * AI-07 /avisos/baja/:token: no PWA and no browser storage, so the e-mail unsubscribe page never registers the
 * service worker (whose install writes Cache Storage).
 */
export function registersServiceWorker(path: string): boolean {
  return !isRecipientUnsubscribePath(path);
}

export function ServiceWorkerRegistration() {
  useEffect(() => {
    if ("serviceWorker" in navigator && registersServiceWorker(window.location.pathname)) {
      void navigator.serviceWorker.register("/sw.js");
    }
  }, []);

  return null;
}
