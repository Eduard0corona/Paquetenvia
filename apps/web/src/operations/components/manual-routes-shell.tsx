"use client";

import Link from "next/link";
import { useRef } from "react";
import { useConfirmDialog } from "../../components/confirm-dialog";
import {
  removeRouteStopConfirmation,
  routeName,
  routeStatusLabels,
  routeStopsText,
  routeStopStatusLabels,
  routeStopSubject,
  routeStopTypeLabels,
  type ManualRoute,
  type ManualRouteDetail,
  type ManualRouteStop,
} from "../contracts/manual-route";
import { routeDriverText, routeListDriverText, type CreateRouteFormState } from "../state/create-route-form-controller";
import type { RouteGuide, RoutesNotice } from "../state/manual-routes-controller";
import { useCreateRouteForm } from "../state/use-create-route-form";
import { useManualRoutes } from "../state/use-manual-routes";
import { EmptyState } from "../../components/ui/empty-state";
import { Field } from "../../components/ui/field";
import { Money } from "../../components/ui/money";
import { PageHeader } from "../../components/ui/page-header";
import { CreateRouteForm } from "./create-route-form";

/**
 * The route planner (`/ops/routes`). UI-PHASE3-ROUTES-NO-IDS-2026-10-12 ("Sí, todo (Recommended)"): every route is
 * named by its date ("Ruta del 12 de octubre de 2026", "Ruta sin fecha"), its driver by the DRV- reference and each
 * stop by its guía; "Agregar entrega" takes the guía (ORD_…). Every refused change says why, with "Verificar
 * identidad" when only MFA is missing. Ids are keys and option values only, never text.
 * UI-PHASE3-ROUTES-ROW-DRIVER-2026-10-12 ("Sí, DRV- en cada fila (Recommended)"): each row of the list also names its
 * driver by DRV-, from the same listOrganizationDrivers read as "Crear ruta" and the detail.
 */
export function ManualRoutesShell() {
  const { state, controller } = useManualRoutes();
  const form = useCreateRouteForm();
  const { confirm, dialog } = useConfirmDialog();
  const selected = state.selected;
  return (
    <div className="page" aria-busy={state.loading || state.mutating}>
      <PageHeader
        eyebrow="Despacho"
        title="Rutas manuales"
        description="Planeación de rutas para tu flota propia."
        live
        actions={<>
          <span>{state.connected ? "Tiempo real conectado" : "Actualización manual"}</span>
          <button className="btn btnSecondary" type="button" onClick={() => void controller.refresh()} disabled={state.loading}>Actualizar</button>
        </>}
      />

      {state.notice && <RoutesNoticeView notice={state.notice} />}
      <section className="opsRouteLayout">
        <aside className="panel opsRoutePanel">
          <h2>Crear ruta</h2>
          <CreateRouteForm form={form.state} controller={form.controller} mutating={state.mutating} onCreate={(input) => void controller.create(input)} />
          <h2>Rutas</h2>
          <RouteList
            routes={state.routes}
            drivers={form.state}
            truncated={state.routesTruncated}
            selectedId={selected?.id ?? null}
            onSelect={(routeId) => void controller.select(routeId)}
          />
        </aside>

        <section className="panel opsRouteDetail">
          {selected === null ? <EmptyState>Selecciona una ruta para ver sus paradas.</EmptyState> : (
            <RouteDetailView
              route={selected}
              driver={routeDriverText(form.state, selected.driver_id)}
              guides={state.guides}
              guide={state.guide}
              mutating={state.mutating}
              onGuideChange={(value) => controller.setGuide(value)}
              onAdd={() => void controller.addStop()}
              onReorder={(stopIds) => void controller.reorder(stopIds)}
              onRemove={(stop, guide) => confirm({
                ...removeRouteStopConfirmation(stop, guide),
                onConfirm: () => void controller.removeStop(stop.id),
              })}
            />
          )}
        </section>
      </section>
      {dialog}
    </div>
  );
}

/** The notice of the last read or change: its reason and, when only MFA is missing, "Verificar identidad". */
export function RoutesNoticeView({ notice }: { readonly notice: RoutesNotice }) {
  return (
    <p className={notice.tone === "info" ? "notice noticeInfo" : "notice noticeWarn"} role="status">
      {notice.text}{" "}
      {notice.stepUpHref !== null && (
        <Link className="btn btnPrimary" href={notice.stepUpHref}>Verificar identidad</Link>
      )}
    </p>
  );
}

/**
 * The routes in the server's order, each by its date name and driver ("Ruta del 12 de octubre de 2026 ·
 * DRV-1a2b3c4d", UI-PHASE3-ROUTES-ROW-DRIVER-2026-10-12), status, stops and assignment cost total. A driver the
 * drivers read does not have is "Repartidor no disponible"; while that read is in flight the row shows the date name
 * alone; never an id. Each row's accessible name is its text.
 */
