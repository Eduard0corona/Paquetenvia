"use client";

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { useConfirmDialog } from "../../components/confirm-dialog";
import { clientApiBaseUrl } from "../../lib/api-base-url";
import { createBulkRouteApi } from "../api/bulk-route-api";
import type { BulkOrder } from "../contracts/bulk-assignment";
import {
  bulkRouteConfirmation,
  bulkRouteGoneMessage,
  bulkRouteMixedCitiesMessage,
  bulkRoutePreviewText,
  createRouteOfferText,
  draftRouteChoices,
  draftRouteOrderText,
  noTodayRouteText,
  ordersText,
  routeStopConflictMessages,
  stopCountText,
  type BulkRouteDriver,
  type DraftRouteChoice,
} from "../contracts/bulk-route";
import { readOperationsSession } from "../session/operations-session";
import {
  BulkRouteController,
  initialBulkRouteState,
  sendableRouteOrders,
  type BulkRouteCreation,
  type BulkRouteNotice,
  type BulkRouteRowResult,
  type BulkRoutesLoad,
  type BulkRouteState,
} from "../state/bulk-route-controller";

/** The id of the step heading; the step's section is labelled with it. */
export const bulkRouteTitleId = "inbox-bulk-route-title";
const previewCaptionId = "inbox-bulk-route-preview";
/** The preview region; focus lands on it once "Crear ruta nueva" created the route. */
const previewRegionId = "inbox-bulk-route-preview-region";
const createOfferId = "inbox-bulk-route-create-offer";

/**
 * UI-PHASE3-INBOX-BULK-ADD-TO-ROUTE-2026-10-11 ("Al terminar el lote (Recommended)"): "Agregar
 * estas órdenes a una ruta…", offered by a finished "Asignar seleccionadas…" with the orders it
 * assigned and its driver, never with other orders or another driver.
 *
 * Only the driver's DRAFT routes can be chosen, named by date and current stops (no identifier,
 * no amount). The preview lists the orders in the order they are added; nothing is sent before the
 * confirmation, which names the route, the driver and the number of orders. Then each order is
 * added on its own and keeps its own result. The backend and RLS decide every stop again.
 *
 * UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11 ("Sí, en el paso (Recommended)"): when the driver has
 * no DRAFT route for today and every order of the batch is in one city, "Crear ruta nueva" creates
 * the DRAFT route for that driver, in that city and for today (Mazatlán), and the step goes on to
 * the preview with it selected. Orders in different cities are sent to Rutas, where routes are also
 * created.
 *
 * UI-PHASE3-ROUTES-DAILY-2026-10-12 ("Ruta por día (Recommended)"): today's routes are listed first
 * and marked "hoy"; when the driver has only older DRAFT routes, they stay listed next to "Crear
 * ruta nueva", so an older route can still be chosen.
 */
