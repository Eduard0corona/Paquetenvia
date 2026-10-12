"use client";

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { clientApiBaseUrl } from "../../lib/api-base-url";
import { useConfirmDialog } from "../../components/confirm-dialog";
import { Field } from "../../components/ui/field";
import { createAssignmentApi } from "../api/assignment-api";
import { createOperationsApi } from "../api/operations-api";
import {
  activeAssignmentsText,
  driverAssignmentConfirmation,
  ineligibilityText,
  sortForPicker,
  vehicleLabel,
} from "../contracts/assignable-driver";
import { canPerform, resolveActiveRole } from "../contracts/capabilities";
import { parseMxnToCents } from "../contracts/money";
import {
  readOperationsSession,
  subscribeToOperationsSession,
} from "../session/operations-session";
import {
  DriverAssignmentController,
  initialDriverAssignmentState,
  type DriverAssignmentState,
} from "../state/driver-assignment-controller";

/**
 * UI-PHASE2-DRIVER-PICKER-2026-10-05: "Asignar repartidor" on the order detail.
 *
 * Offered only while the order admits an assignment (READY_FOR_PICKUP or RESCHEDULED without
 * an active one) and only to the roles that hold listAssignableDrivers and assignDriver in the
 * selected organization; the backend and RLS remain the barrier. Drivers are picked from the
 * list by their DRV- reference, never by typing an id. The cost is typed in MXN and sent as
 * integer cents after a confirmation. After the server answers, the order detail refetches
 * REST; nothing is assumed locally.
 */
