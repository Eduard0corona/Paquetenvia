"use client";

import Link from "next/link";
import { useState } from "react";
import {
  incidentNextActionLabels,
  incidentNextActions,
  incidentOutcomeLabels,
  incidentOutcomes,
  incidentReasonCodes,
  incidentReasonLabels,
  incidentSeverities,
  incidentSeverityLabels,
  incidentStatuses,
  incidentStatusLabels,
  isPending,
  proofTypeLabels,
  type Incident,
  type IncidentStatus,
} from "../contracts/incident";
import { formatMazatlanTime } from "../contracts/operations-formatters";
import { operationsOrderHref } from "../routing/operations-routing";
import type { IncidentsController, IncidentsState } from "../state/incidents-controller";
import { useIncidents } from "../state/use-incidents";
import { ScreenGate, TenantFeedback } from "./tenant-feedback";

export function IncidentsShell() {
  const { state, controller } = useIncidents();
  return (
    <main className="opsShell" aria-busy={state.phase === "loading" || state.busy || state.listing}>
      <header className="opsHeader">
        <div>
          <p className="opsEyebrow">Despacho</p>
          <h1>Incidencias</h1>
          <p>Registra los intentos de entrega fallidos y da seguimiento a su resolución.</p>
        </div>
        <div className="opsHeaderStatus">
          <Link className="btn btnPrimary" href="/ops/dashboard">Volver a Operaciones</Link>
        </div>
      </header>

      <ScreenGate phase={state.phase} accessMessage="Tu rol en la organización activa no gestiona incidencias." />
      <TenantFeedback errors={state.errors} message={state.message} stepUpHref={state.stepUpHref} />
      {state.mfaHint !== null && <p className="pageNote">{state.mfaHint}</p>}

      {state.phase === "ready" && (
        <>
          <section className="opsFormLayout">
            {state.canOpen && <OpenForm key={`open-${state.formKey}`} state={state} controller={controller} />}
            {state.canResolve && <ResolveForm key={`resolve-${state.formKey}`} state={state} controller={controller} />}
          </section>
          <IncidentList state={state} controller={controller} />
        </>
      )}
    </main>
  );
}

function OpenForm({ state, controller }: { readonly state: IncidentsState; readonly controller: IncidentsController }) {
  const [orderId, setOrderId] = useState("");
  const typedOrder = orderId.trim();
  const proofsForOrder = state.proofsOrderId !== null && state.proofsOrderId === typedOrder;
  return (
    <form className="opsForm" autoComplete="off" onSubmit={(event) => {
      event.preventDefault();
      const data = new FormData(event.currentTarget);
      const value = (name: string) => String(data.get(name) ?? "");
      void controller.open({
        orderId: typedOrder,
        type: value("type").trim(),
        severity: value("severity"),
        reasonCode: value("reason_code"),
        nextAction: value("next_action"),
        description: value("description"),
        occurredAtLocal: value("occurred_at"),
        evidence: state.canListProofs
          ? data.getAll("evidence").map(String).join(",")
          : value("evidence"),
      });
    }}>
      <fieldset>
        <legend>Abrir incidencia de intento fallido</legend>
        <label>ID de la orden
          <input name="order_id" required value={orderId} aria-describedby="incident-order-help"
            onChange={(event) => setOrderId(event.target.value)} />
        </label>
        <p id="incident-order-help" className="fieldHint">Pega el ID completo de la orden; lo encuentras al abrirla desde Operaciones.</p>
        <label>Tipo<input name="type" required defaultValue="FAILED_ATTEMPT" pattern="[A-Z_]{1,64}" /></label>
        <label>Severidad
          <select name="severity" required defaultValue="">
            <option value="" disabled>Elige</option>
            {incidentSeverities.map((value) => <option key={value} value={value}>{incidentSeverityLabels[value]}</option>)}
          </select>
        </label>
        <label>Motivo
          <select name="reason_code" required defaultValue="">
            <option value="" disabled>Elige</option>
            {incidentReasonCodes.map((value) => <option key={value} value={value}>{incidentReasonLabels[value]}</option>)}
          </select>
        </label>
        <label>Siguiente acción
          <select name="next_action" required defaultValue="">
            <option value="" disabled>Elige</option>
            {incidentNextActions.map((value) => <option key={value} value={value}>{incidentNextActionLabels[value]}</option>)}
          </select>
        </label>
        <label>Fecha y hora del intento (hora de Mazatlán)<input name="occurred_at" type="datetime-local" required /></label>
        {state.canListProofs ? (
          <fieldset>
            <legend>Evidencias de la orden (elige de 1 a 10)</legend>
            <button
              type="button"
              className="btn btnSecondary"
              disabled={state.listing || typedOrder.length === 0}
              onClick={() => void controller.loadProofs(typedOrder)}
            >
              Ver evidencias de la orden
            </button>
            {!proofsForOrder ? (
              <p>Captura la orden y consulta sus evidencias para elegirlas.</p>
            ) : state.proofs.length === 0 ? (
              <p>La orden no tiene evidencias registradas.</p>
            ) : (
              <ul>
                {state.proofs.map((proof) => (
                  <li key={proof.id}>
                    <label>
                      <input type="checkbox" name="evidence" value={proof.id} />
                      {proofTypeLabels[proof.proof_type]} · capturada {formatMazatlanTime(proof.captured_at)}
                    </label>
                  </li>
                ))}
              </ul>
            )}
            {proofsForOrder && state.proofsCursor !== null && (
              <button type="button" className="btn btnSecondary" disabled={state.listing} onClick={() => void controller.loadMoreProofs()}>
                Cargar más evidencias
              </button>
            )}
          </fieldset>
        ) : (
          <label>IDs de las evidencias (de 1 a 10, separados por coma o renglón)
            <textarea name="evidence" rows={3} required />
          </label>
        )}
        <label>Descripción (no incluyas datos personales innecesarios)
          <textarea name="description" rows={4} maxLength={2000} required />
        </label>
        <button className="btn btnPrimary" type="submit" disabled={state.busy}>Abrir incidencia</button>
      </fieldset>
    </form>
  );
}

