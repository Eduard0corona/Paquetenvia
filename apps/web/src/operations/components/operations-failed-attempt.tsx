"use client";

import { useEffect, useState } from "react";
import { clientApiBaseUrl } from "../../lib/api-base-url";
import { DateTime } from "../../components/ui/date-time";
import { DescriptionList } from "../../components/ui/description-list";
import { createIncidentsApi } from "../api/incidents-api";
import type { OrderStatus } from "../contracts/operations-dashboard";
import { failedAttemptPendingNote } from "../contracts/failed-attempt";
import {
  readOperationsSession,
  subscribeToOperationsSession,
} from "../session/operations-session";
import {
  failedAttemptUnavailableMessage,
  loadFailedAttempt,
  type FailedAttemptState,
} from "../state/failed-attempt-loader";

/**
 * INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11 (AI-07 /ops/orders/:id failed_attempt): an
 * order in FAILED_ATTEMPT shows why the attempt failed, whether dispatch still has to decide
 * and until when to attend it. It is read again whenever the order changes (status or version);
 * the decision itself is taken in "Siguiente paso".
 */
export function OperationsFailedAttempt({
  orderId,
  status,
  version,
}: {
  readonly orderId: string;
  readonly status: OrderStatus;
  readonly version: number;
}) {
  const apiBaseUrl = clientApiBaseUrl();
  const [state, setState] = useState<FailedAttemptState>({ kind: "hidden" });

  useEffect(() => {
    if (status !== "FAILED_ATTEMPT") {
      const timer = window.setTimeout(() => setState({ kind: "hidden" }), 0);
      return () => clearTimeout(timer);
    }

    let abort = new AbortController();
    const start = () => {
      abort.abort();
      abort = new AbortController();
      const signal = abort.signal;
      const session = readOperationsSession();
      if (session === null) {
        setState({ kind: "hidden" });
        return;
      }
      setState({ kind: "loading" });
      void loadFailedAttempt(createIncidentsApi(apiBaseUrl, session), orderId, signal).then((next) => {
        if (!signal.aborted) setState(next);
      });
    };
    const timer = window.setTimeout(start, 0);
    const unsubscribe = subscribeToOperationsSession(start);
    return () => {
      clearTimeout(timer);
      unsubscribe();
      abort.abort();
    };
  }, [apiBaseUrl, orderId, status, version]);

  if (state.kind === "hidden") return null;

  return (
    <section aria-labelledby="failed-attempt-title" className="panel">
      <h2 id="failed-attempt-title">Intento fallido</h2>
      {state.kind === "loading" && <p role="status">Cargando el motivo…</p>}
      {state.kind === "unavailable" && (
        <p className="notice noticeWarn" role="status">
          {failedAttemptUnavailableMessage}
        </p>
      )}
      {state.kind === "ready" && (
        <>
          <DescriptionList
            label="Intento fallido"
            items={[
              { label: "Motivo", value: state.view.reasonLabel },
              { label: "Siguiente paso", value: state.view.nextStepLabel },
              { label: "Atender antes de", value: <DateTime value={state.view.attendBy} /> },
            ]}
          />
          {state.view.decisionPending && (
            <p className="notice noticeInfo" role="status">
              {failedAttemptPendingNote}
            </p>
          )}
        </>
      )}
    </section>
  );
}
