"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useId, useState, type MouseEvent } from "react";
import { DateTime } from "../../components/ui/date-time";
import { EmptyState } from "../../components/ui/empty-state";
import { Feedback, ScreenGate } from "../../components/ui/feedback";
import { Money } from "../../components/ui/money";
import { PageHeader } from "../../components/ui/page-header";
import { StatusBadge } from "../../components/ui/status-badge";
import { orderStatuses } from "../contracts/operations-dashboard";
import { orderStatusLabels } from "../contracts/operations-formatters";
import {
  defaultOrderListView,
  hasOrderListFilters,
  orderListHref,
  orderListSummaryText,
  parseOrderListView,
  type OrderListItem,
  type OrderListView,
} from "../contracts/order-list";
import { normalizePublicIdInput, orderNotFoundMessage } from "../contracts/order-transitions";
import { orderStatusGroupIds, orderStatusGroups, statusesInGroup } from "../contracts/status-groups";
import { operationsOrderHref } from "../routing/operations-routing";
import type { OrderListState } from "../state/order-list-controller";
import { useOrderList } from "../state/use-order-list";

/**
 * UI-PHASE3-ORDERS-LIST-2026-10-12 ("Sí, lista de órdenes"): every order of the selected organization, newest first,
 * searched by guía and filtered by status and creation period in Mazatlán days (ORD-LIST-DATE-FILTER-2026-10-12).
 * A row opens the existing order detail for the roles that can open it. The backend and RLS remain the barrier: this
 * screen only shows what listOrders returns, and it never shows an internal identifier.
 */
export function OperationsOrdersShell() {
  const router = useRouter();
  const view = parseOrderListView(useSearchParams());
  const { state, controller } = useOrderList(view);
  return (
    <OrderListScreen
      state={state}
      onViewChange={(next) => {
        // The controller takes the new filters at once (so a second change builds on the first) and the URL
        // follows in place, without a new history entry; back and forward still restore a list.
        void controller.setView(next);
        window.history.replaceState(null, "", orderListHref(next));
      }}
      onGuideChange={(publicId) => void controller.setGuide(publicId)}
      onRefresh={() => void controller.refresh("manual")}
      onLoadMore={() => void controller.loadMore()}
      onOpen={(href) => router.push(href)}
    />
  );
}

export interface OrderListScreenProps {
  readonly state: OrderListState;
  readonly onViewChange: (view: OrderListView) => void;
  readonly onGuideChange: (publicId: string | null) => void;
  readonly onRefresh: () => void;
  readonly onLoadMore: () => void;
  readonly onOpen: (href: string) => void;
}

