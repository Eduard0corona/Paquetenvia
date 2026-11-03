"use client";

import { useEffect, useRef, useState } from "react";
import {
  documentWarning,
  driverDocumentTypeLabels,
  slotDateText,
  slotStatusText,
} from "../../operations/contracts/driver-document";
import { resolveDriverApiBaseUrl } from "../api/api-base-url";
import { createDriverDocumentsApi } from "../api/driver-documents-api";
import { driverDocumentsFixedLine } from "../contracts/driver-documents";
import type { DriverSession } from "../session/driver-session";
import {
  DriverDocumentsController,
  type DriverDocumentsViewState,
} from "../state/driver-documents-controller";
import styles from "./driver-stops.module.css";

const loadingState: DriverDocumentsViewState = Object.freeze({
  phase: "loading",
  documents: [],
  readAt: null,
  refreshing: false,
  message: null,
});

/**
 * DRV-DOC-001-IN-PERSON-2026-11-02 (AI-07 driver_account documents; Y4, Y6, Y8): "Cuenta > Documentos", the driver's
 * own documents, read-only - one line per document its vehicle requires with its status, its date and, from 30 days
 * before, «Vence en N días» or «Vencido», as the panel shows them. Read when "Cuenta" opens and when the driver taps
 * "Actualizar documentos"; no message is sent (Y8). Nothing is shown while documents are off. Online only; nothing is
 * kept in browser storage or the offline queue.
 */
export function DriverDocumentsList({ session }: Readonly<{ session: DriverSession }>) {
  const controllerRef = useRef<DriverDocumentsController | null>(null);
  const [state, setState] = useState<DriverDocumentsViewState>(loadingState);
  const [online, setOnline] = useState(true);

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
    const controller = new DriverDocumentsController(createDriverDocumentsApi(baseUrl, session));
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

  if (state.phase === "hidden") return null;

  if (state.phase === "loading") {
    return <p className={styles.phoneNote}>Consultando tus documentos…</p>;
  }

  const readAt = state.readAt;
  return (
    <section className={styles.phonePanel} aria-labelledby="driver-documents-heading">
      <h2 id="driver-documents-heading">Documentos</h2>
      <p className={styles.phoneNote}>{driverDocumentsFixedLine}</p>
      {state.phase === "ready" &&
        (state.documents.length === 0 ? (
          <p className={styles.phoneNote}>Tu vehículo no requiere documentos.</p>
        ) : (
          <ul className={styles.documentsList}>
            {state.documents.map((slot) => {
              const date = slotDateText(slot);
              const warning = slot.document === null || readAt === null ? null : documentWarning(slot.document, readAt);
              return (
                <li key={slot.document_type}>
                  <span className={styles.documentsType}>{driverDocumentTypeLabels[slot.document_type]}</span>
                  <span>{slotStatusText(slot)}</span>
                  {date !== null && <span>{date}</span>}
                  {warning !== null && <span className={styles.documentsWarning}>{warning.text}</span>}
                </li>
              );
            })}
          </ul>
        ))}
      <button
        type="button"
        className={styles.accountLogout}
        disabled={state.refreshing || !online}
        onClick={() => void controllerRef.current?.load(navigator.onLine)}
      >
        {state.refreshing ? "Actualizando…" : "Actualizar documentos"}
      </button>
      <p className={styles.liveMessage} role="status" aria-live="polite">
        {state.message ?? (online ? "" : "Necesitas conexión a internet para consultar tus documentos.")}
      </p>
    </section>
  );
}
