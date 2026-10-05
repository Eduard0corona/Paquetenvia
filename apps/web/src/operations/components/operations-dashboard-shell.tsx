"use client";

import { useMemo, useState } from "react";
import { useConfirmDialog } from "../../components/confirm-dialog";
import { OperationsFilters } from "./operations-filters";
import { OperationsOrderCard } from "./operations-order-card";
import { OperationsPositions } from "./operations-positions";
import { orderStatuses } from "../contracts/operations-dashboard";
import { orderStatusLabels } from "../contracts/operations-formatters";
import { DateTime } from "../../components/ui/date-time";
import { PageHeader } from "../../components/ui/page-header";
import { useOperationsDashboard } from "../state/use-operations-dashboard";

export function OperationsDashboardShell() {
  const state = useOperationsDashboard();
  const [view, setView] = useState<"list" | "positions">("list");
  const { confirm, dialog } = useConfirmDialog();
  const grouped = useMemo(
    () =>
      orderStatuses.map((status) => ({
        status,
        items: state.items.filter((item) => item.status === status),
      })),
    [state.items],
  );
  const summary = {
    total: state.items.length,
    unassigned: state.items.filter((item) => item.unassigned_alert).length,
    delivering: state.items.filter((item) => item.status === "DELIVERING").length,
    warnings: state.items.filter((item) => item.cost_warning !== null).length,
  };

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
        <Summary label="Órdenes cargadas" value={summary.total} />
        <Summary label="Órdenes sin asignar" value={summary.unassigned} />
        <Summary label="Órdenes en reparto" value={summary.delivering} />
        <Summary label="Revisar precio" value={summary.warnings} />
      </section>

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
          onClick={() => setView("list")}
        >
          Lista
        </button>
        <button
          type="button"
          className="btn btnSecondary"
          aria-pressed={view === "positions"}
          onClick={() => setView("positions")}
        >
          Posiciones
        </button>
      </fieldset>

      {view === "positions" ? (
        <OperationsPositions items={state.items} />
      ) : (
        <section className="opsBoard" aria-label="Órdenes agrupadas por estado">
          {grouped.map(({ status, items }) => (
            <section
              className="opsBoardColumn"
              aria-labelledby={`status-${status}`}
              key={status}
            >
              <h2 id={`status-${status}`}>
                {orderStatusLabels[status]} <span>{items.length}</span>
              </h2>
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
  readonly value: number;
}) {
  return (
    <article>
      <span>{label}</span>
      <strong>{value}</strong>
    </article>
  );
}