function incidentOptionLabel(incident: Incident): string {
  return [
    incidentReasonLabels[incident.reason_code],
    incidentSeverityLabels[incident.severity],
    incidentStatusLabels[incident.status],
    `SLA ${formatMazatlanTime(incident.sla_due_at)}`,
  ].join(" · ");
}

function ResolveForm({ state, controller }: { readonly state: IncidentsState; readonly controller: IncidentsController }) {
  const pending = state.incidents.filter(isPending);
  return (
    <form className="opsForm" autoComplete="off" onSubmit={(event) => {
      event.preventDefault();
      const data = new FormData(event.currentTarget);
      void controller.resolve(
        String(data.get("incident_id") ?? ""),
        String(data.get("outcome") ?? ""),
        String(data.get("reason") ?? ""),
      );
    }}>
      <fieldset>
        <legend>Resolver incidencia</legend>
        <p>La resolución no cambia el estado de la orden. Una incidencia cerrada no vuelve a cambiar.</p>
        {pending.length === 0 ? (
          <p>No hay incidencias pendientes en la lista; actualízala o carga más.</p>
        ) : (
          <label>Incidencia pendiente
            <select name="incident_id" required defaultValue="">
              <option value="" disabled>Elige</option>
              {pending.map((incident) => (
                <option key={incident.id} value={incident.id}>{incidentOptionLabel(incident)}</option>
              ))}
            </select>
          </label>
        )}
        <label>Resultado
          <select name="outcome" required defaultValue="">
            <option value="" disabled>Elige</option>
            {incidentOutcomes.map((value) => <option key={value} value={value}>{incidentOutcomeLabels[value]}</option>)}
          </select>
        </label>
        <label>Motivo (máximo 500 caracteres; queda en auditoría)
          <textarea name="reason" rows={3} maxLength={500} required />
        </label>
        <button className="btn btnPrimary" type="submit" disabled={state.busy || pending.length === 0}>
          Registrar resolución
        </button>
      </fieldset>
    </form>
  );
}

function IncidentList({ state, controller }: { readonly state: IncidentsState; readonly controller: IncidentsController }) {
  const { incidents } = state;
  return (
    <section>
      <h2>Incidencias de la organización</h2>
      {state.canList && (
        <div className="opsFormActions">
          <label className="opsInlineField">Estado
            <select
              value={state.statusFilter ?? ""}
              disabled={state.listing}
              onChange={(event) => void controller.refresh(
                event.target.value === "" ? null : (event.target.value as IncidentStatus),
              )}
            >
              <option value="">Todos</option>
              {incidentStatuses.map((value) => <option key={value} value={value}>{incidentStatusLabels[value]}</option>)}
            </select>
          </label>
          <button type="button" className="btn btnSecondary" disabled={state.listing} onClick={() => void controller.refresh()}>Actualizar</button>
        </div>
      )}
      {incidents.length === 0 ? <p>Sin incidencias para mostrar.</p> : (
        <table className="opsTable">
          <caption>Incidencias registradas</caption>
          <thead>
            <tr>
              <th scope="col">Incidencia</th><th scope="col">Orden</th><th scope="col">Estado</th>
              <th scope="col">Severidad</th><th scope="col">Motivo</th><th scope="col">Siguiente acción</th>
              <th scope="col">Custodia</th><th scope="col">Ocurrió</th><th scope="col">SLA</th>
            </tr>
          </thead>
          <tbody>
            {incidents.map((incident) => (
              <tr key={incident.id}>
                <td>{incident.id}</td>
                <td><Link href={operationsOrderHref(incident.order_id)}>Abrir orden</Link></td>
                <td>{incidentStatusLabels[incident.status]}</td>
                <td>{incidentSeverityLabels[incident.severity]}</td>
                <td>{incidentReasonLabels[incident.reason_code]}</td>
                <td>{incidentNextActionLabels[incident.next_action]}</td>
                <td>{incident.custody_acquired ? "Con custodia" : "Sin custodia"}</td>
                <td>{formatMazatlanTime(incident.occurred_at)}</td>
                <td>{formatMazatlanTime(incident.sla_due_at)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {state.canList && state.nextCursor !== null && (
        <button type="button" className="btn btnSecondary" disabled={state.listing} onClick={() => void controller.loadMore()}>Cargar más</button>
      )}
    </section>
  );
}
