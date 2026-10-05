"use client";

import Link from "next/link";
import { useMemo, useState } from "react";
import { useConfirmDialog } from "../../components/confirm-dialog";
import { OperationsFilters } from "./operations-filters";
import { OperationsOrderCard } from "./operations-order-card";
import { OperationsPositions } from "./operations-positions";
import { orderStatuses } from "../contracts/operations-dashboard";
import {
  formatMazatlanTime,
  orderStatusLabels,
} from "../contracts/operations-formatters";
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
    <main className="opsShell" aria-busy={state.loading}>
      <header className="opsHeader">
        <div>
          <p className="opsEyebrow">Despacho</p>
          <h1>Operaciones</h1>
          <p>{state.activeOrganizationName}</p>
        </div>
        <div className="opsHeaderStatus" aria-live="polite">
          <span>{state.connection}</span>
          <span>
            Última actualización:{" "}
            {state.lastUpdated === null
              ? "Pendiente"
              : formatMazatlanTime(state.lastUpdated)}
          </span>
          <button
            type="button"
            className="opsPrimary"
            onClick={state.refresh}
            disabled={state.loading || state.accessUnavailable}
          >
            Actualizar
          </button>
          <Link className="opsPrimary" href="/ops/orders/new">Nueva orden</Link>
          <Link className="opsPrimary" href="/ops/routes">Rutas manuales</Link>
          <Link className="opsPrimary" href="/ops/orders/import">Importar CSV</Link>
          <Link className="opsPrimary" href="/ops/incidents">Incidencias</Link>
          <Link className="opsPrimary" href="/finance/cod">Cobro contra entrega</Link>
        </div>
      </header>

      <p className="opsTimezone">Horarios mostrados en hora de Mazatlán.</p>

      {state.contexts.length > 1 && state.canChangeOrganization ? (
        <label className="opsOrganizationSelector">
          Organización activa
          <select
            value={
              state.contexts.find(
                (context) =>
                  context.display_name === state.activeOrganizationName,
              )?.organization_id ?? ""
            }
            onChange={(event) =>
              void state.requestOrganizationChange(event.target.value)
            }
          >
            {state.contexts.map((context) => (
              <option key={context.organization_id} value={context.organization_id}>
                {context.display_name}
              </option>
            ))}
          </select>
        </label>
      ) : null}

      {state.accessUnavailable && (
        <section className="opsMessage" role="alert">
          <h2>Acceso no disponible</h2>
          <p>No es posible mostrar información de operaciones.</p>
        </section>
      )}
      {state.error !== null && (
        <p className="opsAlert" role="alert">
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
          aria-pressed={view === "list"}
          onClick={() => setView("list")}
        >
          Lista
        </button>
        <button
          type="button"
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
          className="opsLoadMore"
          onClick={state.loadMore}
          disabled={state.loadingMore}
        >
          {state.loadingMore ? "Cargando…" : "Cargar más"}
        </button>
      )}
      <p className="opsLive" aria-live="polite">
        {state.loading ? "Actualizando operaciones." : ""}
      </p>
      {dialog}
    </main>
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
