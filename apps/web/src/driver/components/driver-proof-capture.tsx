"use client";

import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import {
  DriverProofPreview,
  driverProofPreviewProblemMessage,
  driverProofPreviewStatus,
} from "../proofs/proof-preview";
import styles from "./driver-stops.module.css";

/**
 * Proof photo step of a driver stop (UI-001 phase 3): take the photo, check it in
 * "Vista previa de la foto", then "Repetir" or "Usar esta foto". Only "Usar esta foto"
 * calls `onUse`, which queues the action with that same photo exactly as before.
 */
export function DriverProofCapture({
  orderId,
  actionLabel,
  onUse,
}: Readonly<{
  orderId: string;
  actionLabel: string;
  onUse: (photo: Blob) => Promise<boolean>;
}>) {
  const [preview] = useState(() => new DriverProofPreview());
  const state = useSyncExternalStore(
    preview.subscribe,
    preview.getSnapshot,
    preview.getSnapshot,
  );
  const inputRef = useRef<HTMLInputElement>(null);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const shownPhase = useRef(state.phase);
  const [unrenderableUrl, setUnrenderableUrl] = useState<string | null>(null);

  // Unmount (the next step replaced this one): revoke the URL and forget the photo.
  useEffect(() => () => preview.release(), [preview]);

  // Focus follows the step so it is never lost on a hidden control.
  useEffect(() => {
    if (shownPhase.current === state.phase) return;
    shownPhase.current = state.phase;
    if (state.phase === "preview") headingRef.current?.focus();
    else if (state.phase === "capture") inputRef.current?.focus();
  }, [state.phase]);

  const inputId = `proof-${orderId}`;
  const actionId = `proof-action-${orderId}`;
  const problemId = `proof-error-${orderId}`;
  const headingId = `proof-preview-heading-${orderId}`;
  const problem =
    state.phase === "capture" || state.phase === "preview"
      ? state.problem
      : null;

  const retake = () => {
    preview.retake();
    const input = inputRef.current;
    if (!input) return;
    // Forget the old pick so the same file can be chosen again, and reopen the camera
    // where the browser allows it; otherwise the input shows again and takes focus.
    input.value = "";
    input.click();
  };

  return (
    <>
      <p id={actionId} className={styles.proofAction}>
        {actionLabel}
      </p>
      <div className={styles.proofCapture} hidden={state.phase !== "capture"}>
        <label htmlFor={inputId}>
          Foto de evidencia (JPEG o PNG, máximo 10 MiB)
        </label>
        <input
          ref={inputRef}
          id={inputId}
          type="file"
          accept="image/jpeg,image/png"
          capture="environment"
          aria-describedby={problem ? problemId : undefined}
          aria-invalid={state.phase === "capture" && problem ? true : undefined}
          onChange={(event) => preview.select(event.currentTarget.files)}
        />
      </div>
      {state.phase === "preview" ? (
        <section className={styles.proofPreview} aria-labelledby={headingId}>
          <h3 id={headingId} ref={headingRef} tabIndex={-1}>
            Vista previa de la foto
          </h3>
          {unrenderableUrl === state.objectUrl ? (
            <p>No se puede mostrar la vista previa de esta foto.</p>
          ) : (
            // eslint-disable-next-line @next/next/no-img-element -- a local blob: URL that next/image cannot load
            <img
              className={styles.proofImage}
              src={state.objectUrl}
              alt="Foto de evidencia tomada"
              onError={() => setUnrenderableUrl(state.objectUrl)}
            />
          )}
          <div className={styles.proofPreviewActions}>
            <button
              type="button"
              className={styles.retakeButton}
              onClick={retake}
            >
              Repetir
            </button>
            <button
              type="button"
              aria-describedby={actionId}
              onClick={() => void preview.use(onUse)}
            >
              Usar esta foto
            </button>
          </div>
        </section>
      ) : null}
      {problem ? (
        <p id={problemId} className={styles.proofProblem} role="alert">
          {driverProofPreviewProblemMessage(problem)}
        </p>
      ) : null}
      <p className={styles.liveMessage} aria-live="polite">
        {driverProofPreviewStatus(state)}
      </p>
    </>
  );
}
