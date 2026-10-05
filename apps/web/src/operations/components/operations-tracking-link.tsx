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
 * TRK-002-AUTO-LINK: show and copy the order's public tracking link. Nobody
 * revokes it (TRK-002-NO-REVOCATION): it lives until 24 hours after the order
 * reaches a final state.
 * Every order gets its link when it is created; "Ver enlace" reads it with
 * get-or-create, which always returns the same link (never a rotation).
 * Offered only to DISPATCHER and PLATFORM_ADMIN members of the organization
 * that owns the order; the backend is still the barrier, and a PLATFORM_ADMIN
 * without a second factor is offered the MFA step-up. The link is kept only in
 * component memory while shown and never persisted or logged. Sending it to
 * the customer is not available (GATE-004, GATE-007).
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
    <section aria-labelledby="tracking-link-title" className="panel opsTrackingLink">
      <h2 id="tracking-link-title">Enlace de seguimiento</h2>
      <p>
        La orden tiene su enlace público desde que se creó; siempre es el mismo.
        Funciona mientras la orden está en curso y 24 horas después de
        entregarse, devolverse o cancelarse. El envío al cliente por WhatsApp o
        correo todavía no está disponible.
      </p>
      <div className="opsHeaderStatus">
        <button
          type="button"
          className="btn btnPrimary"
          disabled={busy}
          onClick={() => void controllerRef.current?.show()}
        >
          {busy ? "Cargando…" : "Ver enlace"}
        </button>
      </div>
      {state.kind === "shown" && (
        <div role="status" aria-live="polite">
          <label htmlFor="tracking-link-url">Enlace público de la orden</label>
          <input
            id="tracking-link-url"
            type="text"
            readOnly
            autoComplete="off"
            spellCheck={false}
            value={state.url}
            onFocus={(event) => event.currentTarget.select()}
          />
          <p>
            {state.validUntil === null
              ? "Vigente mientras la orden esté en curso."
              : `Vigente hasta: ${formatMazatlanTime(state.validUntil)}`}
          </p>
          <div className="opsHeaderStatus">
            <button
              type="button"
              className="btn btnPrimary"
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
              className="btn btnSecondary"
              onClick={() => controllerRef.current?.hide()}
            >
              Ocultar
            </button>
          </div>
        </div>
      )}
      {state.kind === "idle" && state.message !== null && (
        <p
          className={state.stepUpHref === null ? undefined : "notice noticeCrit"}
          role="status"
          aria-live="polite"
        >
          {state.message}{" "}
          {state.stepUpHref !== null && (
            <Link className="btn btnPrimary" href={state.stepUpHref}>
              Verificar identidad
            </Link>
          )}
        </p>
      )}
    </section>
  );
}
