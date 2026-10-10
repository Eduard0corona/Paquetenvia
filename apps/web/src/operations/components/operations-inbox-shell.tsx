"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useEffect, useMemo, useRef, useState, type MouseEvent, type ReactNode } from "react";
import { DateTime } from "../../components/ui/date-time";
import { EmptyState } from "../../components/ui/empty-state";
import { ScreenGate } from "../../components/ui/feedback";
import { PageHeader } from "../../components/ui/page-header";
import { StatusBadge } from "../../components/ui/status-badge";
import {
  hasInboxFilters,
  inboxHref,
  inboxOrderHref,
  inboxQueueCount,
  inboxQueues,
  inboxQueueTabs,
  inboxStatusChoices,
  inboxViewKey,
  parseInboxView,
  priceReviewQueue,
  type InboxView,
} from "../contracts/inbox";
import type { OperationsDashboardOrder, OperationsServiceType } from "../contracts/operations-dashboard";
import { orderStatusLabels, serviceTypeLabel } from "../contracts/operations-formatters";
import type { OperationsQueueId } from "../contracts/queue-counts";
import { formatServiceWindow } from "../contracts/service-window";
import { useOperationsInbox } from "../state/use-operations-inbox";
import { OperationsDriverAssignment } from "./operations-driver-assignment";

const serviceTypes: readonly OperationsServiceType[] = ["SAME_DAY", "URGENT", "SCHEDULED_ROUTE"];
const assignmentPanelId = "inbox-assignment";

interface AssignTarget {
  readonly orderId: string;
  readonly publicId: string;
  /** The panel belongs to the view and the session it was opened in. */
  readonly viewKey: string;
  readonly sessionGeneration: number;
}

/**
 * UI-PHASE3-INBOX-2026-10-10: "Bandeja de trabajo", where DISPATCHER and PLATFORM_ADMIN land.
 *
 * The queues show the real server counts; the table lists the selected queue through the
 * operations dashboard queries; the queue and the chips live in the URL, so the order detail can
 * return to the same view. "Asignar" reuses the order detail's driver picker (same list,
 * confirmation and integer cents). There is no bulk action. The backend and RLS remain the
 * barrier: this screen only shows what the API returns.
 */
