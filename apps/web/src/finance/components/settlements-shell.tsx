"use client";

import Link from "next/link";
import { formatMxnCentsWithCurrency } from "../../operations/contracts/money";
import { formatMazatlanTime } from "../../operations/contracts/operations-formatters";
import {
  settlementLineTypeLabels,
  settlementStatuses,
  settlementStatusLabels,
  type Settlement,
} from "../contracts/settlement";
import {
  actionNeedsMfa,
  visibleSettlementActions,
  type SettlementsController,
  type SettlementsState,
} from "../state/settlements-controller";
import { useSettlements } from "../state/use-settlements";

export function SettlementsShell() {
  const { state, controller } = useSettlements();
  return (
    <main className="opsShell" aria-busy={state.loading || state.busy}>
      <header className="opsHeader">
        <div>
          <p className="opsEyebrow">Finanzas</p>
          <h1>Liquidaciones</h1>
          <p>Liquidaciones de repartidores con la API como autoridad. Montos en centavos exactos.</p>
        </div>
        <div className="opsHeaderStatus">
          <button className="opsPrimary" type="button" disabled={state.phase !== "ready" || state.loading}
            onClick={() => void controller.refresh()}>Actualizar</button>
        </div>
      </header>

      {state.phase === "no_session" && (
        <section className="opsMessage" role="alert">
          <h2>Sin sesión</h2>
          <p>Inicia sesión y selecciona una organización.</p>
        </section>
      )}
      {state.phase === "access_unavailable" && (
        <section className="opsMessage" role="alert">
          <h2>Acceso no disponible</h2>
          <p>Tu rol en la organización activa no opera liquidaciones.</p>
        </section>
      )}
      {state.phase === "loading" && <p className="opsLive" aria-live="polite">Cargando permisos.</p>}

      <Feedback state={state} />

      {state.phase === "ready" && (
        <section className="opsRouteLayout">
          <aside className="opsRoutePanel">
            <Filters key={`filters-${state.formKey}`} controller={controller} disabled={state.loading} />
            {state.canCreate && <CreateForm key={`create-${state.formKey}`} controller={controller} disabled={state.busy} />}
            <h2>Liquidaciones</h2>
            {state.items.length === 0 && !state.loading && <p>Sin liquidaciones para estos filtros.</p>}
            <ul className="opsRouteList">
              {state.items.map((item) => (
                <li key={item.id}>
                  <button type="button" aria-current={state.selected?.id === item.id}
                    onClick={() => void controller.select(item.id)}>
                    <strong>{item.period_from} a {item.period_to}</strong>
                    <span>{settlementStatusLabels[item.status]} · {formatMxnCentsWithCurrency(item.total_cents)}</span>
                    <span>Repartidor {short(item.payee_id)} · {item.lines.length} línea(s)</span>
                  </button>
                </li>
              ))}
            </ul>
            {state.nextCursor !== null && (
              <button type="button" className="opsLoadMore" disabled={state.loading}
                onClick={() => void controller.loadMore()}>
                {state.loading ? "Cargando…" : "Cargar más"}
              </button>
            )}
          </aside>
          <section className="opsRouteDetail">
            {state.selected === null
              ? <p>Selecciona una liquidación para ver sus líneas.</p>
              : <Detail key={`${state.selected.id}-${state.formKey}`} settlement={state.selected} state={state} controller={controller} />}
          </section>
        </section>
      )}
    </main>
  );
}

function Feedback({ state }: { readonly state: SettlementsState }) {
  return (
    <>
      {state.errors.length > 0 && (
        <ul className="opsAlert" role="alert">
          {state.errors.map((error) => <li key={error}>{error}</li>)}
        </ul>
      )}
      {state.message !== null && (
        <p className={state.stepUpHref === null ? "opsWarning" : "opsAlert"} role="status">
          {state.message}{" "}
          {state.stepUpHref !== null && <Link className="opsPrimary" href={state.stepUpHref}>Verificar identidad</Link>}
        </p>
      )}
    </>
  );
}

function Filters({ controller, disabled }: { readonly controller: SettlementsController; readonly disabled: boolean }) {
  return (
    <form className="opsForm" autoComplete="off" onSubmit={(event) => {
      event.preventDefault();
      const data = new FormData(event.currentTarget);
      const value = (name: string) => String(data.get(name) ?? "").trim() || undefined;
      void controller.applyFilters({
        payeeId: value("payee_id"),
        status: value("status"),
        periodFrom: value("period_from"),
        periodTo: value("period_to"),
      });
    }}>
      <h2>Filtros</h2>
      <label>Estado
        <select name="status" defaultValue="">
          <option value="">Todos</option>
          {settlementStatuses.map((status) => <option key={status} value={status}>{settlementStatusLabels[status]}</option>)}
        </select>
      </label>
      <label>Repartidor (UUID)<input name="payee_id" /></label>
      <label>Periodo desde<input name="period_from" type="date" /></label>
      <label>Periodo hasta<input name="period_to" type="date" /></label>
      <button className="opsSecondary" type="submit" disabled={disabled}>Aplicar filtros</button>
    </form>
  );
}

