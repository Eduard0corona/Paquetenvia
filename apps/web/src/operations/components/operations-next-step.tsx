"use client";

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { clientApiBaseUrl } from "../../lib/api-base-url";
import { useConfirmDialog } from "../../components/confirm-dialog";
import { Field } from "../../components/ui/field";
import { createOrderActionsApi } from "../api/order-actions-api";
import {
  maximumReasonLength,
  nextStepActions,
  transitionConfirmation,
  type AllowedTransition,
  type NextStepAction,
} from "../contracts/order-transitions";
import {
  readOperationsSession,
  subscribeToOperationsSession,
} from "../session/operations-session";
import {
  initialOrderTransitionState,
  OrderTransitionController,
  type OrderTransitionState,
} from "../state/order-transition-controller";

/**
 * UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05: "Siguiente paso" on the order detail.
 *
 * Only the transitions the server returned in allowed_transitions are shown, and only the
 * ones this screen supports; there are no disabled placeholders and no status logic here.
 * Each one asks for a reason, then a confirmation, then calls transitionOrder with the
 * version just read; the order detail refetches REST afterwards.
 */
export function OperationsNextStep({
  orderId,
  publicId,
  version,
  allowedTransitions,
  onOrderChanged,
}: {
  readonly orderId: string;
  readonly publicId: string;
  readonly version: number;
  readonly allowedTransitions: readonly AllowedTransition[];
  readonly onOrderChanged: () => void;
}) {
  const apiBaseUrl = clientApiBaseUrl();
  const [state, setState] = useState<OrderTransitionState>(initialOrderTransitionState);
  const [selected, setSelected] = useState<NextStepAction | null>(null);
  const [reason, setReason] = useState("");
  const [acknowledged, setAcknowledged] = useState(false);
  const [inputError, setInputError] = useState<string | null>(null);
  const controllerRef = useRef<OrderTransitionController | null>(null);
  const onOrderChangedRef = useRef(onOrderChanged);
  const { confirm, dialog } = useConfirmDialog();
  const actions = nextStepActions(allowedTransitions);
  const offeredKey = `${version}|${actions.map((action) => action.target).join(",")}`;

  useEffect(() => {
    onOrderChangedRef.current = onOrderChanged;
  }, [onOrderChanged]);

  useEffect(() => {
    const start = () => {
      controllerRef.current?.dispose();
      controllerRef.current = null;
      const session = readOperationsSession();
      if (session === null) return;
      controllerRef.current = new OrderTransitionController(
        createOrderActionsApi(apiBaseUrl, session),
        orderId,
        setState,
        () => onOrderChangedRef.current(),
      );
    };
    start();
    const unsubscribe = subscribeToOperationsSession(start);
    return () => {
      unsubscribe();
      controllerRef.current?.dispose();
      controllerRef.current = null;
    };
  }, [apiBaseUrl, orderId]);

  // A new version or a new list closes the form: it was filled for the previous state.
  useEffect(() => {
    const timer = window.setTimeout(() => {
      setSelected(null);
      setReason("");
      setAcknowledged(false);
      setInputError(null);
    }, 0);
    return () => clearTimeout(timer);
  }, [offeredKey]);

  if (actions.length === 0 && state.message === null) return null;

  const open = (action: NextStepAction) => {
    setSelected(action);
    setReason("");
    setAcknowledged(false);
    setInputError(null);
  };

  return (
    <section aria-labelledby="next-step-title" className="panel opsNextStep">
      <h2 id="next-step-title">Siguiente paso</h2>
      {actions.length > 0 && (
        <div className="opsFormActions">
          {actions.map((action) => (
            <button
              key={action.target}
              type="button"
              className={action.danger ? "btn btnDanger" : "btn btnPrimary"}
              aria-pressed={selected?.target === action.target}
              onClick={() => open(action)}
            >
              {action.label}
            </button>
          ))}
        </div>
      )}
      {selected !== null && (
        <form
          className="opsNextStepForm"
          noValidate
          onSubmit={(event) => {
            event.preventDefault();
            if (state.busy) return;
            const trimmed = reason.trim();
            if (trimmed.length === 0) {
              setInputError("Escribe el motivo del cambio.");
              return;
            }
            if (trimmed.length > maximumReasonLength) {
              setInputError(`El motivo admite hasta ${maximumReasonLength} caracteres.`);
              return;
            }
            if (selected.needsRestrictedGoodsAcknowledgement && !acknowledged) {
              setInputError("Confirma que el envío no contiene artículos prohibidos.");
              return;
            }
            setInputError(null);
            const action = selected;
            confirm({
              ...transitionConfirmation(publicId, action),
              onConfirm: () =>
                void controllerRef.current?.submit(action, trimmed, version, acknowledged),
            });
          }}
        >
          <Field
            label={`Motivo: ${selected.label.toLowerCase()}`}
            hint="Queda en el historial de la orden. No escribas datos personales."
            error={inputError}
          >
            {(control) => (
              <textarea
                {...control}
                name="reason"
                rows={3}
                maxLength={maximumReasonLength}
                value={reason}
                onChange={(event) => {
                  setReason(event.currentTarget.value);
                  setInputError(null);
                }}
              />
            )}
          </Field>
          {selected.needsRestrictedGoodsAcknowledgement && (
            <label className="opsCheckbox">
              <input
                type="checkbox"
                name="restricted_goods_acknowledged"
                checked={acknowledged}
                onChange={(event) => {
                  setAcknowledged(event.currentTarget.checked);
                  setInputError(null);
                }}
              />
              <span>Confirmo que el envío no contiene artículos prohibidos.</span>
            </label>
          )}
          <div className="opsFormActions">
            <button className="btn btnSecondary" type="button" onClick={() => setSelected(null)}>
              Volver
            </button>
            <button
              className={selected.danger ? "btn btnDanger" : "btn btnPrimary"}
              type="submit"
              aria-disabled={state.busy}
            >
              {state.busy ? "Enviando…" : "Continuar"}
            </button>
          </div>
        </form>
      )}
      {state.message !== null && (
        <p
          className={state.success ? "notice noticeInfo" : state.stepUpHref === null ? "notice noticeWarn" : "notice noticeCrit"}
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
      {dialog}
    </section>
  );
}
