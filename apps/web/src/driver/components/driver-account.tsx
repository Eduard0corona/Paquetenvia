"use client";

import { useEffect, useState } from "react";
import { isBffAuthenticationEnabled } from "../../auth/auth-mode";
import { bootstrapBffSession } from "../../auth/bff-session-installation";
import { signOutInstalledSession } from "../../auth/logout";
import { accountLabel, readSessionAccount, type SessionAccount } from "../../auth/session-account";
import {
  driverSessionChangedEvent,
  readBrowserDriverSession,
  type DriverSession,
} from "../session/driver-session";
import styles from "./driver-stops.module.css";

/**
 * Display name and "Cerrar sesión" for the driver PWA header. Logging out needs the
 * network (POST /auth/logout); offline the button stays disabled so the in-memory session
 * and the offline queue are left exactly as they are.
 */
export function DriverAccount() {
  const [session, setSession] = useState<DriverSession | null>(null);
  const [account, setAccount] = useState<SessionAccount | null>(null);
  const [online, setOnline] = useState(true);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);

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
    const left = await signOutInstalledSession(
      current,
      window,
      window.location,
      process.env.NEXT_PUBLIC_AUTH_MODE,
    );
    if (left) return;
    setBusy(false);
    setFailed(true);
    // The local session objects were dropped; restore them from the still-valid cookie.
    if (isBffAuthenticationEnabled(process.env.NEXT_PUBLIC_AUTH_MODE)) void bootstrapBffSession(window);
  }

  return (
    <div className={styles.account}>
      <span className={styles.accountName}>{accountLabel(account)}</span>
      <button
        type="button"
        className={styles.accountLogout}
        disabled={busy || !online}
        onClick={() => void logout(session)}
      >
        {busy ? "Cerrando sesión…" : "Cerrar sesión"}
      </button>
      {!online && <span className={styles.accountHint}>Conéctate a internet para cerrar sesión.</span>}
      {failed && <span role="alert" className={styles.accountHint}>No fue posible cerrar la sesión. Intenta de nuevo.</span>}
    </div>
  );
}
