"use client";

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { useConfirmDialog, type ConfirmRequest } from "../../components/confirm-dialog";
import { DateTime } from "../../components/ui/date-time";
import { clientApiBaseUrl } from "../../lib/api-base-url";
import { createOperationsApi } from "../api/operations-api";
import { canManageRecipientNotices, createRecipientNoticesApi } from "../api/recipient-notices-api";
import {
  recipientNoticeAttempts,
  recipientNoticeOrderFacts,
  recipientNoticeReason,
  recipientNoticeRowTitle,
  recipientNoticeStoppedLines,
  recipientNoticeTexts,
  stopRecipientNoticesConfirmation,
} from "../contracts/recipient-notices";
import { readOperationsSession, subscribeToOperationsSession } from "../session/operations-session";
import { RecipientNoticesController, type RecipientNoticesState } from "../state/recipient-notices-controller";

/**
 * NTF-RECIPIENT-NOTICE-PANEL-2026-10-30 (AI-08 NTF-007; AI-07 /ops/orders/:id recipient_notices): "Avisos al
 * destinatario". Mounted only when the selected organization owns the order and the member is a DISPATCHER or a
 * PLATFORM_ADMIN (the API still decides, and asks a PLATFORM_ADMIN without MFA for the step-up); an operator
 * organization never sees it. It is read with each read of the order, after a NotificationStatusChanged v2 notice of
 * the order (the screen gathers both, `revision`) and after "Detener avisos" (`onStopped`). It always shows, with the
 * line of T4 while notices are off and the quiet-hours line of T2, and the button is always offered (T3, T6 choice 13).
 */
export function OperationsRecipientNotices({
  orderId,
  publicId,
  ownerOrganizationId,
  revision,
  paused,
  onStopped,
}: {
  readonly orderId: string;
  readonly publicId: string;
  readonly ownerOrganizationId: string;
  /** Grows each time the screen asks for a new read of the panel. */
  readonly revision: number;
  /**
   * SEC-004: the screen's wait for the dashboard is running; the panel reads nothing of its own then and is read with
   * the order once the wait ends (T6 choice 9).
   */
  readonly paused: boolean;
  readonly onStopped: () => void;
}) {
  const apiBaseUrl = clientApiBaseUrl();
  const [allowed, setAllowed] = useState(false);
  const [state, setState] = useState<RecipientNoticesState>({ list: { kind: "loading" }, stop: { kind: "idle" } });
  const controllerRef = useRef<RecipientNoticesController | null>(null);
  const onStoppedRef = useRef(onStopped);
  const pausedRef = useRef(paused);
  const { confirm, dialog } = useConfirmDialog();

  useEffect(() => {
    onStoppedRef.current = onStopped;
    pausedRef.current = paused;
  }, [onStopped, paused]);

  useEffect(() => {
    let cancelled = false;
    const abort = new AbortController();

    const start = async () => {
      controllerRef.current?.dispose();
      controllerRef.current = null;
      setAllowed(false);
      setState({ list: { kind: "loading" }, stop: { kind: "idle" } });
      const session = readOperationsSession();
      if (session === null) return;
      let contexts;
      try {
        contexts = await createOperationsApi(apiBaseUrl, session).organizationContexts(abort.signal);
      } catch {
        return;
      }
      if (
        cancelled ||
        readOperationsSession() !== session ||
        !canManageRecipientNotices(contexts, session.organizationId, ownerOrganizationId)
      )
        return;
      controllerRef.current = new RecipientNoticesController(
        createRecipientNoticesApi(apiBaseUrl, session),
        orderId,
        (next) => {
          if (!cancelled) setState(next);
        },
        () => onStoppedRef.current(),
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

  // A new revision comes only from a read the screen admitted; the first read after the panel is allowed waits out a
  // running 429 wait, after which the screen reads the order and so the panel.
  useEffect(() => {
    if (allowed && !pausedRef.current) void controllerRef.current?.load();
  }, [allowed, revision]);

  if (!allowed) return null;
  return (
    <>
      <RecipientNoticesView
        state={state}
        onStop={(returnFocus) =>
          confirm({
            ...stopRecipientNoticesConfirmation(publicId),
            returnFocus,
            onConfirm: () => void controllerRef.current?.stop(),
          })
        }
      />
      {dialog}
    </>
  );
}

/** The panel itself, from the controller's state; it holds no state of its own. */
export function RecipientNoticesView({
  state,
  onStop,
}: {
  readonly state: RecipientNoticesState;
  readonly onStop: (returnFocus: ConfirmRequest["returnFocus"]) => void;
}) {
  const { list, stop } = state;
  return (
    <section aria-labelledby="recipient-notices-title" className="panel opsRecipientNotices">
      <h2 id="recipient-notices-title">{recipientNoticeTexts.title}</h2>
      <p className="opsSummaryNote">{recipientNoticeTexts.notActiveYet}</p>
      <p className="opsSummaryNote">{recipientNoticeTexts.quietHours}</p>
      {list.kind === "loaded" && (
        <>
          <ul>
            {recipientNoticeOrderFacts(list.list).map((fact) => (
              <li key={fact}>{fact}</li>
            ))}
          </ul>
          {list.list.items.length === 0 ? (
            <p>{recipientNoticeTexts.empty}</p>
          ) : (
            <ol className="opsTimeline">
              {list.list.items.map((notice) => {
                const attempts = recipientNoticeAttempts(notice);
                const reason = recipientNoticeReason(notice);
                return (
                  <li key={notice.notificationId}>
                    <strong>{recipientNoticeRowTitle(notice)}</strong>
                    <DateTime value={notice.updatedAt} />
                    {attempts !== null && <span>{attempts}</span>}
                    {reason !== null && <span>{reason}</span>}
                  </li>
                );
              })}
            </ol>
          )}
        </>
      )}
      {list.kind === "failed" && <FailureNotice message={list.message} stepUpHref={list.stepUpHref} />}
      <div className="opsFormActions">
        <button
          type="button"
          className="btn btnSecondary"
          disabled={stop.kind === "busy"}
          aria-busy={stop.kind === "busy"}
          onClick={(event) => onStop(event.currentTarget)}
        >
          {recipientNoticeTexts.stopButton}
        </button>
      </div>
      {stop.kind === "stopped" && (
        <p role="status" aria-live="polite">
          {recipientNoticeStoppedLines(stop.channels).join(" ")}
        </p>
      )}
      {stop.kind === "failed" && <FailureNotice message={stop.message} stepUpHref={stop.stepUpHref} />}
    </section>
  );
}

function FailureNotice({ message, stepUpHref }: { readonly message: string; readonly stepUpHref: string | null }) {
  return (
    <p className={stepUpHref === null ? undefined : "notice noticeCrit"} role="status" aria-live="polite">
      {message}{" "}
      {stepUpHref !== null && (
        <Link className="btn btnPrimary" href={stepUpHref}>
          Verificar identidad
        </Link>
      )}
    </p>
  );
}
