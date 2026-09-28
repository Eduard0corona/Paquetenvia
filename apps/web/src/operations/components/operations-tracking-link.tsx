"use client";

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { createOperationsApi } from "../api/operations-api";
import {
  canManageTrackingLink,
  createTrackingLinkApi,
} from "../api/tracking-link-api";
import { formatMazatlanTime } from "../contracts/operations-formatters";
import {
  readOperationsSession,
  subscribeToOperationsSession,
} from "../session/operations-session";
import {
  TrackingLinkController,
  type TrackingLinkState,
} from "../state/tracking-link-controller";

/**
 * TRK-002-ISSUE-ENDPOINT: issue (or rotate) and revoke the order's public
 * tracking link. Offered only to DISPATCHER and PLATFORM_ADMIN members of the
 * organization that owns the order; the backend is still the barrier, and a
 * PLATFORM_ADMIN without a second factor is offered the MFA step-up. The link
 * is shown once, kept only in component memory and never persisted or logged.
 * Sending it to the customer is not available (GATE-004, GATE-007).
 */
export function OperationsTrackingLink({
  orderId,
  ownerOrganizationId,
}: {
  readonly orderId: string;
  readonly ownerOrganizationId: string;
}) {
  const apiBaseUrl =
    process.env.NEXT_PUBLIC_API_BASE_URL ??
    (typeof window === "undefined" ? "http://127.0.0.1" : window.location.origin);
  const [allowed, setAllowed] = useState(false);
  const [state, setState] = useState<TrackingLinkState>({
    kind: "idle",
    message: null,
    stepUpHref: null,
  });
  const controllerRef = useRef<TrackingLinkController | null>(null);

  useEffect(() => {
    let cancelled = false;
    const abort = new AbortController();

    const start = async () => {
      controllerRef.current?.dispose();
      controllerRef.current = null;
      setAllowed(false);
      setState({ kind: "idle", message: null, stepUpHref: null });
      const session = readOperationsSession();
      if (session === null) return;
      let contexts;
      try {
        contexts = await createOperationsApi(
          apiBaseUrl,
          session,
        ).organizationContexts(abort.signal);
      } catch {
        return;
      }
      if (
        cancelled ||
        readOperationsSession() !== session ||
        !canManageTrackingLink(
          contexts,
          session.organizationId,
          ownerOrganizationId,
        )
      )
        return;
      controllerRef.current = new TrackingLinkController(
        createTrackingLinkApi(apiBaseUrl, session),
        orderId,
        window.location.origin,
        (next) => {
          if (!cancelled) setState(next);
        },
      );
      setAllowed(true);
    };

    const initial = window.setTimeout(() => void start(), 0);
    const unsubscribe = subscribeToOperationsSession(() => void start());
    return () => {
      cancelled = true;
      abort.abort();
      clearTimeout(initial);
      unsubscribe();
      controllerRef.current?.dispose();
      controllerRef.current = null;
    };
  }, [apiBaseUrl, orderId, ownerOrganizationId]);

  if (!allowed) return null;

  const busy = state.kind === "busy";
  return (
    <section aria-labelledby="tracking-link-title" className="opsTrackingLink">
      <h2 id="tracking-link-title">Enlace de seguimiento</h2>
      <p>
        Genera un enlace público para esta orden. Se muestra una sola vez;
        generar otro revoca el anterior. El envío al cliente por WhatsApp o
        correo todavía no está disponible.
      </p>
      <div className="opsHeaderStatus">
        <button
          type="button"
          className="opsPrimary"
          disabled={busy}
          onClick={() => void controllerRef.current?.issue()}
        >
          {state.kind === "busy" && state.action === "issue"
            ? "Generando…"
            : "Generar o rotar enlace"}
        </button>
        <button
          type="button"
          className="opsSecondary"
          disabled={busy}
          onClick={() => {
            if (
              window.confirm(
                "¿Revocar el enlace de seguimiento? Quien lo tenga dejará de ver la orden.",
              )
            )
              void controllerRef.current?.revoke();
          }}
        >
          {state.kind === "busy" && state.action === "revoke"
            ? "Revocando…"
            : "Revocar enlace"}
        </button>
      </div>
      {state.kind === "shown" && (
        <div role="status" aria-live="polite">
          <label htmlFor="tracking-link-url">
            Copia el enlace ahora; no se volverá a mostrar.
          </label>
          <input
            id="tracking-link-url"
            type="text"
            readOnly
            autoComplete="off"
            spellCheck={false}
            value={state.url}
            onFocus={(event) => event.currentTarget.select()}
          />
          <p>Vence: {formatMazatlanTime(state.expiresAt)}</p>
          <div className="opsHeaderStatus">
            <button
              type="button"
              className="opsPrimary"
              onClick={() =>
                void controllerRef.current?.copy(
                  typeof navigator === "undefined"
                    ? undefined
                    : navigator.clipboard,
                )
              }
            >
              {state.copied ? "Copiado" : "Copiar enlace"}
            </button>
            <button
              type="button"
              className="opsSecondary"
              onClick={() => controllerRef.current?.hide()}
            >
              Ocultar
            </button>
          </div>
        </div>
      )}
      {state.kind === "idle" && state.message !== null && (
        <p
          className={state.stepUpHref === null ? undefined : "opsAlert"}
          role="status"
          aria-live="polite"
        >
          {state.message}{" "}
          {state.stepUpHref !== null && (
            <Link className="opsPrimary" href={state.stepUpHref}>
              Verificar identidad
            </Link>
          )}
        </p>
      )}
    </section>
  );
}