export function RouteList({
  routes,
  drivers,
  truncated,
  selectedId,
  onSelect,
}: {
  readonly routes: readonly ManualRoute[];
  /** The listOrganizationDrivers read of "Crear ruta", which also names the driver of the detail. */
  readonly drivers: Pick<CreateRouteFormState, "drivers" | "driverReferences">;
  readonly truncated: boolean;
  readonly selectedId: string | null;
  readonly onSelect: (routeId: string) => void;
}) {
  return (
    <>
      <ul className="opsRouteList">{routes.map((route) => {
        const driver = routeListDriverText(drivers, route.driver_id);
        return <li key={route.id}>
          <button type="button" aria-current={selectedId === route.id} onClick={() => onSelect(route.id)}>
            <strong>{driver === null ? routeName(route) : `${routeName(route)} · ${driver}`}</strong>
            <span>{routeStatusLabels[route.status]}</span>
            <span>{routeStopsText(route.stop_count)} · <Money cents={route.assignment_cost_cents_total} currency={false} /></span>
          </button></li>;
      })}</ul>
      {truncated && <p className="fieldHint">Hay más rutas de las que esta lista muestra; algunas no aparecen.</p>}
    </>
  );
}

/**
 * The route shown: its date name, driver (DRV- or "Repartidor no disponible"), status, stops and assignment cost
 * total; "Agregar entrega" by guía; each stop by its guía (or "Guía no disponible") with keyboard and drag reorder.
 */
export function RouteDetailView({
  route,
  driver,
  guides,
  guide,
  mutating,
  onGuideChange,
  onAdd,
  onReorder,
  onRemove,
}: {
  readonly route: ManualRouteDetail;
  /** "Repartidor DRV-…", "Repartidor no disponible" or "Cargando repartidor…". */
  readonly driver: string;
  readonly guides: Readonly<Record<string, RouteGuide>>;
  readonly guide: string;
  readonly mutating: boolean;
  readonly onGuideChange: (value: string) => void;
  readonly onAdd: () => void;
  readonly onReorder: (stopIds: readonly string[]) => void;
  readonly onRemove: (stop: ManualRouteStop, guide: string | null) => void;
}) {
  const dragged = useRef<string | null>(null);
  const draft = route.status === "DRAFT";
  return (
    <>
      <header>
        <h2>{routeName(route)}</h2>
        <p>{driver} · {routeStatusLabels[route.status]} · {routeStopsText(route.stop_count)}</p>
        <Money cents={route.assignment_cost_cents_total} currency={false} strong />
      </header>
      <form className="opsRouteAdd" onSubmit={(event) => { event.preventDefault(); onAdd(); }}>
        <Field label="Guía (ORD_…)" hint="La orden debe estar asignada a tu flota propia.">
          {(control) => <input {...control} name="guide" type="text" inputMode="text" required autoComplete="off" autoCapitalize="off"
            spellCheck={false} maxLength={64} placeholder="ORD_…" value={guide} onChange={(event) => onGuideChange(event.currentTarget.value)} />}
        </Field>
        <button className="btn btnPrimary" type="submit" disabled={mutating || !draft}>Agregar entrega</button>
      </form>
      <ol className="opsRouteStops">{route.stops.map((stop, index, stops) => {
        const view = guides[stop.order_id] ?? loadingGuide;
        const publicId = view.kind === "ready" ? view.publicId : null;
        const subject = routeStopSubject(stop, publicId);
        return <li key={stop.id} draggable={draft}
          onDragStart={() => { dragged.current = stop.id; }}
          onDragOver={(event) => event.preventDefault()}
          onDrop={() => { const source = dragged.current; dragged.current = null; if (source && source !== stop.id) onReorder(move(stops, source, stop.id)); }}>
          <span className="opsDrag" aria-hidden="true">::</span>
          <div><strong>{stop.sequence}. <StopGuide view={view} /></strong><small>{routeStopTypeLabels[stop.stop_type]} · {routeStopStatusLabels[stop.status]}</small></div>
          <div className="opsStopActions">
            <button type="button" className="btn btnSecondary" aria-label={`Mover ${subject} arriba`} disabled={index === 0 || mutating}
              onClick={() => onReorder(swap(stops, index, index - 1))}>Subir</button>
            <button type="button" className="btn btnSecondary" aria-label={`Mover ${subject} abajo`} disabled={index === stops.length - 1 || mutating}
              onClick={() => onReorder(swap(stops, index, index + 1))}>Bajar</button>
            <button type="button" className="btn btnSecondary" aria-label={`Retirar ${subject} de la ruta`} disabled={mutating}
              onClick={() => onRemove(stop, publicId)}>Retirar</button>
          </div>
        </li>;
      })}</ol>
      {route.stops.length === 0 && <p>La ruta todavía no tiene paradas.</p>}
    </>
  );
}

const loadingGuide: RouteGuide = { kind: "loading" };

function StopGuide({ view }: { readonly view: RouteGuide }) {
  if (view.kind === "ready") return <span className="opsInboxGuide">{view.publicId}</span>;
  return <span>{view.kind === "loading" ? "Cargando guía…" : "Guía no disponible"}</span>;
}

function swap(stops: readonly ManualRouteStop[], left: number, right: number): string[] { const ids = stops.map((stop) => stop.id); [ids[left], ids[right]] = [ids[right], ids[left]]; return ids; }
function move(stops: readonly ManualRouteStop[], source: string, target: string): string[] { const ids = stops.map((stop) => stop.id); const from = ids.indexOf(source); const to = ids.indexOf(target); if (from < 0 || to < 0) return ids; ids.splice(to, 0, ids.splice(from, 1)[0]); return ids; }
