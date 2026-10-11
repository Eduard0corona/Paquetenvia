"use client";

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { useConfirmDialog } from "../../components/confirm-dialog";
import { Money } from "../../components/ui/money";
import { clientApiBaseUrl } from "../../lib/api-base-url";
import { createAssignmentApi } from "../api/assignment-api";
import { activeAssignmentsText, vehicleLabel, type AssignableDriver } from "../contracts/assignable-driver";
import {
  bulkAssignmentConfirmation,
  driverBlockText,
  intersectEligibility,
  parseBulkCosts,
  type BulkDriverChoice,
  type BulkEligibility,
  type BulkOrder,
} from "../contracts/bulk-assignment";
import { vatIncludedLabel } from "../contracts/create-order";
import { readOperationsSession } from "../session/operations-session";
import {
  BulkAssignmentController,
  initialBulkAssignmentState,
  type BulkAssignmentState,
  type BulkDriversLoad,
  type BulkRowResult,
} from "../state/bulk-assignment-controller";

/** The id of the panel heading; the inbox labels the panel's section with it. */
export const bulkAssignmentTitleId = "inbox-bulk-title";

/**
 * UI-PHASE3-INBOX-PRICE-REVIEW-BULK-2026-10-11 ("Costo por orden"): "Asignar seleccionadas…".
 *
 * The batch is the selection taken when the panel opened. Only drivers eligible for every order
 * can be picked; the others are listed with the orders that keep them out. The preview lists each
 * order with its own cost, typed in MXN and sent as integer cents (empty and required: nothing is
 * prefilled until the tariff table exists). Nothing is sent before the confirmation, which names
 * the driver, the number of orders and the sum of the costs; then each order is assigned on its
 * own and keeps its own result. The backend and RLS decide every assignment again.
 */
