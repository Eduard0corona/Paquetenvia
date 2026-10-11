"use client";

import { useSearchParams } from "next/navigation";
import { useMemo } from "react";
import { useConfirmDialog } from "../../components/confirm-dialog";
import { OperationsFilters } from "./operations-filters";
import { OperationsOrderCard } from "./operations-order-card";
import { OperationsPositions } from "./operations-positions";
import { dashboardViewHref, parseDashboardView, type DashboardView } from "../contracts/dashboard-view";
import { orderStatusGroupIds, statusGroup } from "../contracts/status-groups";
import { statusGroupTotals } from "../contracts/queue-counts";
import { DateTime } from "../../components/ui/date-time";
import { EmptyState } from "../../components/ui/empty-state";
import { PageHeader } from "../../components/ui/page-header";
import { StatusGroupChip } from "../../components/ui/status-badge";
import { useOperationsDashboard } from "../state/use-operations-dashboard";

export function OperationsDashboardShell() {
  const state = useOperationsDashboard();
  const { confirm, dialog } = useConfirmDialog();
  // UI-PHASE3-INBOX-2026-10-10: the view lives in the URL and is read while rendering, so a deep link
  // (/ops/dashboard?view=positions, "Mapa de posiciones" in the inbox) opens the positions view without
  // painting the list first, and a later navigation with another query shows the view it names.
  const searchParams = useSearchParams();
  const view = parseDashboardView(searchParams);
  // The toggle rewrites the URL in place; Next.js syncs useSearchParams with history.replaceState, so
  // switching needs no server request, adds no history entry and keeps the loaded orders.
  const showView = (next: DashboardView) => {
    if (next !== view) window.history.replaceState(null, "", dashboardViewHref(next, searchParams));
  };
  const grouped = useMemo(
    () =>
      orderStatusGroupIds.map((group) => ({
        group,
        items: state.items.filter((item) => statusGroup(item.status) === group),
      })),
    [state.items],
  );
  // UI-PHASE2-QUEUE-COUNTS-2026-10-05: indicators and group totals are the
  // server counts of every order, never a count of the loaded page.
  const counts = state.queueCounts;
  const groupTotals = useMemo(
    () => (counts === null ? null : statusGroupTotals(counts)),
    [counts],
  );
  const pendingValue = state.queueCountsUnavailable ? "Sin dato" : "…";

  return (
    <div className="page" aria-busy={state.loading}>
      <PageHeader
        eyebrow="Despacho"
        title="Operaciones"
        live
        actions={
          <>
            <span className="opsConnection">{state.connection}</span>
            <span>
              Última actualización:{" "}
              {state.lastUpdated === null ? "Pendiente" : <DateTime value={state.lastUpdated} />}
            </span>
            <button
              type="button"
              className="btn btnSecondary"
              onClick={state.refresh}
              disabled={state.loading || state.accessUnavailable}
            >
              Actualizar
            </button>
          </>
        }
      />

      <p className="pageNote">Horarios mostrados en hora de Mazatlán.</p>

      {state.accessUnavailable && (
        <section className="panel" role="alert">
          <h2>Acceso no disponible</h2>
          <p>No es posible mostrar información de operaciones.</p>
        </section>
      )}
      {state.error !== null && (
        <p className="notice noticeCrit" role="alert">
          {state.error}
        </p>
      )}


      <section className="opsSummary" aria-label="Resumen operativo">
        <Summary label="Sin asignar" value={counts?.queues.unassigned ?? pendingValue} />
        <Summary label="Requiere atención" value={counts?.queues.needs_attention ?? pendingValue} />
        <Summary label="Revisar precio" value={counts?.queues.price_review ?? pendingValue} />
        <Summary
          label="Entregadas sin cerrar"
          value={counts?.queues.delivered_not_closed ?? pendingValue}
        />
        <Summary label="En ruta" value={counts?.queues.en_route ?? pendingValue} />
      </section>
      <p className="opsSummaryNote">
        {state.queueCountsUnavailable
          ? "Los totales no están disponibles por ahora; la lista sigue actualizándose."
          : "Totales de todas las órdenes de la organización, sin aplicar filtros."}
      </p>

      <OperationsFilters
        filters={state.filters}
        items={state.items}
        disabled={state.accessUnavailable}
        onChange={state.setFilters}
      />

      <fieldset className="opsToggle">
        <legend>Vista</legend>
        <button
          type="button"
          className="btn btnSecondary"
          aria-pressed={view === "list"}
          onClick={() => showView("list")}
        >
          Lista
        </button>
        <button
          type="button"
          className="btn btnSecondary"
          aria-pressed={view === "positions"}
          onClick={() => showView("positions")}
        >
          Posiciones
        </button>
      </fieldset>

      {view === "positions" ? (
        <OperationsPositions items={state.items} />
      ) : (
        <section className="opsBoard" aria-label="Órdenes agrupadas por estado">
          {grouped.map(({ group, items }) => (
            <section
              className="opsBoardColumn"
              aria-labelledby={`status-group-${group}`}
              key={group}
            >
              <h2 id={`status-group-${group}`}>
                <StatusGroupChip group={group} />
                <span className="opsBoardTotals">
                  <span className="opsBoardCount">
                    {items.length}
                    <span className="srOnly">
                      {" "}
                      {items.length === 1 ? "orden cargada" : "órdenes cargadas"}
                    </span>
                  </span>
                  {groupTotals !== null && (
                    <span className="opsBoardTotal">{groupTotals[group]} en total</span>
                  )}
                </span>
              </h2>
              {items.length === 0 ? (
                <EmptyState>Sin órdenes en este grupo.</EmptyState>
              ) : (
                <div className="opsCards">
                  {items.map((item) => (
                    <OperationsOrderCard
                      key={item.order_id}
                      order={item}
                      confirm={confirm}
                      onPublishExternalOffer={state.publishExternalOffer}
                    />
                  ))}
                </div>
              )}
            </section>
          ))}
        </section>
      )}

      {view === "list" && state.nextCursor !== null && (
        <button
          type="button"
          className="btn btnSecondary"
          onClick={state.loadMore}
          disabled={state.loadingMore}
        >
          {state.loadingMore ? "Cargando…" : "Cargar más"}
        </button>
      )}
      <p className="live" aria-live="polite">
        {state.loading ? "Actualizando operaciones." : ""}
      </p>
      {dialog}
    </div>
  );
}

function Summary({
  label,
  value,
}: {
  readonly label: string;
  readonly value: number | string;
}) {
  return (
    <article>
      <span>{label}</span>
      <strong>{value}</strong>
    </article>
  );
}