export function OperationsDriverAssignment({
  orderId,
  publicId,
  assignable,
  onOrderChanged,
}: {
  readonly orderId: string;
  readonly publicId: string;
  readonly assignable: boolean;
  readonly onOrderChanged: () => void;
}) {
  const apiBaseUrl = clientApiBaseUrl();
  const [allowed, setAllowed] = useState(false);
  const [state, setState] = useState<DriverAssignmentState>(initialDriverAssignmentState);
  const [selected, setSelected] = useState<string | null>(null);
  const [cost, setCost] = useState("");
  const [inputError, setInputError] = useState<string | null>(null);
  /** The confirmed outcome stays visible after the refetched order hides the picker. */
  const [done, setDone] = useState<string | null>(null);
  const controllerRef = useRef<DriverAssignmentController | null>(null);
  const onOrderChangedRef = useRef(onOrderChanged);
  const { confirm, dialog } = useConfirmDialog();

  useEffect(() => {
    onOrderChangedRef.current = onOrderChanged;
  }, [onOrderChanged]);

  useEffect(() => {
    let cancelled = false;
    const abort = new AbortController();

    const start = async () => {
      controllerRef.current?.dispose();
      controllerRef.current = null;
      setAllowed(false);
      setState(initialDriverAssignmentState);
      setSelected(null);
      if (!assignable) return;
      setDone(null);
      const session = readOperationsSession();
      if (session === null) return;
      let role: string | null;
      try {
        role = resolveActiveRole(
          await createOperationsApi(apiBaseUrl, session).organizationContexts(abort.signal),
          session.organizationId,
        );
      } catch {
        return;
      }
      if (
        cancelled ||
        readOperationsSession() !== session ||
        !canPerform(role, "listAssignableDrivers") ||
        !canPerform(role, "assignDriver")
      )
        return;
      const controller = new DriverAssignmentController(
        createAssignmentApi(apiBaseUrl, session),
        orderId,
        (next) => {
          if (cancelled) return;
          setState(next);
          if (next.success) setDone(next.message);
        },
        () => onOrderChangedRef.current(),
      );
      controllerRef.current = controller;
      setAllowed(true);
      await controller.load();
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
  }, [apiBaseUrl, orderId, assignable]);

  if (!allowed)
    return done === null ? null : (
      <p className="notice noticeInfo" role="status" aria-live="polite">
        {done}
      </p>
    );

  const drivers = sortForPicker(state.drivers);
  const anyEligible = drivers.some((driver) => driver.eligible);
  const busy = state.busy !== "idle";

  return (
    <section aria-labelledby="driver-assignment-title" className="panel opsDriverAssignment">
      <h2 id="driver-assignment-title">Asignar repartidor</h2>
      <p>
        Elige a un repartidor de tu flota y escribe el costo de la asignación. Solo puedes elegir a
        quien cumple hoy los requisitos para esta orden.
      </p>
      {state.kind === "loading" && <p className="live" aria-live="polite">Cargando repartidores…</p>}
      {state.kind === "ready" && drivers.length === 0 && (
        <p className="emptyState">
          <strong>No hay repartidores propios registrados.</strong>
          Puedes publicar una oferta externa desde el tablero.
        </p>
      )}
      {state.kind === "ready" && drivers.length > 0 && (
        <form
          className="opsDriverPicker"
          noValidate
          onSubmit={(event) => {
            event.preventDefault();
            if (busy) return;
            const driver = drivers.find((value) => value.driver_id === selected && value.eligible);
            const costCents = parseMxnToCents(cost);
            if (driver === undefined) {
              setInputError("Elige un repartidor disponible.");
              return;
            }
            if (costCents === null) {
              setInputError("Escribe un costo válido en pesos, por ejemplo 45.00.");
              return;
            }
            setInputError(null);
            confirm({
              ...driverAssignmentConfirmation(publicId, driver, costCents),
              onConfirm: () => void controllerRef.current?.assign(driver, costCents),
            });
          }}
        >
          <fieldset>
            <legend>Repartidor</legend>
            {!anyEligible && (
              <p className="notice noticeWarn" role="status">
                Ningún repartidor propio cumple hoy los requisitos para esta orden. Puedes publicar
                una oferta externa desde el tablero.
              </p>
            )}
            <ul className="opsDriverChoices">
              {drivers.map((driver) => {
                const hintId = `driver-${driver.driver_id}-hint`;
                return (
                  <li key={driver.driver_id} className={driver.eligible ? undefined : "opsDriverIneligible"}>
                    <label>
                      <input
                        type="radio"
                        name="driver"
                        value={driver.driver_id}
                        checked={selected === driver.driver_id}
                        disabled={!driver.eligible}
                        aria-describedby={hintId}
                        onChange={() => {
                          setSelected(driver.driver_id);
                          setInputError(null);
                        }}
                      />
                      <span>
                        <strong>{driver.driver_reference}</strong> · {vehicleLabel(driver.vehicle_type)} ·{" "}
                        {activeAssignmentsText(driver.active_assignment_count)}
                      </span>
                    </label>
                    <p id={hintId} className="fieldHint">
                      {driver.eligible
                        ? "Disponible para esta orden"
                        : `No disponible: ${ineligibilityText(driver.ineligibility_reasons)}`}
                    </p>
                  </li>
                );
              })}
            </ul>
            {state.nextCursor !== null && (
              <button
                type="button"
                className="btn btnSecondary"
                onClick={() => void controllerRef.current?.loadMore()}
              >
                {state.busy === "loading-more" ? "Cargando…" : "Cargar más repartidores"}
              </button>
            )}
          </fieldset>
          <Field
            label="Costo de la asignación (MXN)"
            hint="Lo que se pagará al repartidor por esta orden, por ejemplo 45.00."
            error={inputError}
          >
            {(control) => (
              <input
                {...control}
                name="cost"
                type="text"
                inputMode="decimal"
                autoComplete="off"
                placeholder="45.00"
                value={cost}
                onChange={(event) => {
                  setCost(event.currentTarget.value);
                  setInputError(null);
                }}
              />
            )}
          </Field>
          <div className="opsFormActions">
            <button className="btn btnPrimary" type="submit" aria-disabled={busy || !anyEligible}>
              {state.busy === "assigning" ? "Asignando…" : "Asignar repartidor"}
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