export function OperationsBulkAssignment({
  orders,
  returnUrl,
  onChanged,
  onRemove,
  onClose,
}: {
  readonly orders: readonly BulkOrder[];
  /** The inbox view the MFA step-up returns to. */
  readonly returnUrl: string;
  /** The inbox reads its queue and counts again. */
  readonly onChanged: () => void;
  /** An order left the batch: the inbox clears its checkbox too. */
  readonly onRemove: (orderId: string) => void;
  readonly onClose: () => void;
}) {
  const apiBaseUrl = clientApiBaseUrl();
  const [batch] = useState(orders);
  // The batch is listed at once; the controller then reads each order's drivers.
  const [state, setState] = useState<BulkAssignmentState>(() => ({
    ...initialBulkAssignmentState,
    orders: batch,
    drivers: Object.fromEntries(batch.map((order) => [order.orderId, { kind: "loading" } as const])),
  }));
  const [sessionMissing, setSessionMissing] = useState(false);
  const [driverId, setDriverId] = useState<string | null>(null);
  const [typed, setTyped] = useState<Readonly<Record<string, string>>>({});
  const [costErrors, setCostErrors] = useState<ReadonlyMap<string, string>>(new Map());
  const [formError, setFormError] = useState<string | null>(null);
  const controllerRef = useRef<BulkAssignmentController | null>(null);
  const onChangedRef = useRef(onChanged);
  const { confirm, dialog } = useConfirmDialog();

  useEffect(() => {
    onChangedRef.current = onChanged;
  }, [onChanged]);

  useEffect(() => {
    let controller: BulkAssignmentController | null = null;
    const start = () => {
      const session = readOperationsSession();
      if (session === null) {
        setSessionMissing(true);
        return;
      }
      controller = new BulkAssignmentController(
        createAssignmentApi(apiBaseUrl, session),
        setState,
        () => onChangedRef.current(),
        returnUrl,
      );
      controllerRef.current = controller;
      void controller.load(batch);
    };
    const initial = window.setTimeout(start, 0);
    return () => {
      clearTimeout(initial);
      controller?.dispose();
      controllerRef.current = null;
    };
  }, [apiBaseUrl, batch, returnUrl]);

  const started = state.phase === "running" || state.phase === "done";
  const eligibility = eligibilityOf(state);
  const chosen = eligibility?.eligible.find((choice) => choice.driverId === driverId) ?? null;
  const costs = parseBulkCosts(state.orders, typed);
  const count = state.orders.length;
  const sent = state.orders.filter((order) => isAnswered(state.results[order.orderId])).length;

  const remove = (orderId: string) => {
    controllerRef.current?.remove(orderId);
    onRemove(orderId);
    setCostErrors((errors) => new Map([...errors].filter(([id]) => id !== orderId)));
    if (count <= 1) onClose();
  };

  const review = () => {
    if (state.phase !== "ready" || eligibility === null) {
      setFormError("Espera a que se revisen los repartidores de todas las órdenes, o quita las que no se pudieron revisar.");
      return;
    }
    if (chosen === null) {
      setFormError("Elige un repartidor que pueda llevar todas las órdenes.");
      return;
    }
    const check = parseBulkCosts(state.orders, typed);
    if (!check.ok) {
      setCostErrors(check.errors);
      setFormError(check.message);
      return;
    }
    setCostErrors(new Map());
    setFormError(null);
    const run = { driver: chosen, costs: check.costs };
    confirm({
      ...bulkAssignmentConfirmation(run.driver, state.orders.length, check.totalCents),
      onConfirm: () => void controllerRef.current?.execute(run),
    });
  };

  return (
    <div className="opsBulkAssign">
      <div className="opsInboxAssignBar">
        <h2 id={bulkAssignmentTitleId}>
          Asignar {count === 1 ? "1 orden seleccionada" : `${count} órdenes seleccionadas`}
        </h2>
        <button type="button" className="btn btnSecondary" onClick={onClose} disabled={state.phase === "running"}>
          Cerrar
        </button>
      </div>
      <p>
        Elige un repartidor que pueda llevar todas las órdenes y escribe el costo de cada una. Nada se asigna
        hasta que revises la vista previa y confirmes.
      </p>
      {sessionMissing && (
        <p className="notice noticeWarn" role="status">
          Tu sesión no es válida; inicia sesión de nuevo.
        </p>
      )}
      {state.phase === "loading" && !sessionMissing && (
        <p className="live" aria-live="polite">
          Revisando qué repartidores pueden llevar cada orden…
        </p>
      )}

      {eligibility !== null && (
        <BulkDrivers
          eligibility={eligibility}
          selected={driverId}
          disabled={started}
          onSelect={(id) => {
            setDriverId(id);
            setFormError(null);
          }}
        />
      )}

      <div className="opsTableScroll" role="region" aria-labelledby="inbox-bulk-preview" tabIndex={0}>
        <table className="opsInboxTable opsBulkPreview">
          <caption id="inbox-bulk-preview">
            <strong>Vista previa:</strong>{" "}
            {count === 1 ? "1 orden" : `${count} órdenes`}
            {chosen === null ? "" : ` para ${chosen.driverReference}`}. Cada una se asigna por separado con su
            propio costo.
          </caption>
          <thead>
            <tr>
              <th scope="col">Guía</th>
              <th scope="col">Destino (zona)</th>
              <th scope="col" className="opsInboxTotal">
                Total ({vatIncludedLabel})
              </th>
              <th scope="col">Costo de la asignación (MXN)</th>
              <th scope="col">Resultado</th>
              {!started && (
                <th scope="col">
                  <span className="srOnly">Quitar del lote</span>
                </th>
              )}
            </tr>
          </thead>
          <tbody>
            {state.orders.map((order) => {
              const inputId = `bulk-cost-${order.orderId}`;
              const error = costErrors.get(order.orderId) ?? null;
              const load = state.drivers[order.orderId];
              const runCents = state.run?.costs.get(order.orderId);
              return (
                <tr key={order.orderId}>
                  <th scope="row">
                    <span className="opsInboxGuide">{order.publicId}</span>
                  </th>
                  <td>{order.zoneName ?? "Sin zona asignada"}</td>
                  {/* Integer MXN cents from the API, formatted by the shared Money component (no floating point). */}
                  <td className="opsInboxTotal">
                    <Money cents={order.totalCents} />
                  </td>
                  <td className="opsBulkCost">
                    {started && runCents !== undefined ? (
                      <Money cents={runCents} />
                    ) : (
                      <>
                        <label className="srOnly" htmlFor={inputId}>
                          Costo de la asignación de la orden {order.publicId} (MXN)
                        </label>
                        <input
                          id={inputId}
                          name={inputId}
                          type="text"
                          inputMode="decimal"
                          autoComplete="off"
                          placeholder="45.00"
                          value={typed[order.orderId] ?? ""}
                          aria-invalid={error === null ? undefined : true}
                          aria-describedby={error === null ? undefined : `${inputId}-error`}
                          disabled={started}
                          onChange={(event) => {
                            const value = event.currentTarget.value;
                            setTyped((current) => ({ ...current, [order.orderId]: value }));
                            setCostErrors((errors) => new Map([...errors].filter(([id]) => id !== order.orderId)));
                          }}
                        />
                        {error !== null && (
                          <p id={`${inputId}-error`} className="fieldError">
                            {error}
                          </p>
                        )}
                      </>
                    )}
                  </td>
                  <td>
                    {started ? (
                      <ResultText result={state.results[order.orderId]} driver={state.run?.driver ?? null} />
                    ) : (
                      <LoadText
                        load={load}
                        onRetry={() => void controllerRef.current?.retryLoad(order.orderId)}
                        canRetry={state.phase === "ready"}
                      />
                    )}
                  </td>
                  {!started && (
                    <td>
                      <button type="button" className="btnLink" onClick={() => remove(order.orderId)}>
                        Quitar<span className="srOnly"> la orden {order.publicId} del lote</span>
                      </button>
                    </td>
                  )}
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <p className="opsSummaryNote">
        Suma de los costos:{" "}
        {costs.ok ? <Money cents={costs.totalCents} strong /> : "escribe un costo válido para cada orden."}
      </p>

      {formError !== null && (
        <p className="notice noticeWarn" role="alert">
          {formError}
        </p>
      )}
      {state.phase === "running" && (
        <p className="live" aria-live="polite">
          Asignando: {sent} de {count} con respuesta…
        </p>
      )}
      {state.message !== null && (
        <p className={state.canRetry || state.stepUpHref !== null ? "notice noticeWarn" : "notice noticeInfo"} role="status">
          {state.message}{" "}
          {state.stepUpHref !== null && (
            <Link className="btn btnPrimary" href={state.stepUpHref}>
              Verificar identidad
            </Link>
          )}
        </p>
      )}
      <div className="opsFormActions">
        {!started && (
          <button
            type="button"
            className="btn btnPrimary"
            onClick={review}
            aria-disabled={state.phase !== "ready" || eligibility === null || eligibility.eligible.length === 0}
          >
            Revisar y asignar {count === 1 ? "1 orden" : `${count} órdenes`}
          </button>
        )}
        {state.phase === "done" && state.canRetry && (
          <button type="button" className="btn btnPrimary" onClick={() => void controllerRef.current?.retry()}>
            Reintentar las pendientes
          </button>
        )}
      </div>
      {dialog}
    </div>
  );
}

/** The drivers of a batch: only those eligible for every order can be picked. */
function BulkDrivers({
  eligibility,
  selected,
  disabled,
  onSelect,
}: {
  readonly eligibility: BulkEligibility;
  readonly selected: string | null;
  readonly disabled: boolean;
  readonly onSelect: (driverId: string) => void;
}) {
  const none = eligibility.eligible.length === 0 && eligibility.blocked.length === 0;
  return (
    <fieldset className="opsBulkDrivers" disabled={disabled}>
      <legend>Repartidor para todas las órdenes</legend>
      {none && (
        <p className="emptyState">
          <strong>No hay repartidores propios registrados.</strong>
          Puedes publicar una oferta externa desde el tablero.
        </p>
      )}
      {!none && eligibility.eligible.length === 0 && (
        <p className="notice noticeWarn" role="status">
          Ningún repartidor propio puede llevar todas las órdenes seleccionadas. Quita del lote las órdenes que lo
          impiden o asígnalas una por una.
        </p>
      )}
      {eligibility.eligible.length > 0 && (
        <ul className="opsDriverChoices">
          {eligibility.eligible.map((choice) => (
            <li key={choice.driverId}>
              <label>
                <input
                  type="radio"
                  name="bulk-driver"
                  value={choice.driverId}
                  checked={selected === choice.driverId}
                  onChange={() => onSelect(choice.driverId)}
                />
                <span>
                  <DriverLabel choice={choice} />
                </span>
              </label>
            </li>
          ))}
        </ul>
      )}
      {eligibility.blocked.length > 0 && (
        <div className="opsBulkBlocked">
          <p className="fieldHint">No pueden llevar todas las órdenes seleccionadas:</p>
          <ul>
            {eligibility.blocked.map((choice) => (
              <li key={choice.driverId}>
                <DriverLabel choice={choice} />: {driverBlockText(choice)}
              </li>
            ))}
          </ul>
        </div>
      )}
    </fieldset>
  );
}

function DriverLabel({ choice }: { readonly choice: BulkDriverChoice }) {
  return (
    <>
      <strong>{choice.driverReference}</strong> · {vehicleLabel(choice.vehicleType)} ·{" "}
      {activeAssignmentsText(choice.activeAssignmentCount)}
    </>
  );
}

function LoadText({
  load,
  canRetry,
  onRetry,
}: {
  readonly load: BulkDriversLoad | undefined;
  readonly canRetry: boolean;
  readonly onRetry: () => void;
}) {
  if (load === undefined || load.kind === "loading") return <span>Revisando repartidores…</span>;
  if (load.kind === "ready") return <span>Lista para asignar</span>;
  return (
    <span className="opsBulkProblem">
      {load.message}{" "}
      {load.retryable && canRetry && (
        <button type="button" className="btnLink" onClick={onRetry}>
          Reintentar
        </button>
      )}
    </span>
  );
}

function ResultText({
  result,
  driver,
}: {
  readonly result: BulkRowResult | undefined;
  readonly driver: BulkDriverChoice | null;
}) {
  if (result === undefined || result.kind === "pending") return <span>En espera</span>;
  if (result.kind === "sending") return <span>Enviando…</span>;
  if (result.kind === "assigned")
    return <strong className="opsBulkOk">Asignada{driver === null ? "" : ` a ${driver.driverReference}`}</strong>;
  if (result.kind === "not_sent") return <span className="opsBulkProblem">Sin enviar</span>;
  return <span className="opsBulkProblem">{result.message}</span>;
}

function isAnswered(result: BulkRowResult | undefined): boolean {
  return result !== undefined && result.kind !== "pending" && result.kind !== "sending";
}

/** The intersection once every order of the batch has its full driver list; null before that. */
function eligibilityOf(state: BulkAssignmentState): BulkEligibility | null {
  if (state.phase === "loading" || state.orders.length === 0) return null;
  const lists = new Map<string, readonly AssignableDriver[]>();
  for (const order of state.orders) {
    const load = state.drivers[order.orderId];
    if (load?.kind !== "ready") return null;
    lists.set(order.orderId, load.drivers);
  }
  return intersectEligibility(state.orders, lists);
}