export function OperationsBulkRoute({
  orders,
  driver,
  returnUrl,
  onChanged,
  onCancel,
  onRunningChange,
}: {
  readonly orders: readonly BulkOrder[];
  readonly driver: BulkRouteDriver;
  /** The inbox view the MFA step-up returns to. */
  readonly returnUrl: string;
  /** The inbox reads its queue and counts again. */
  readonly onChanged: () => void;
  /** Leaves the step before anything was sent. */
  readonly onCancel: () => void;
  /** The batch panel cannot be closed while a run is sending. */
  readonly onRunningChange: (running: boolean) => void;
}) {
  const apiBaseUrl = clientApiBaseUrl();
  const [batch] = useState(orders);
  const [routeDriver] = useState(driver);
  const [state, setState] = useState<BulkRouteState>(() => initialBulkRouteState(batch));
  const [sessionMissing, setSessionMissing] = useState(false);
  const [routeId, setRouteId] = useState<string | null>(null);
  const [formError, setFormError] = useState<string | null>(null);
  const controllerRef = useRef<BulkRouteController | null>(null);
  const sectionRef = useRef<HTMLElement>(null);
  const onChangedRef = useRef(onChanged);
  const onRunningChangeRef = useRef(onRunningChange);
  const { confirm, dialog } = useConfirmDialog();
  const creating = state.creation.kind === "creating";
  // A route being created holds the batch panel open too: closing it would cut the request.
  const running = state.phase === "running" || creating;

  useEffect(() => {
    onChangedRef.current = onChanged;
    onRunningChangeRef.current = onRunningChange;
  }, [onChanged, onRunningChange]);

  useEffect(() => {
    onRunningChangeRef.current(running);
  }, [running]);

  useEffect(() => {
    sectionRef.current?.focus();
  }, []);

  // The created route is selected: focus moves to the preview it now names.
  useEffect(() => {
    if (state.createdRouteId !== null) document.getElementById(previewRegionId)?.focus();
  }, [state.createdRouteId]);

  useEffect(() => {
    let controller: BulkRouteController | null = null;
    const start = () => {
      const session = readOperationsSession();
      if (session === null) {
        setSessionMissing(true);
        return;
      }
      controller = new BulkRouteController(
        createBulkRouteApi(apiBaseUrl, session),
        routeDriver,
        batch,
        setState,
        () => onChangedRef.current(),
        returnUrl,
      );
      controllerRef.current = controller;
      void controller.loadRoutes();
    };
    const initial = window.setTimeout(start, 0);
    return () => {
      clearTimeout(initial);
      controller?.dispose();
      controllerRef.current = null;
    };
  }, [apiBaseUrl, batch, routeDriver, returnUrl]);

  const reference = routeDriver.driverReference;
  const choices = state.routes.kind === "ready" ? draftRouteChoices(state.routes.routes, state.routes.today) : [];
  const fixedId = state.route?.id ?? null;
  // The route "Crear ruta nueva" just created is the selected one until the person picks another.
  const selectedId = routeId ?? state.createdRouteId;
  const chosen = choices.find((choice) => choice.route.id === (fixedId ?? selectedId)) ?? null;
  const choosing = state.phase === "choosing";
  const started = Object.keys(state.results).length > 0;
  const sendable = sendableRouteOrders(state);
  const count = state.orders.length;
  const answered = state.orders.filter((order) => isAnswered(state.results[order.orderId])).length;
  const added = state.orders.filter((order) => state.results[order.orderId]?.kind === "added").length;
  const reload = () => {
    setFormError(null);
    void controllerRef.current?.loadRoutes();
  };

  const review = () => {
    if (!choosing || state.routes.kind !== "ready") {
      setFormError(`Espera a que se carguen las rutas en borrador de ${reference}.`);
      return;
    }
    if (chosen === null) {
      setFormError(fixedId === null ? `Elige una ruta en borrador de ${reference}.` : bulkRouteGoneMessage);
      return;
    }
    setFormError(null);
    const choice = chosen;
    confirm({
      ...bulkRouteConfirmation(choice, reference, sendable.length),
      onConfirm: () => void controllerRef.current?.execute(choice.route.id),
    });
  };

  return (
    <section ref={sectionRef} tabIndex={-1} className="opsBulkRoute" aria-labelledby={bulkRouteTitleId}>
      <div className="opsInboxAssignBar">
        <h3 id={bulkRouteTitleId}>
          Agregar {ordersText(count)} de {reference} a una ruta
        </h3>
        {choosing && !started && (
          <button type="button" className="btn btnSecondary" onClick={onCancel} disabled={creating}>
            Cancelar
          </button>
        )}
      </div>
      <p>
        Solo van las órdenes que este lote asignó a {reference}, a una ruta en borrador de ese mismo repartidor. Nada
        se agrega hasta que revises la vista previa y confirmes.
      </p>
      {sessionMissing && (
        <p className="notice noticeWarn" role="status">
          Tu sesión no es válida; inicia sesión de nuevo.
        </p>
      )}

      {!sessionMissing &&
        (state.route === null ? (
          <BulkRouteChoices
            driverReference={reference}
            routes={state.routes}
            choices={choices}
            selected={selectedId}
            disabled={!choosing}
            creation={state.creation}
            notice={state.creationNotice}
            onSelect={(id) => {
              setRouteId(id);
              setFormError(null);
            }}
            onReload={reload}
            onCreate={() => {
              // The route about to be created becomes the selected one, not an older route picked before.
              setRouteId(null);
              setFormError(null);
              void controllerRef.current?.createRoute();
            }}
          />
        ) : (
          <BulkRouteFixed
            route={state.route}
            choice={chosen}
            routes={state.routes}
            canReload={choosing}
            onReload={reload}
          />
        ))}

      <BulkRoutePreview
        orders={state.orders}
        driverReference={reference}
        choice={chosen}
        phase={state.phase}
        results={state.results}
      />

      {formError !== null && (
        <p className="notice noticeWarn" role="alert">
          {formError}
        </p>
      )}
      {running && (
        <p className="live" aria-live="polite">
          Agregando: {answered} de {count} con respuesta… No salgas de la bandeja hasta que termine.
        </p>
      )}
      {state.message !== null && (
        <p className={added < count || state.stepUpHref !== null ? "notice noticeWarn" : "notice noticeInfo"} role="status">
          {state.message}{" "}
          {state.stepUpHref !== null && (
            <Link className="btn btnPrimary" href={state.stepUpHref}>
              Verificar identidad
            </Link>
          )}
        </p>
      )}
      <div className="opsFormActions">
        {choosing && !sessionMissing && sendable.length > 0 && (
          <button
            type="button"
            className="btn btnPrimary"
            onClick={review}
            aria-disabled={state.routes.kind !== "ready" || chosen === null}
          >
            Revisar y agregar {ordersText(sendable.length)}
          </button>
        )}
        {state.phase === "done" && state.canRetry && (
          <button type="button" className="btn btnPrimary" onClick={() => void controllerRef.current?.retry()}>
            Reintentar las pendientes
          </button>
        )}
        {state.phase === "done" && added > 0 && state.route !== null && (
          <Link className="btn btnSecondary" href={`/ops/routes?routeId=${encodeURIComponent(state.route.id)}`}>
            Ver la ruta en Rutas
          </Link>
        )}
      </div>
      {dialog}
    </section>
  );
}