/** The whole screen for one state; pure, so every state can be rendered and checked on its own. */
export function OrderListScreen({
  state,
  onViewChange,
  onGuideChange,
  onRefresh,
  onLoadMore,
  onOpen,
}: OrderListScreenProps) {
  const blocked = state.phase === "no_session" || state.phase === "access_unavailable";
  const filtered = hasOrderListFilters(state.view);
  return (
    <div className="page" aria-busy={state.loading || state.loadingMore}>
      <PageHeader
        eyebrow="Operación"
        title="Órdenes"
        description="Todas las órdenes de tu organización, de la más reciente a la más antigua. Busca por guía o filtra por estado y fecha de creación."
        live
        actions={
          <>
            <span>
              Última actualización:{" "}
              {state.lastUpdated === null ? "Pendiente" : <DateTime value={state.lastUpdated} />}
            </span>
            <button
              type="button"
              className="btn btnSecondary"
              onClick={onRefresh}
              disabled={state.phase !== "ready" || state.loading}
            >
              Actualizar
            </button>
          </>
        }
      />

      <p className="pageNote">Fechas en hora de Mazatlán.</p>

      <ScreenGate phase={state.phase} accessMessage="Tu rol en la organización activa no consulta órdenes." />

      {!blocked && state.phase === "ready" && (
        <>
          <OrderGuideSearch
            key={state.guide ?? ""}
            guide={state.guide}
            disabled={state.loading}
            onGuideChange={onGuideChange}
          />
          <OrderListFilters view={state.view} onViewChange={onViewChange} />

          {state.rangeError !== null && (
            <p className="notice noticeCrit" role="alert">
              {state.rangeError}
            </p>
          )}
          <Feedback errors={[]} message={state.message} stepUpHref={state.stepUpHref} />
          {state.error !== null && (
            <p className="notice noticeCrit" role="alert">
              {state.error}
            </p>
          )}

          {!state.canOpenDetail && (
            <p className="pageNote">El detalle de cada orden lo abren despacho y administración; tu rol consulta la lista.</p>
          )}

          {state.rows.length > 0 && (
            <OrderListTable rows={state.rows} canOpenDetail={state.canOpenDetail} onOpen={onOpen} />
          )}

          {state.loaded && state.rows.length === 0 && state.message === null && (
            <OrderListEmpty guide={state.guide} filtered={filtered} />
          )}
          {!state.loaded && state.loading && state.error === null && state.message === null && (
            <p className="live" aria-live="polite">
              Cargando órdenes…
            </p>
          )}

          {state.rows.length > 0 && (
            <p className="opsSummaryNote">{orderListSummaryText(state.rows.length, state.nextCursor !== null)}</p>
          )}
          {state.nextCursor !== null && (
            <button type="button" className="btn btnSecondary" onClick={onLoadMore} disabled={state.loadingMore}>
              {state.loadingMore ? "Cargando…" : "Cargar más"}
            </button>
          )}
          <p className="live" aria-live="polite">
            {state.loading && state.loaded ? "Actualizando la lista de órdenes." : ""}
          </p>
        </>
      )}
    </div>
  );
}

/**
 * "Buscar guía" inside the list: only the exact tracking number (ORD_…), validated like the top-bar search before
 * anything is sent; a value that cannot be a guía reads like an unknown one. The typed text stays in this field only.
 */
export function OrderGuideSearch({
  guide,
  disabled,
  onGuideChange,
}: {
  readonly guide: string | null;
  readonly disabled: boolean;
  readonly onGuideChange: (publicId: string | null) => void;
}) {
  const inputId = useId();
  const messageId = useId();
  const [value, setValue] = useState(guide ?? "");
  const [message, setMessage] = useState<string | null>(null);
  return (
    <form
      className="opsOrderSearch"
      role="search"
      aria-label="Buscar orden por guía"
      noValidate
      onSubmit={(event) => {
        event.preventDefault();
        if (disabled) return;
        const publicId = normalizePublicIdInput(value);
        if (publicId === null) {
          setMessage(orderNotFoundMessage);
          return;
        }
        setMessage(null);
        onGuideChange(publicId);
      }}
    >
      <label htmlFor={inputId}>
        Guía (ORD_…)
        <input
          id={inputId}
          name="public_id"
          type="search"
          inputMode="text"
          autoComplete="off"
          autoCapitalize="off"
          spellCheck={false}
          maxLength={64}
          value={value}
          aria-describedby={message === null ? undefined : messageId}
          onChange={(event) => {
            setValue(event.currentTarget.value);
            setMessage(null);
          }}
        />
      </label>
      <div className="opsFormActions">
        <button type="submit" className="btn btnSecondary" aria-disabled={disabled}>
          Buscar guía
        </button>
        {guide !== null && (
          <button
            type="button"
            className="btnLink"
            onClick={() => {
              setValue("");
              setMessage(null);
              onGuideChange(null);
            }}
          >
            Quitar búsqueda
          </button>
        )}
      </div>
      {message !== null && (
        <p id={messageId} className="fieldError" role="status" aria-live="polite">
          {message}
        </p>
      )}
    </form>
  );
}

