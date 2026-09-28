"use client";

import Link from "next/link";
import {
  incidentNextActionLabels,
  incidentNextActions,
  incidentOutcomeLabels,
  incidentOutcomes,
  incidentReasonCodes,
  incidentReasonLabels,
  incidentSeverities,
  incidentSeverityLabels,
  incidentStatusLabels,
  type Incident,
} from "../contracts/incident";
import { formatMazatlanTime } from "../contracts/operations-formatters";
import { operationsOrderHref } from "../routing/operations-routing";
import type { IncidentsController, IncidentsState } from "../state/incidents-controller";
import { useIncidents } from "../state/use-incidents";
import { ScreenGate, TenantFeedback } from "./tenant-feedback";

export function IncidentsShell() {
  const { state, controller } = useIncidents();
  return (
    <main className="opsShell" aria-busy={state.phase === "loading" || state.busy}>
      <header className="opsHeader">
        <div>
          <p className="opsEyebrow">Despacho</p>
          <h1>Incidencias</h1>
          <p>Registro de intentos fallidos y su resolución con la API como autoridad.</p>
        </div>
        <div className="opsHeaderStatus">
          <Link className="opsPrimary" href="/ops/dashboard">Volver a Operaciones</Link>
        </div>
      </header>

      <ScreenGate phase={state.phase} accessMessage="Tu rol en la organización activa no gestiona incidencias." />
      <TenantFeedback errors={state.errors} message={state.message} stepUpHref={state.stepUpHref} />
      {state.mfaHint !== null && <p className="opsTimezone">{state.mfaHint}</p>}

      {state.phase === "ready" && (
        <>
          <section className="opsFormLayout">
            {state.canOpen && <OpenForm key={`open-${state.formKey}`} state={state} controller={controller} />}
            {state.canResolve && <ResolveForm key={`resolve-${state.formKey}`} state={state} controller={controller} />}
          </section>
          <IncidentList incidents={state.incidents} />
        </>
      )}
    </main>
  );
}

function OpenForm({ state, controller }: { readonly state: IncidentsState; readonly controller: IncidentsController }) {
  return (
    <form className="opsForm" autoComplete="off" onSubmit={(event) => {
      event.preventDefault();
      const data = new FormData(event.currentTarget);
      const value = (name: string) => String(data.get(name) ?? "");
      void controller.open({
        orderId: value("order_id").trim(),
        type: value("type").trim(),
        severity: value("severity"),
        reasonCode: value("reason_code"),
        nextAction: value("next_action"),
        description: value("description"),
        occurredAtLocal: value("occurred_at"),
        evidence: value("evidence"),
      });
    }}>
      <fieldset>
        <legend>Abrir incidencia de intento fallido</legend>
        <label>Orden (UUID)<input name="order_id" required /></label>
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
        <label>Evidencias (UUID de prueba, de 1 a 10, separados por coma o renglón)
          <textarea name="evidence" rows={3} required />
        </label>
        <label>Descripción (no incluyas datos personales innecesarios)
          <textarea name="description" rows={4} maxLength={2000} required />
        </label>
        <button className="opsPrimary" type="submit" disabled={state.busy}>Abrir incidencia</button>
      </fieldset>
    </form>
  );
}

function ResolveForm({ state, controller }: { readonly state: IncidentsState; readonly controller: IncidentsController }) {
  const open = state.incidents.filter((incident) => incident.status === "OPEN" || incident.status === "INVESTIGATING");
  return (
    <form className="opsForm" autoComplete="off" onSubmit={(event) => {
      event.preventDefault();
      const data = new FormData(event.currentTarget);
      void controller.resolve(
        String(data.get("incident_id") ?? "").trim(),
        String(data.get("outcome") ?? ""),
        String(data.get("reason") ?? ""),
      );
    }}>
      <fieldset>
        <legend>Resolver incidencia</legend>
        <p>La resolución no cambia el estado de la orden. Una incidencia cerrada no vuelve a cambiar.</p>
        <label>Incidencia (UUID)
          <input name="incident_id" required list="incidents-open" defaultValue={open[0]?.id ?? ""} />
          <datalist id="incidents-open">
            {open.map((incident) => <option key={incident.id} value={incident.id} />)}
          </datalist>
        </label>
        <label>Resultado
          <select name="outcome" required defaultValue="">
            <option value="" disabled>Elige</option>
            {incidentOutcomes.map((value) => <option key={value} value={value}>{incidentOutcomeLabels[value]}</option>)}
          </select>
        </label>
        <label>Motivo (máximo 500 caracteres; queda en auditoría)
          <textarea name="reason" rows={3} maxLength={500} required />
        </label>
        <button className="opsPrimary" type="submit" disabled={state.busy}>Registrar resolución</button>
      </fieldset>
    </form>
  );
}

function IncidentList({ incidents }: { readonly incidents: readonly Incident[] }) {
  return (
    <section>
      <h2>Incidencias de esta sesión</h2>
      <p>La API no ofrece una consulta de incidencias; aquí aparecen solo las que abriste o resolviste en esta organización.</p>
      {incidents.length === 0 ? <p>Sin incidencias registradas en esta sesión.</p> : (
        <table className="opsTable">
          <caption>Tal como las devolvió el servidor</caption>
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
    </section>
  );
}
