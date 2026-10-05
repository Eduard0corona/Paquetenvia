"use client";

import Link from "next/link";
import { useRef } from "react";
import type { ManualRouteStop } from "../contracts/manual-route";
import { useManualRoutes } from "../state/use-manual-routes";
import { formatMxnCents } from "../contracts/money";

export function ManualRoutesShell() {
  const state = useManualRoutes();
  const dragged = useRef<string | null>(null);
  return (
    <main className="opsShell opsRoutes" aria-busy={state.loading || state.mutating}>
      <header className="opsHeader">
        <div><p className="opsEyebrow">Despacho</p><h1>Rutas manuales</h1><p>Planeacion OWN con PostgreSQL como autoridad.</p></div>
        <div className="opsHeaderStatus"><span>{state.connected ? "Tiempo real conectado" : "Recuperacion REST"}</span>
          <button className="opsPrimary" type="button" onClick={() => void state.refresh()} disabled={state.loading}>Actualizar</button>
          <Link className="opsPrimary" href="/ops/dashboard">Volver a Operaciones</Link></div>
      </header>

      {state.message && <p className="opsAlert" role="status">{state.message}</p>}
      <section className="opsRouteLayout">
        <aside className="opsRoutePanel">
          <h2>Crear ruta</h2>
          <form onSubmit={(event) => {
            event.preventDefault(); const data = new FormData(event.currentTarget);
            void state.create({ driverId: String(data.get("driver")), cityId: String(data.get("city")),
              serviceAreaId: nullable(String(data.get("serviceArea"))), scheduledFor: nullable(String(data.get("scheduledFor"))) });
          }}>
            <Field name="driver" label="Driver OWN (UUID)" required />
            <Field name="city" label="Ciudad (UUID)" required />
            <Field name="serviceArea" label="Area de servicio (UUID opcional)" />
            <label>Fecha<input name="scheduledFor" type="date" /></label>
            <button className="opsPrimary" type="submit" disabled={state.mutating}>Crear DRAFT</button>
          </form>
          <h2>Rutas</h2>
          <ul className="opsRouteList">{state.routes.map((route) => <li key={route.id}>
            <button type="button" aria-current={state.selected?.id === route.id} onClick={() => void state.select(route.id)}>
              <strong>{route.scheduled_for ?? "Sin fecha"}</strong><span>{route.status} · v{route.version}</span>
              <span>{route.stop_count} stops · {money(route.assignment_cost_cents_total)}</span>
            </button></li>)}</ul>
        </aside>

        <section className="opsRouteDetail">
          {state.selected === null ? <p>Selecciona una ruta para abrir su estado autoritativo.</p> : <>
            <header><h2>Ruta {short(state.selected.id)}</h2><p>Driver {short(state.selected.driver_id)} · {state.selected.status} · version {state.selected.version}</p>
              <strong>{money(state.selected.assignment_cost_cents_total)}</strong></header>
            <form className="opsRouteAdd" onSubmit={(event) => { event.preventDefault(); const value = String(new FormData(event.currentTarget).get("order")); void state.addStop(value); }}>
              <Field name="order" label="Orden con assignment OWN (UUID)" required defaultValue={queryOrder()} />
              <button className="opsPrimary" type="submit" disabled={state.mutating || state.selected.status !== "DRAFT"}>Agregar DELIVERY</button>
            </form>
            <ol className="opsRouteStops">{state.selected.stops.map((stop, index, stops) =>
              <li key={stop.id} draggable={state.selected?.status === "DRAFT"}
                onDragStart={() => { dragged.current = stop.id; }}
                onDragOver={(event) => event.preventDefault()}
                onDrop={() => { const source = dragged.current; dragged.current = null; if (source && source !== stop.id) void state.reorder(move(stops, source, stop.id)); }}>
                <span className="opsDrag" aria-hidden="true">::</span><div><strong>{stop.sequence}. Orden {short(stop.order_id)}</strong><small>{stop.stop_type} - {stop.status}</small></div>
                <div className="opsStopActions">
                  <button type="button" aria-label={`Mover orden ${short(stop.order_id)} arriba`} disabled={index === 0 || state.mutating}
                    onClick={() => void state.reorder(swap(stops, index, index - 1))}>Up</button>
                  <button type="button" aria-label={`Mover orden ${short(stop.order_id)} abajo`} disabled={index === stops.length - 1 || state.mutating}
                    onClick={() => void state.reorder(swap(stops, index, index + 1))}>Down</button>
                  <button type="button" disabled={state.mutating} onClick={() => void state.removeStop(stop.id)}>Retirar</button>
                </div>
              </li>)}</ol>
            {state.selected.stops.length === 0 && <p>La ruta todavia no tiene stops.</p>}
          </>}
        </section>
      </section>
    </main>
  );
}

function Field({ name, label, required = false, defaultValue }: { readonly name: string; readonly label: string; readonly required?: boolean; readonly defaultValue?: string }) {
  return <label>{label}<input name={name} required={required} defaultValue={defaultValue} autoComplete="off" /></label>;
}
function nullable(value: string): string | null { return value.trim() || null; }
function short(value: string): string { return value.slice(0, 8); }
function money(cents: number): string { return formatMxnCents(cents); }
function queryOrder(): string { return typeof window === "undefined" ? "" : new URL(window.location.href).searchParams.get("orderId") ?? ""; }
function swap(stops: readonly ManualRouteStop[], left: number, right: number): string[] { const ids = stops.map((stop) => stop.id); [ids[left], ids[right]] = [ids[right], ids[left]]; return ids; }
function move(stops: readonly ManualRouteStop[], source: string, target: string): string[] { const ids = stops.map((stop) => stop.id); const from = ids.indexOf(source); const to = ids.indexOf(target); if (from < 0 || to < 0) return ids; ids.splice(to, 0, ids.splice(from, 1)[0]); return ids; }
