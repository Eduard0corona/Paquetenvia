"use client";

import { useRef } from "react";
import { useConfirmDialog } from "../../components/confirm-dialog";
import {
  removeRouteStopConfirmation,
  routeStatusLabels,
  routeStopStatusLabels,
  routeStopTypeLabels,
  type ManualRouteStop,
} from "../contracts/manual-route";
import { useManualRoutes } from "../state/use-manual-routes";
import { EmptyState } from "../../components/ui/empty-state";
import { Field } from "../../components/ui/field";
import { Money } from "../../components/ui/money";
import { PageHeader } from "../../components/ui/page-header";
import { shortId } from "../../lib/short-id";

export function ManualRoutesShell() {
  const state = useManualRoutes();
  const dragged = useRef<string | null>(null);
  const { confirm, dialog } = useConfirmDialog();
  return (
    <div className="page" aria-busy={state.loading || state.mutating}>
      <PageHeader
        eyebrow="Despacho"
        title="Rutas manuales"
        description="Planeación de rutas para tu flota propia."
        live
        actions={<>
          <span>{state.connected ? "Tiempo real conectado" : "Actualización manual"}</span>
          <button className="btn btnSecondary" type="button" onClick={() => void state.refresh()} disabled={state.loading}>Actualizar</button>
        </>}
      />

      {state.message && <p className="notice noticeCrit" role="status">{state.message}</p>}
      <section className="opsRouteLayout">
        <aside className="panel opsRoutePanel">
          <h2>Crear ruta</h2>
          <form onSubmit={(event) => {
            event.preventDefault(); const data = new FormData(event.currentTarget);
            void state.create({ driverId: String(data.get("driver")), cityId: String(data.get("city")),
              serviceAreaId: nullable(String(data.get("serviceArea"))), scheduledFor: nullable(String(data.get("scheduledFor"))) });
          }}>
            <TextField name="driver" label="ID del repartidor (flota propia)" help="Pega el ID completo del repartidor." required />
            <TextField name="city" label="ID de la ciudad" help="Pega el ID completo de la ciudad." required />
            <TextField name="serviceArea" label="ID del área de servicio (opcional)" help="Déjalo vacío si la ruta cubre toda la ciudad." />
            <label>Fecha<input name="scheduledFor" type="date" /></label>
            <button className="btn btnPrimary" type="submit" disabled={state.mutating}>Crear ruta (borrador)</button>
          </form>
          <h2>Rutas</h2>
          <ul className="opsRouteList">{state.routes.map((route) => <li key={route.id}>
            <button type="button" aria-current={state.selected?.id === route.id} onClick={() => void state.select(route.id)}>
              <strong>{route.scheduled_for ?? "Sin fecha"}</strong><span>{routeStatusLabels[route.status]}</span>
              <span>{route.stop_count} {route.stop_count === 1 ? "parada" : "paradas"} · <Money cents={route.assignment_cost_cents_total} currency={false} /></span>
            </button></li>)}</ul>
        </aside>

        <section className="panel opsRouteDetail">
          {state.selected === null ? <EmptyState>Selecciona una ruta para ver sus paradas.</EmptyState> : <>
            <header><h2>Ruta {shortId(state.selected.id)}</h2><p>Repartidor {shortId(state.selected.driver_id)} · {routeStatusLabels[state.selected.status]} · versión {state.selected.version}</p>
              <Money cents={state.selected.assignment_cost_cents_total} currency={false} strong /></header>
            <form className="opsRouteAdd" onSubmit={(event) => { event.preventDefault(); const value = String(new FormData(event.currentTarget).get("order")); void state.addStop(value); }}>
              <TextField name="order" label="ID de la orden" help="La orden debe estar asignada a tu flota propia." required defaultValue={queryOrder()} />
              <button className="btn btnPrimary" type="submit" disabled={state.mutating || state.selected.status !== "DRAFT"}>Agregar entrega</button>
            </form>
            <ol className="opsRouteStops">{state.selected.stops.map((stop, index, stops) =>
              <li key={stop.id} draggable={state.selected?.status === "DRAFT"}
                onDragStart={() => { dragged.current = stop.id; }}
                onDragOver={(event) => event.preventDefault()}
                onDrop={() => { const source = dragged.current; dragged.current = null; if (source && source !== stop.id) void state.reorder(move(stops, source, stop.id)); }}>
                <span className="opsDrag" aria-hidden="true">::</span><div><strong>{stop.sequence}. Orden {shortId(stop.order_id)}</strong><small>{routeStopTypeLabels[stop.stop_type]} · {routeStopStatusLabels[stop.status]}</small></div>
                <div className="opsStopActions">
                  <button type="button" className="btn btnSecondary" aria-label={`Mover orden ${shortId(stop.order_id)} arriba`} disabled={index === 0 || state.mutating}
                    onClick={() => void state.reorder(swap(stops, index, index - 1))}>Subir</button>
                  <button type="button" className="btn btnSecondary" aria-label={`Mover orden ${shortId(stop.order_id)} abajo`} disabled={index === stops.length - 1 || state.mutating}
                    onClick={() => void state.reorder(swap(stops, index, index + 1))}>Bajar</button>
                  <button type="button" className="btn btnSecondary" aria-label={`Retirar orden ${shortId(stop.order_id)} de la ruta`} disabled={state.mutating}
                    onClick={() => confirm({
                      ...removeRouteStopConfirmation(stop),
                      onConfirm: () => void state.removeStop(stop.id),
                    })}>Retirar</button>
                </div>
              </li>)}</ol>
            {state.selected.stops.length === 0 && <p>La ruta todavía no tiene paradas.</p>}
          </>}
        </section>
      </section>
      {dialog}
    </div>
  );
}

function TextField({ name, label, help, required = false, defaultValue }: { readonly name: string; readonly label: string; readonly help?: string; readonly required?: boolean; readonly defaultValue?: string }) {
  return (
    <Field label={label} hint={help}>
      {(control) => <input {...control} name={name} required={required} defaultValue={defaultValue} autoComplete="off" />}
    </Field>
  );
}
function nullable(value: string): string | null { return value.trim() || null; }
function queryOrder(): string { return typeof window === "undefined" ? "" : new URL(window.location.href).searchParams.get("orderId") ?? ""; }
function swap(stops: readonly ManualRouteStop[], left: number, right: number): string[] { const ids = stops.map((stop) => stop.id); [ids[left], ids[right]] = [ids[right], ids[left]]; return ids; }
function move(stops: readonly ManualRouteStop[], source: string, target: string): string[] { const ids = stops.map((stop) => stop.id); const from = ids.indexOf(source); const to = ids.indexOf(target); if (from < 0 || to < 0) return ids; ids.splice(to, 0, ids.splice(from, 1)[0]); return ids; }