export function OperationsInboxShell() {
  const router = useRouter();
  const searchKey = useSearchParams().toString();
  const view = useMemo(() => parseInboxView(new URLSearchParams(searchKey)), [searchKey]);
  const viewKey = inboxViewKey(view);
  const state = useOperationsInbox(view);
  const [assigning, setAssigning] = useState<AssignTarget | null>(null);
  const assignRef = useRef<HTMLElement>(null);

  const showing = inboxViewKey(state.view) === viewKey;
  const rows = showing ? state.rows : [];
  const queue = inboxQueues[view.queue];
  const target =
    assigning !== null && assigning.viewKey === viewKey && assigning.sessionGeneration === state.sessionGeneration
      ? assigning
      : null;
  const targetOrderId = target?.orderId ?? null;
  const blocked = state.phase === "no_session" || state.phase === "access_unavailable";

  useEffect(() => {
    if (targetOrderId !== null) assignRef.current?.focus();
  }, [targetOrderId]);

  const go = (next: InboxView) => router.replace(inboxHref(next), { scroll: false });
  const count = (id: OperationsQueueId) => (
    <QueueCount value={inboxQueueCount(state.counts, id)} unavailable={state.countsUnavailable} />
  );
  const closeAssignment = () => {
    const orderId = targetOrderId;
    setAssigning(null);
    // Focus goes back to the row's "Asignar" when the row is still listed.
    if (orderId !== null)
      window.setTimeout(() => document.getElementById(assignButtonId(orderId))?.focus(), 0);
  };

  return (
    <div className="page" aria-busy={state.loading}>
      <PageHeader
        eyebrow="Despacho"
        title="Bandeja de trabajo"
        live
        actions={
          <>
            <span className="opsConnection">{state.connection}</span>
            <span>
              Última actualización:{" "}
              {state.lastUpdated === null ? "Pendiente" : <DateTime value={state.lastUpdated} />}
            </span>
            <Link className="btn btnSecondary" href="/ops/dashboard?view=positions">
              Mapa de posiciones
            </Link>
            <button
              type="button"
              className="btn btnSecondary"
              onClick={() => state.refresh()}
              disabled={state.loading || blocked}
            >
              Actualizar
            </button>
          </>
        }
      />

      <p className="pageNote">Horarios mostrados en hora de Mazatlán.</p>

      <ScreenGate
        phase={state.phase === "loading" ? "ready" : state.phase}
        accessMessage="No es posible mostrar la bandeja de trabajo."
      />

      {!blocked && (
        <>
          {state.error !== null && (
            <p className="notice noticeCrit" role="alert">
              {state.error}
            </p>
          )}

          <nav className="opsInboxQueues" aria-label="Colas de trabajo">
            <ul>
              {inboxQueueTabs.map((id) =>
                id === priceReviewQueue.id ? (
                  <li key={id}>
                    <div className="opsInboxQueue opsInboxQueueStatic">
                      <span className="opsInboxQueueLabel">
                        <span>{priceReviewQueue.label}</span> {count(id)}
                      </span>
                      <span className="opsInboxQueueNote">{priceReviewQueue.note}</span>
                    </div>
                  </li>
                ) : (
                  <li key={id}>
                    <Link
                      className="opsInboxQueue"
                      href={inboxHref({ ...view, queue: id, status: null })}
                      replace
                      scroll={false}
                      aria-current={view.queue === id ? "page" : undefined}
                    >
                      <span className="opsInboxQueueLabel">
                        <span>{inboxQueues[id].label}</span> {count(id)}
                      </span>
                    </Link>
                  </li>
                ),
              )}
            </ul>
          </nav>
          <p className="opsSummaryNote">
            {state.countsUnavailable
              ? "Los totales no están disponibles por ahora; la lista sigue actualizándose."
              : "Totales de todas las órdenes de la organización, sin aplicar filtros."}
          </p>

          <InboxFilters view={view} zones={state.zones} onChange={go} />

          {target !== null && (
            <section
              id={assignmentPanelId}
              ref={assignRef}
              tabIndex={-1}
              className="opsInboxAssign"
              aria-label={`Asignar repartidor a la orden ${target.publicId}`}
            >
              <div className="opsInboxAssignBar">
                <p>
                  Orden <strong>{target.publicId}</strong>
                </p>
                <Link className="btn btnSecondary" href={inboxOrderHref(target.orderId, view)}>
                  Abrir orden
                </Link>
                <button type="button" className="btn btnSecondary" onClick={closeAssignment}>
                  Cerrar
                </button>
              </div>
              <OperationsDriverAssignment
                orderId={target.orderId}
                publicId={target.publicId}
                assignable={rows.some((row) => row.order_id === target.orderId && row.unassigned_alert)}
                onOrderChanged={() => state.refresh("assignment")}
              />
            </section>
          )}

          {rows.length > 0 && (
            <div className="opsTableScroll" role="region" aria-labelledby="inbox-caption" tabIndex={0}>
              <table className="opsInboxTable">
                <caption id="inbox-caption">
                  <strong>{queue.label}:</strong> {queue.description}
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Guía</th>
                    <th scope="col">Estado</th>
                    <th scope="col">Destino (zona)</th>
                    <th scope="col">Ventana de entrega</th>
                    <th scope="col">Repartidor</th>
                    <th scope="col">
                      <span className="srOnly">Acciones</span>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((row) => (
                    <InboxRow
                      key={row.order_id}
                      row={row}
                      href={inboxOrderHref(row.order_id, view)}
                      assigning={targetOrderId === row.order_id}
                      onOpen={(href) => router.push(href)}
                      onAssign={() =>
                        setAssigning({
                          orderId: row.order_id,
                          publicId: row.public_id,
                          viewKey,
                          sessionGeneration: state.sessionGeneration,
                        })
                      }
                    />
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {showing && state.loaded && rows.length === 0 && !state.hasMore && (
            <EmptyState title={queue.empty}>
              {hasInboxFilters(view) ? "Prueba con otros filtros o límpialos." : null}
            </EmptyState>
          )}
          {(!showing || !state.loaded) && state.error === null && (
            <p className="live" aria-live="polite">
              Cargando órdenes…
            </p>
          )}

          {rows.length > 0 && (
            <p className="opsSummaryNote">
              {rows.length === 1 ? "Se muestra 1 orden" : `Se muestran ${rows.length} órdenes`}
              {state.hasMore ? "; hay más por cargar." : "."}
            </p>
          )}
          {showing && state.hasMore && (
            <button type="button" className="btn btnSecondary" onClick={state.loadMore} disabled={state.loadingMore}>
              {state.loadingMore ? "Cargando…" : "Cargar más"}
            </button>
          )}
          <p className="live" aria-live="polite">
            {state.loading && state.loaded ? "Actualizando la bandeja." : ""}
          </p>
        </>
      )}
    </div>
  );
}

function InboxRow({
  row,
  href,
  assigning,
  onOpen,
  onAssign,
}: {
  readonly row: OperationsDashboardOrder;
  readonly href: string;
  readonly assigning: boolean;
  readonly onOpen: (href: string) => void;
  readonly onAssign: () => void;
}) {
  // A click anywhere on the row opens the order; links, buttons and selected text keep
  // their own behavior and the guide link is the keyboard path.
  const openFromRow = (event: MouseEvent<HTMLTableRowElement>) => {
    const element = event.target instanceof Element ? event.target : null;
    if (element !== null && element.closest("a, button, input, select, textarea, label") !== null) return;
    if ((window.getSelection()?.toString() ?? "") !== "") return;
    onOpen(href);
  };
  return (
    <tr className="opsInboxRow" onClick={openFromRow}>
      <th scope="row">
        <Link className="opsInboxGuide" href={href}>
          {row.public_id}
        </Link>
      </th>
      <td>
        <span className="opsInboxStatus">
          <StatusBadge status={row.status} showGroup={false} />
          {row.cost_warning !== null && <span className="opsInboxTag">Precio por revisar</span>}
        </span>
      </td>
      <td>{row.delivery_zone?.name ?? "Sin zona asignada"}</td>
      <td>{formatServiceWindow(row.delivery_window)}</td>
      <td>{row.assignment?.driver_reference ?? "Sin repartidor"}</td>
      <td>
        <span className="opsInboxActions">
          <Link className="btn btnSecondary" href={href}>
            Abrir<span className="srOnly"> la orden {row.public_id}</span>
          </Link>
          {row.unassigned_alert && (
            <button
              id={assignButtonId(row.order_id)}
              type="button"
              className="btn btnPrimary"
              aria-expanded={assigning}
              aria-controls={assigning ? assignmentPanelId : undefined}
              onClick={onAssign}
            >
              Asignar<span className="srOnly"> la orden {row.public_id}</span>
            </button>
          )}
        </span>
      </td>
    </tr>
  );
}

/**
 * Filter chips. Every chip is a server filter of the dashboard query (status of the queue,
 * service type, delivery zone); none filters the rows already loaded.
 */
function InboxFilters({
  view,
  zones,
  onChange,
}: {
  readonly view: InboxView;
  readonly zones: readonly { readonly id: string; readonly label: string }[];
  readonly onChange: (view: InboxView) => void;
}) {
  const statuses = inboxStatusChoices(view.queue);
  const zoneChoices =
    view.zoneId !== null && !zones.some((zone) => zone.id === view.zoneId)
      ? [...zones, { id: view.zoneId, label: "Zona elegida" }]
      : zones;
  return (
    <section className="opsInboxFilters" aria-label="Filtros de la cola">
      {statuses.length > 0 && (
        <fieldset className="opsChipGroup">
          <legend>Estado</legend>
          <Chip pressed={view.status === null} onClick={() => onChange({ ...view, status: null })}>
            Todos
          </Chip>
          {statuses.map((status) => (
            <Chip key={status} pressed={view.status === status} onClick={() => onChange({ ...view, status })}>
              {orderStatusLabels[status]}
            </Chip>
          ))}
        </fieldset>
      )}
      <fieldset className="opsChipGroup">
        <legend>Servicio</legend>
        <Chip pressed={view.serviceType === null} onClick={() => onChange({ ...view, serviceType: null })}>
          Todos
        </Chip>
        {serviceTypes.map((serviceType) => (
          <Chip
            key={serviceType}
            pressed={view.serviceType === serviceType}
            onClick={() => onChange({ ...view, serviceType })}
          >
            {serviceTypeLabel(serviceType)}
          </Chip>
        ))}
      </fieldset>
      {zoneChoices.length > 0 && (
        <fieldset className="opsChipGroup">
          <legend>Zona de entrega</legend>
          <Chip pressed={view.zoneId === null} onClick={() => onChange({ ...view, zoneId: null })}>
            Todas
          </Chip>
          {zoneChoices.map((zone) => (
            <Chip key={zone.id} pressed={view.zoneId === zone.id} onClick={() => onChange({ ...view, zoneId: zone.id })}>
              {zone.label}
            </Chip>
          ))}
        </fieldset>
      )}
      {hasInboxFilters(view) && (
        <button
          type="button"
          className="btnLink"
          onClick={() => onChange({ ...view, status: null, serviceType: null, zoneId: null })}
        >
          Limpiar filtros
        </button>
      )}
    </section>
  );
}

/** A queue's server count: the number, "…" while it loads, "Sin dato" when it failed. */
function QueueCount({ value, unavailable }: { readonly value: number | null; readonly unavailable: boolean }) {
  if (value !== null)
    return (
      <span className="opsInboxCount">
        {value}
        <span className="srOnly">{value === 1 ? " orden" : " órdenes"}</span>
      </span>
    );
  if (unavailable) return <span className="opsInboxCount">Sin dato</span>;
  return (
    <span className="opsInboxCount">
      <span aria-hidden="true">…</span>
      <span className="srOnly">conteo pendiente</span>
    </span>
  );
}

function assignButtonId(orderId: string): string {
  return `inbox-assign-${orderId}`;
}

function Chip({
  pressed,
  onClick,
  children,
}: {
  readonly pressed: boolean;
  readonly onClick: () => void;
  readonly children: ReactNode;
}) {
  return (
    <button type="button" className="btn btnSecondary opsChip" aria-pressed={pressed} onClick={onClick}>
      {children}
    </button>
  );
}