function CreateForm({ controller, disabled }: { readonly controller: SettlementsController; readonly disabled: boolean }) {
  return (
    <form className="opsForm" autoComplete="off" onSubmit={(event) => {
      event.preventDefault();
      const data = new FormData(event.currentTarget);
      void controller.create({
        driver_id: String(data.get("driver_id") ?? "").trim(),
        period_from: String(data.get("period_from") ?? ""),
        period_to: String(data.get("period_to") ?? ""),
      });
    }}>
      <h2>Calcular liquidación</h2>
      <p>Solo periodos ya cerrados (días operativos completos, hora de Mazatlán).</p>
      <label>Repartidor (UUID)<input name="driver_id" required /></label>
      <label>Periodo desde<input name="period_from" type="date" required /></label>
      <label>Periodo hasta<input name="period_to" type="date" required /></label>
      <button className="opsPrimary" type="submit" disabled={disabled}>Calcular</button>
    </form>
  );
}

function Detail({
  settlement,
  state,
  controller,
}: {
  readonly settlement: Settlement;
  readonly state: SettlementsState;
  readonly controller: SettlementsController;
}) {
  const actions = visibleSettlementActions(state.role, settlement.status);
  const mfaHint = actions.some((action) => actionNeedsMfa(state.role, action));
  // While another settlement is loading, no action may target the one still shown.
  const locked = state.busy || state.selecting !== null;
  return (
    <>
      <header>
        <h2>Liquidación {short(settlement.id)}</h2>
        <p>
          {settlementStatusLabels[settlement.status]} · Repartidor {short(settlement.payee_id)} ·
          {" "}{settlement.period_from} a {settlement.period_to} · creada {formatMazatlanTime(settlement.created_at)}
        </p>
        <strong>Total {formatMxnCentsWithCurrency(settlement.total_cents)}</strong>
      </header>
      <table className="opsTable">
        <caption>Líneas inmutables, en orden de creación</caption>
        <thead>
          <tr><th scope="col">Tipo</th><th scope="col">Orden</th><th scope="col">Monto</th><th scope="col">Registrada</th></tr>
        </thead>
        <tbody>
          {settlement.lines.map((line) => (
            <tr key={line.id}>
              <td>{settlementLineTypeLabels[line.line_type]}</td>
              <td>{line.order_id === null ? "—" : short(line.order_id)}</td>
              <td className="opsAmount">{formatMxnCentsWithCurrency(line.amount_cents)}</td>
              <td>{formatMazatlanTime(line.created_at)}</td>
            </tr>
          ))}
          {settlement.lines.length === 0 && <tr><td colSpan={4}>Sin líneas; total 0.</td></tr>}
        </tbody>
      </table>

      {mfaHint && <p className="opsTimezone">Aprobar y marcar pagada requieren verificar tu identidad (MFA).</p>}

      <div className="opsFormActions">
        {actions.includes("approve") && (
          <button type="button" className="opsPrimary" disabled={locked}
            onClick={() => void controller.approve()}>Aprobar</button>
        )}
        {actions.includes("pay") && (
          <button type="button" className="opsPrimary" disabled={locked}
            onClick={() => void controller.markPaid()}>Marcar pagada</button>
        )}
        {actions.includes("export") && (
          <button type="button" className="opsSecondary" disabled={locked}
            onClick={() => void controller.exportCsv()}>Exportar CSV</button>
        )}
        <button type="button" className="opsSecondary" onClick={() => controller.clearSelection()}>Cerrar</button>
      </div>

      {actions.includes("adjust") && (
        <form className="opsForm" autoComplete="off" onSubmit={(event) => {
          event.preventDefault();
          const data = new FormData(event.currentTarget);
          void controller.addAdjustment(String(data.get("amount") ?? ""), String(data.get("reason") ?? ""));
        }}>
          <h3>Agregar ajuste</h3>
          <label>Monto en MXN (negativo para descontar)
            <input name="amount" inputMode="decimal" pattern="-?[0-9]+(\.[0-9]{1,2})?" required />
          </label>
          <label>Motivo<textarea name="reason" maxLength={500} required /></label>
          <button className="opsPrimary" type="submit" disabled={locked}>Agregar ajuste</button>
        </form>
      )}

      {actions.includes("void") && (
        <form className="opsForm" autoComplete="off" onSubmit={(event) => {
          event.preventDefault();
          void controller.voidSettlement(String(new FormData(event.currentTarget).get("reason") ?? ""));
        }}>
          <h3>Anular liquidación</h3>
          <p>Las líneas y el total se conservan; anular solo libera sus fuentes para otra liquidación.</p>
          <label>Motivo<textarea name="reason" maxLength={500} required /></label>
          <button className="opsSecondary" type="submit" disabled={locked}>Anular</button>
        </form>
      )}
    </>
  );
}

function short(value: string): string {
  return value.slice(0, 8);
}