/**
 * The driver's DRAFT routes before the first run: only they can be chosen, by date and current
 * stops, today's first (Mazatlán, marked "hoy") and then newest first. When the driver has none for
 * today, "Crear ruta nueva" is offered if every order of the batch is in one city
 * (UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11, UI-PHASE3-ROUTES-DAILY-2026-10-12), below the older
 * routes when there are some; routes are also created in Rutas, opened in another tab.
 */
export function BulkRouteChoices({
  driverReference,
  routes,
  choices,
  selected,
  disabled,
  creation,
  notice,
  onSelect,
  onReload,
  onCreate,
}: {
  readonly driverReference: string;
  readonly routes: BulkRoutesLoad;
  readonly choices: readonly DraftRouteChoice[];
  readonly selected: string | null;
  readonly disabled: boolean;
  readonly creation: BulkRouteCreation;
  readonly notice: BulkRouteNotice | null;
  readonly onSelect: (routeId: string) => void;
  readonly onReload: () => void;
  readonly onCreate: () => void;
}) {
  const creating = creation.kind === "creating";
  const offered = creation.kind === "offered" || creating;
  return (
    <fieldset className="opsBulkDrivers" disabled={disabled}>
      <legend>Ruta en borrador de {driverReference}</legend>
      {routes.kind === "loading" && (
        <p className="live" aria-live="polite">
          Buscando las rutas en borrador de {driverReference}…
        </p>
      )}
      {routes.kind === "failed" && (
        <p className="notice noticeWarn" role="status">
          {routes.message}{" "}
          {routes.stepUpHref !== null && (
            <Link className="btn btnPrimary" href={routes.stepUpHref}>
              Verificar identidad
            </Link>
          )}
        </p>
      )}
      {choices.length > 0 && (
        <>
          <ul className="opsDriverChoices">
            {choices.map((choice) => (
              <li key={choice.route.id}>
                <label>
                  <input
                    type="radio"
                    name="bulk-route"
                    value={choice.route.id}
                    checked={selected === choice.route.id}
                    onChange={() => onSelect(choice.route.id)}
                  />
                  <span>{choice.label}</span>
                </label>
              </li>
            ))}
          </ul>
          <p className="fieldHint">{draftRouteOrderText(choices)}</p>
        </>
      )}
      {routes.kind === "ready" && (choices.length === 0 || creation.kind !== "none") && (
        <BulkRouteNoDraft
          driverReference={driverReference}
          olderRoutes={choices.length > 0}
          creation={creation}
          onCreate={onCreate}
        />
      )}
      {notice !== null && (
        <p className={notice.tone === "info" ? "notice noticeInfo" : "notice noticeWarn"} role="status">
          {notice.text}{" "}
          {notice.stepUpHref !== null && (
            <Link className="btn btnPrimary" href={notice.stepUpHref}>
              Verificar identidad
            </Link>
          )}
        </p>
      )}
      <p className="fieldHint">
        {offered ? "Las rutas también se crean en Rutas." : "Las rutas se crean en Rutas."}{" "}
        <Link href="/ops/routes" target="_blank" rel="noopener noreferrer">
          Abrir Rutas en otra pestaña
        </Link>{" "}
        y, cuando la ruta exista, elige «Buscar rutas de nuevo».
      </p>
      <p>
        <button type="button" className="btnLink" onClick={onReload} disabled={routes.kind === "loading" || creating}>
          Buscar rutas de nuevo
        </button>
      </p>
    </fieldset>
  );
}

/**
 * The driver has no DRAFT route for today (the only place "Crear ruta nueva" appears), either no
 * DRAFT route at all or only older ones listed above: the button when the orders share one city,
 * why not when they do not. Never an identifier: the city is its name.
 */