/** Status (one or all, grouped as everywhere else) and the creation period in Mazatlán calendar days. */
export function OrderListFilters({
  view,
  onViewChange,
}: {
  readonly view: OrderListView;
  readonly onViewChange: (view: OrderListView) => void;
}) {
  return (
    <fieldset className="opsFilters">
      <legend>Filtros</legend>
      <label>
        Estado
        <select
          value={view.status ?? ""}
          onChange={(event) => {
            const value = event.currentTarget.value;
            onViewChange({ ...view, status: orderStatuses.find((candidate) => candidate === value) ?? null });
          }}
        >
          <option value="">Todos los estados</option>
          {orderStatusGroupIds.map((group) => (
            <optgroup key={group} label={orderStatusGroups[group].label}>
              {statusesInGroup(group).map((status) => (
                <option key={status} value={status}>
                  {orderStatusLabels[status]}
                </option>
              ))}
            </optgroup>
          ))}
        </select>
      </label>
      <label>
        Creada desde (hora de Mazatlán)
        <input
          type="date"
          value={view.from ?? ""}
          onChange={(event) => onViewChange({ ...view, from: event.currentTarget.value || null })}
        />
      </label>
      <label>
        Creada hasta (hora de Mazatlán)
        <input
          type="date"
          value={view.to ?? ""}
          onChange={(event) => onViewChange({ ...view, to: event.currentTarget.value || null })}
        />
      </label>
      {hasOrderListFilters(view) && (
        <div className="opsFormActions">
          <button type="button" className="btnLink" onClick={() => onViewChange(defaultOrderListView)}>
            Limpiar filtros
          </button>
        </div>
      )}
    </fieldset>
  );
}

export function OrderListTable({
  rows,
  canOpenDetail,
  onOpen,
}: {
  readonly rows: readonly OrderListItem[];
  readonly canOpenDetail: boolean;
  readonly onOpen: (href: string) => void;
}) {
  return (
    <div className="opsTableScroll" role="region" aria-labelledby="orders-caption" tabIndex={0}>
      <table className="opsTable">
        <caption id="orders-caption">Órdenes de tu organización, de la más reciente a la más antigua.</caption>
        <thead>
          <tr>
            <th scope="col">Guía</th>
            <th scope="col">Estado</th>
            <th scope="col" className="opsAmount">
              Total (IVA incluido)
            </th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <OrderListRow
              key={row.id}
              row={row}
              href={canOpenDetail ? operationsOrderHref(row.id) : null}
              onOpen={onOpen}
            />
          ))}
        </tbody>
      </table>
    </div>
  );
}

/**
 * One order: guía, exact status (group color, dot and text) and total in integer cents. With `href`, a click anywhere
 * on the row opens the detail and the guía link is the keyboard path; without it the row is read-only.
 */
export function OrderListRow({
  row,
  href,
  onOpen,
}: {
  readonly row: OrderListItem;
  readonly href: string | null;
  readonly onOpen: (href: string) => void;
}) {
  const openFromRow = (event: MouseEvent<HTMLTableRowElement>) => {
    if (href === null) return;
    const element = event.target instanceof Element ? event.target : null;
    if (element !== null && element.closest("a, button, input, select, textarea, label") !== null) return;
    if ((window.getSelection()?.toString() ?? "") !== "") return;
    onOpen(href);
  };
  return (
    <tr className={href === null ? undefined : "opsInboxRow"} onClick={href === null ? undefined : openFromRow}>
      <th scope="row">
        {href === null ? (
          <span className="opsInboxGuide">{row.public_id}</span>
        ) : (
          <Link className="opsInboxGuide" href={href}>
            {row.public_id}
          </Link>
        )}
      </th>
      <td>
        <StatusBadge status={row.status} showGroup={false} />
      </td>
      {/* Integer MXN cents from the API, formatted by the shared Money component (no floating point). */}
      <td className="opsAmount">
        <Money cents={row.total_cents} />
      </td>
    </tr>
  );
}

/** Empty list: the uniform guía message for a search, otherwise whether filters are applied. */
export function OrderListEmpty({ guide, filtered }: { readonly guide: string | null; readonly filtered: boolean }) {
  if (guide !== null) {
    return (
      <EmptyState title={orderNotFoundMessage}>
        {filtered ? "Si la guía es correcta, prueba sin filtros de estado o fecha." : "Revisa el número de guía."}
      </EmptyState>
    );
  }
  return filtered ? (
    <EmptyState title="Sin órdenes con estos filtros">Prueba con otro estado o con otras fechas.</EmptyState>
  ) : (
    <EmptyState title="Sin órdenes">Tu organización todavía no tiene órdenes.</EmptyState>
  );
}
