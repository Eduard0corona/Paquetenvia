"use client";

import { useEffect, useRef, useState } from "react";
import { resolveDriverApiBaseUrl } from "../api/api-base-url";
import { createDriverVoiceApi } from "../api/voice-api";
import { driverPhoneConsentText } from "../contracts/voice";
import {
  DriverPhoneController,
  driverPhoneMessages,
  type DriverPhoneViewState,
} from "../state/driver-phone-controller";
import type { DriverSession } from "../session/driver-session";
import styles from "./driver-stops.module.css";

const loadingState: DriverPhoneViewState = Object.freeze({
  phase: "loading",
  voiceCallsEnabled: false,
  registered: false,
  consentedAt: null,
  message: null,
});

/**
 * VOICE-001 "Cuenta": the driver registers their own mobile for masked calls with explicit consent, or removes it.
 * The stored number is never displayed (only that one is registered and since when); the field is cleared after
 * saving and nothing is kept in browser storage. Online only.
 */
export function DriverPhoneSettings({ session }: Readonly<{ session: DriverSession }>) {
  const controllerRef = useRef<DriverPhoneController | null>(null);
  const [state, setState] = useState<DriverPhoneViewState>(loadingState);
  const [online, setOnline] = useState(true);
  const [phone, setPhone] = useState("");
  const [consent, setConsent] = useState(false);

  useEffect(() => {
    const refresh = () => setOnline(navigator.onLine);
    refresh();
    window.addEventListener("online", refresh);
    window.addEventListener("offline", refresh);
    return () => {
      window.removeEventListener("online", refresh);
      window.removeEventListener("offline", refresh);
    };
  }, []);

  useEffect(() => {
    let active = true;
    const baseUrl = resolveDriverApiBaseUrl(
      window.location.origin,
      process.env.NEXT_PUBLIC_API_BASE_URL,
      process.env.NODE_ENV,
    );
    const controller = new DriverPhoneController(createDriverVoiceApi(baseUrl, session));
    controllerRef.current = controller;
    const unsubscribe = controller.subscribe((next) => {
      if (active) setState(next);
    });
    void controller.load(navigator.onLine);
    return () => {
      active = false;
      unsubscribe();
      controller.dispose();
      if (controllerRef.current === controller) controllerRef.current = null;
    };
  }, [session]);

  if (state.phase === "loading") {
    return <p className={styles.phoneNote}>Consultando tu cuenta…</p>;
  }

  if (state.phase === "hidden") {
    return <p className={styles.phoneNote}>Las llamadas con destinatarios no están activas en tu organización.</p>;
  }

  if (state.phase === "error") {
    return (
      <p className={styles.phoneNote} role="status">
        {state.message ?? driverPhoneMessages.loadFailed}
      </p>
    );
  }

  const busy = state.phase === "saving";

  async function save() {
    const saved = await controllerRef.current?.register(phone, consent, online);
    if (saved) {
      setPhone("");
      setConsent(false);
    }
  }

  return (
    <section className={styles.phonePanel} aria-labelledby="driver-phone-heading">
      <h2 id="driver-phone-heading">Mi celular para llamadas</h2>
      <p className={styles.phoneNote}>
        {state.registered
          ? `Celular registrado${state.consentedAt ? ` el ${formatDate(state.consentedAt)}` : ""}. No lo mostramos por seguridad.`
          : "No has registrado tu celular. Lo necesitas para llamar a los destinatarios sin que vean tu número."}
      </p>
      {state.voiceCallsEnabled ? (
        <form
          className={styles.phoneForm}
          onSubmit={(event) => {
            event.preventDefault();
            void save();
          }}
        >
          <label htmlFor="driver-phone-input">{state.registered ? "Cambiar mi celular" : "Mi celular"}</label>
          <input
            id="driver-phone-input"
            name="driver-phone"
            type="tel"
            inputMode="tel"
            autoComplete="off"
            maxLength={32}
            value={phone}
            disabled={busy || !online}
            onChange={(event) => setPhone(event.target.value)}
          />
          <label className={styles.phoneConsent}>
            <input
              type="checkbox"
              checked={consent}
              disabled={busy || !online}
              onChange={(event) => setConsent(event.target.checked)}
            />
            <span>{driverPhoneConsentText}</span>
          </label>
          <button type="submit" disabled={busy || !online}>
            {busy ? "Guardando…" : "Guardar celular"}
          </button>
        </form>
      ) : null}
      {state.registered ? (
        <button
          type="button"
          className={styles.accountLogout}
          disabled={busy || !online}
          onClick={() => void controllerRef.current?.remove(online)}
        >
          Borrar mi celular
        </button>
      ) : null}
      {!online ? <p className={styles.phoneNote}>{driverPhoneMessages.offline}</p> : null}
      <p className={styles.liveMessage} role="status" aria-live="polite">
        {state.message}
      </p>
    </section>
  );
}

function formatDate(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? ""
    : new Intl.DateTimeFormat("es-MX", { dateStyle: "medium", timeZone: "America/Mazatlan" }).format(date);
}