function BulkRouteNoDraft({
  driverReference,
  olderRoutes,
  creation,
  onCreate,
}: {
  readonly driverReference: string;
  /** The driver's older DRAFT routes are listed above, none of them dated today. */
  readonly olderRoutes: boolean;
  readonly creation: BulkRouteCreation;
  readonly onCreate: () => void;
}) {
  if (creation.kind === "offered" || creation.kind === "creating") {
    const creating = creation.kind === "creating";
    return (
      <div className="opsBulkRouteCreate">
        <p className="emptyState">
          <strong>{noTodayRouteText(driverReference, olderRoutes)}</strong>
        </p>
        <p id={createOfferId}>{createRouteOfferText(driverReference, creation.cityName)}</p>
        <button type="button" className="btn btnPrimary" onClick={onCreate} disabled={creating} aria-describedby={createOfferId}>
          Crear ruta nueva
        </button>
        {creating && (
          <p className="live" aria-live="polite">
            Creando la ruta en borrador…
          </p>
        )}
      </div>
    );
  }
  return (
    <p className="emptyState">
      <strong>{noTodayRouteText(driverReference, olderRoutes)}</strong>{" "}
      {creation.kind === "mixed_cities"
        ? bulkRouteMixedCitiesMessage
        : "Crea una para este repartidor en Rutas y vuelve a buscar aquí."}
    </p>
  );
}

/** The route fixed by the first run: its name and current stops, or why it can no longer be used. */
export function BulkRouteFixed({
  route,
  choice,
  routes,
  canReload,
  onReload,
}: {
  readonly route: { readonly stop_count: number };
  readonly choice: DraftRouteChoice | null;
  readonly routes: BulkRoutesLoad;
  readonly canReload: boolean;
  readonly onReload: () => void;
}) {
  return (
    <div className="opsBulkRouteFixed">
      {routes.kind === "loading" && (
        <p className="live" aria-live="polite">
          Leyendo de nuevo la ruta…
        </p>
      )}
      {routes.kind === "failed" && (
        <p className="notice noticeWarn" role="status">
          {routes.message}
        </p>
      )}
      {routes.kind === "ready" && choice !== null && (
        <p>
          Ruta: <strong>{choice.name}</strong> · {stopCountText(route.stop_count)}
        </p>
      )}
      {routes.kind === "ready" && choice === null && (
        <p className="notice noticeWarn" role="status">
          {bulkRouteGoneMessage}
        </p>
      )}
      {canReload && routes.kind !== "loading" && (
        <button type="button" className="btnLink" onClick={onReload}>
          Leer la ruta de nuevo
        </button>
      )}
    </div>
  );
}

/** The preview and the results: the orders in the order they are added, Guía and Destino only. */
export function BulkRoutePreview({
  orders,
  driverReference,
  choice,
  phase,
  results,
}: {
  readonly orders: readonly BulkOrder[];
  readonly driverReference: string;
  readonly choice: DraftRouteChoice | null;
  readonly phase: BulkRouteState["phase"];
  readonly results: BulkRouteState["results"];
}) {
  return (
    <div id={previewRegionId} className="opsTableScroll" role="region" aria-labelledby={previewCaptionId} tabIndex={0}>
      <table className="opsInboxTable opsBulkRoutePreview">
        <caption id={previewCaptionId}>
          <strong>Vista previa:</strong> {bulkRoutePreviewText(driverReference, orders.length, choice)}
        </caption>
        <thead>
          <tr>
            <th scope="col">Orden de envío</th>
            <th scope="col">Guía</th>
            <th scope="col">Destino (zona)</th>
            <th scope="col">Resultado</th>
          </tr>
        </thead>
        <tbody>
          {orders.map((order, index) => (
            <tr key={order.orderId}>
              <td>{index + 1}</td>
              <th scope="row">
                <span className="opsInboxGuide">{order.publicId}</span>
              </th>
              <td>{order.zoneName ?? "Sin zona asignada"}</td>
              <td>
                <RouteResultText result={results[order.orderId]} phase={phase} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function RouteResultText({
  result,
  phase,
}: {
  readonly result: BulkRouteRowResult | undefined;
  readonly phase: BulkRouteState["phase"];
}) {
  if (result === undefined) return <span>{phase === "choosing" ? "Por agregar" : "En espera"}</span>;
  if (result.kind === "pending") return <span>En espera</span>;
  if (result.kind === "sending") return <span>Enviando…</span>;
  if (result.kind === "added") return <strong className="opsBulkOk">Agregada a la ruta</strong>;
  if (result.kind === "not_sent") return <span className="opsBulkProblem">Sin enviar</span>;
  if (result.kind === "route_changed")
    return <span className="opsBulkProblem">{routeStopConflictMessages.VERSION_CONFLICT}</span>;
  return <span className="opsBulkProblem">{result.message}</span>;
}

function isAnswered(result: BulkRouteRowResult | undefined): boolean {
  return result !== undefined && result.kind !== "pending" && result.kind !== "sending";
}
