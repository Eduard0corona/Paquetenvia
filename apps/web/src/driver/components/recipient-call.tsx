"use client";

import { useEffect, useRef, useState } from "react";
import type { DriverVoiceApi } from "../api/voice-api";
import { recipientCallLabel } from "../contracts/voice";
import {
  isRecipientCallEligible,
  RecipientCallController,
  type RecipientCallViewState,
} from "../state/recipient-call-controller";
import styles from "./driver-stops.module.css";

const hiddenState: RecipientCallViewState = Object.freeze({ phase: "hidden", message: null });

/**
 * VOICE-001 "Llamar al destinatario" on the detail of a DELIVERY stop whose confirmed status is DELIVERING. The
 * server decides whether the call is available; the PWA never receives or shows a phone number. Online only: offline
 * the button is disabled with an explanation and nothing is queued.
 */
export function RecipientCall({
  api,
  orderId,
  stopType,
  confirmedStatus,
  offline,
}: Readonly<{
  api: DriverVoiceApi | null;
  orderId: string;
  stopType: string;
  confirmedStatus: string;
  offline: boolean;
}>) {
  const eligible = isRecipientCallEligible(stopType, confirmedStatus);
  const controllerRef = useRef<RecipientCallController | null>(null);
  const [state, setState] = useState<RecipientCallViewState>(hiddenState);
  const [browserOnline, setBrowserOnline] = useState(true);
  const connected = browserOnline && !offline;

  useEffect(() => {
    const refresh = () => setBrowserOnline(navigator.onLine);
    refresh();
    window.addEventListener("online", refresh);
    window.addEventListener("offline", refresh);
    return () => {
      window.removeEventListener("online", refresh);
      window.removeEventListener("offline", refresh);
    };
  }, []);

  useEffect(() => {
    if (!api) return;
    let active = true;
    const controller = new RecipientCallController(api, orderId);
    controllerRef.current = controller;
    const unsubscribe = controller.subscribe((next) => {
      if (active) setState(next);
    });
    return () => {
      active = false;
      unsubscribe();
      controller.dispose();
      if (controllerRef.current === controller) controllerRef.current = null;
      setState(hiddenState);
    };
  }, [api, orderId]);

  useEffect(() => {
    void controllerRef.current?.update(eligible, connected);
  }, [api, orderId, eligible, connected]);

  if (state.phase === "hidden") return null;
  const showButton = state.phase === "available" || state.phase === "calling" || state.phase === "offline";

  return (
    <section className={styles.actions} aria-labelledby={`recipient-call-${orderId}`}>
      <h3 id={`recipient-call-${orderId}`} className={styles.callHeading}>
        Contacto con el destinatario
      </h3>
      {showButton ? (
        <button
          type="button"
          disabled={state.phase !== "available" || !connected}
          onClick={() => void controllerRef.current?.call(connected)}
        >
          {state.phase === "calling" ? "Pidiendo la llamada…" : recipientCallLabel}
        </button>
      ) : null}
      <p className={styles.liveMessage} role="status" aria-live="polite">
        {state.message}
      </p>
    </section>
  );
}
