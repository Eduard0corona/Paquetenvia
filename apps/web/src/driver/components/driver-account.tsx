"use client";

import { useEffect, useState } from "react";
import { isBffAuthenticationEnabled } from "../../auth/auth-mode";
import { bootstrapBffSession } from "../../auth/bff-session-installation";
import { signOutInstalledSession } from "../../auth/logout";
import { accountLabel, readSessionAccount, type SessionAccount } from "../../auth/session-account";
import { forgetDriverStopDetails } from "../cache/driver-stops-cache";
import {
  driverLogoutStartedEvent,
  driverSessionChangedEvent,
  readBrowserDriverSession,
  type DriverSession,
} from "../session/driver-session";
import { DriverPhoneSettings } from "./driver-phone-settings";
import styles from "./driver-stops.module.css";

/**
 * Display name and "Cerrar sesión" for the driver PWA header. Logging out needs the
 * network (POST /auth/logout); offline the button stays disabled so the in-memory session
 * and the offline queue are left exactly as they are. UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11:
 * once the server ended the session, the saved stop details are wiped from the phone before
 * leaving; the offline queue is kept. "Cuenta" (VOICE-001, AI-07 driver_account) opens the
 * driver's own phone for masked calls; it reads the API only once opened.
 */
export function DriverAccount() {
  const [session, setSession] = useState<DriverSession | null>(null);
  const [account, setAccount] = useState<SessionAccount | null>(null);
  const [online, setOnline] = useState(true);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);
  const [accountOpen, setAccountOpen] = useState(false);

  useEffect(() => {
    const refresh = () => {
      setSession(readBrowserDriverSession());
      setAccount(readSessionAccount());
    };
    const refreshOnline = () => setOnline(navigator.onLine);
    refresh();
    refreshOnline();
    window.addEventListener(driverSessionChangedEvent, refresh);
    window.addEventListener("online", refreshOnline);
    window.addEventListener("offline", refreshOnline);
    return () => {
      window.removeEventListener(driverSessionChangedEvent, refresh);
      window.removeEventListener("online", refreshOnline);
      window.removeEventListener("offline", refreshOnline);
    };
  }, []);

  if (session === null) return null;

  async function logout(current: DriverSession) {
    setBusy(true);
    setFailed(false);
    window.dispatchEvent(new Event(driverLogoutStartedEvent));
    const leave = {
      assign: (url: string) => {
        void forgetDriverStopDetails()
          .catch(() => undefined)
          .finally(() => window.location.assign(url));
      },
    };
    const left = await signOutInstalledSession(
      current,
      window,
      leave,
      process.env.NEXT_PUBLIC_AUTH_MODE,
    );
    if (left) return;
    setBusy(false);
    setFailed(true);
    // The local session objects were dropped; restore them from the still-valid cookie.
    if (isBffAuthenticationEnabled(process.env.NEXT_PUBLIC_AUTH_MODE)) {
      void bootstrapBffSession(window, fetch, null, { rememberDriver: true });
    }
  }

  // DRV-SESSION-EXPIRY-OFFLINE-2026-10-19 (E3): a remembered session has no
  // server session to end yet and asks the API for nothing, so "Cerrar sesión"
  // waits for the confirmed one and "Cuenta" is not offered.
  const remembered = session.remembered === true;

  return (
    <div className={styles.account}>
      <span className={styles.accountName}>{accountLabel(account)}</span>
      <button
        type="button"
        className={styles.accountLogout}
        disabled={busy || !online || remembered}
        onClick={() => void logout(session)}
      >
        {busy ? "Cerrando sesión…" : "Cerrar sesión"}
      </button>
      {!online && <span className={styles.accountHint}>Conéctate a internet para cerrar sesión.</span>}
      {online && remembered && (
        <span className={styles.accountHint}>Podrás cerrar sesión cuando se confirme tu sesión.</span>
      )}
      {failed && <span role="alert" className={styles.accountHint}>No fue posible cerrar la sesión. Intenta de nuevo.</span>}
      {remembered ? null : (
        <details
          className={styles.accountDetails}
          onToggle={(event) => setAccountOpen(event.currentTarget.open)}
        >
          <summary>Cuenta</summary>
          {accountOpen ? <DriverPhoneSettings session={session} /> : null}
        </details>
      )}
    </div>
  );
}
