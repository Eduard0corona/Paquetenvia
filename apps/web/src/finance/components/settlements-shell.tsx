"use client";

import { useConfirmDialog, type ConfirmRequest } from "../../components/confirm-dialog";
import { DateTime } from "../../components/ui/date-time";
import { EmptyState } from "../../components/ui/empty-state";
import { Feedback, ScreenGate } from "../../components/ui/feedback";
import { Money } from "../../components/ui/money";
import { PageHeader } from "../../components/ui/page-header";
import { shortId } from "../../lib/short-id";
import {
  settlementAdjustmentConfirmation,
  settlementApprovalConfirmation,
  settlementPaymentConfirmation,
  settlementVoidConfirmation,
} from "../contracts/confirmations";
import {
  settlementLineTypeLabels,
  settlementStatuses,
  settlementStatusLabels,
  type Settlement,
} from "../contracts/settlement";
import {
  settlementMfaHint,
  visibleSettlementActions,
  type SettlementsController,
  type SettlementsState,
} from "../state/settlements-controller";
import { useSettlements } from "../state/use-settlements";

export function SettlementsShell() {
  const { state, controller } = useSettlements();
  const { confirm, dialog } = useConfirmDialog();
  return (
    <div className="page" aria-busy={state.loading || state.busy}>
      <PageHeader
        eyebrow="Finanzas"
        title="Liquidaciones"
        description="Calcula, revisa, aprueba y paga las liquidaciones de tus repartidores."
        actions={
          <button className="btn btnSecondary" type="button" disabled={state.phase !== "ready" || state.loading}
            onClick={() => void controller.refresh()}>Actualizar</button>
        }
      />

      <ScreenGate phase={state.phase} accessMessage="Tu rol en la organización activa no opera liquidaciones." />
      <Feedback errors={state.errors} message={state.message} stepUpHref={state.stepUpHref} />

      {state.phase === "ready" && (
        <section className="opsRouteLayout">
          <aside className="panel opsRoutePanel">
            <Filters key={`filters-${state.formKey}`} controller={controller} disabled={state.loading} />
            {state.canCreate && <CreateForm key={`create-${state.formKey}`} controller={controller} disabled={state.busy} />}
            <h2>Liquidaciones</h2>
            {state.items.length === 0 && !state.loading && <EmptyState>Sin liquidaciones para estos filtros.</EmptyState>}
            <ul className="opsRouteList">
              {state.items.map((item) => (
                <li key={item.id}>
                  <button type="button" aria-current={state.selected?.id === item.id}
                    onClick={() => void controller.select(item.id)}>
                    <strong>{item.period_from} a {item.period_to}</strong>
                    <span>{settlementStatusLabels[item.status]} · <Money cents={item.total_cents} /></span>
                    <span>Repartidor {shortId(item.payee_id)} · {item.lines.length} línea(s)</span>
                  </button>
                </li>
              ))}
            </ul>
            {state.nextCursor !== null && (
              <button type="button" className="btn btnSecondary" disabled={state.loading}
                onClick={() => void controller.loadMore()}>
                {state.loading ? "Cargando…" : "Cargar más"}
              </button>
            )}
          </aside>
          <section className="panel opsRouteDetail">
            {state.selected === null
              ? <EmptyState>Selecciona una liquidación para ver sus líneas.</EmptyState>
              : <Detail key={`${state.selected.id}-${state.formKey}`} settlement={state.selected} state={state}
                controller={controller} confirm={confirm} />}
          </section>
        </section>
      )}
      {dialog}
    </div>
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
      <label>ID del repartidor (opcional)<input name="payee_id" aria-describedby="settlement-filter-payee-help" /></label>
      <p id="settlement-filter-payee-help" className="fieldHint">Pega el ID completo del repartidor; déjalo vacío para ver todos.</p>
      <label>Periodo desde<input name="period_from" type="date" /></label>
      <label>Periodo hasta<input name="period_to" type="date" /></label>
      <button className="btn btnSecondary" type="submit" disabled={disabled}>Aplicar filtros</button>
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
      <label>ID del repartidor<input name="driver_id" required aria-describedby="settlement-create-driver-help" /></label>
      <p id="settlement-create-driver-help" className="fieldHint">Pega el ID completo del repartidor.</p>
      <label>Periodo desde<input name="period_from" type="date" required /></label>
      <label>Periodo hasta<input name="period_to" type="date" required /></label>
      <button className="btn btnPrimary" type="submit" disabled={disabled}>Calcular</button>
    </form>
  );
}

function Detail({
  settlement,
  state,
  controller,
  confirm,
}: {
  readonly settlement: Settlement;
  readonly state: SettlementsState;
  readonly controller: SettlementsController;
  readonly confirm: (request: ConfirmRequest) => void;
}) {
  const actions = visibleSettlementActions(state.role, settlement.status);
  const mfaHint = settlementMfaHint(state.role, actions);
  // While another settlement is loading, no action may target the one still shown.
  const locked = state.busy || state.selecting !== null;
  return (
    <>
      <header>
        <h2>Liquidación {shortId(settlement.id)}</h2>
        <p>
          {settlementStatusLabels[settlement.status]} · Repartidor {shortId(settlement.payee_id)} ·
          {" "}{settlement.period_from} a {settlement.period_to} · creada <DateTime value={settlement.created_at} />
        </p>
        <strong>Total <Money cents={settlement.total_cents} /></strong>
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
              <td>{line.order_id === null ? "—" : shortId(line.order_id)}</td>
              <td className="opsAmount"><Money cents={line.amount_cents} /></td>
              <td><DateTime value={line.created_at} /></td>
            </tr>
          ))}
          {settlement.lines.length === 0 && <tr><td colSpan={4}>Sin líneas; total 0.</td></tr>}
        </tbody>
      </table>

      {mfaHint !== null && <p className="notice noticeInfo">{mfaHint}</p>}

      <div className="opsFormActions">
        {actions.includes("approve") && (
          <button type="button" className="btn btnPrimary" disabled={locked}
            onClick={() => confirm({
              ...settlementApprovalConfirmation(settlement),
              onConfirm: () => void controller.approve(),
            })}>Aprobar</button>
        )}
        {actions.includes("pay") && (
          <button type="button" className="btn btnPrimary" disabled={locked}
            onClick={() => confirm({
              ...settlementPaymentConfirmation(settlement),
              onConfirm: () => void controller.markPaid(),
            })}>Marcar pagada</button>
        )}
        {actions.includes("export") && (
          <button type="button" className="btn btnSecondary" disabled={locked}
            onClick={() => void controller.exportCsv()}>Exportar CSV</button>
        )}
        <button type="button" className="btn btnSecondary" onClick={() => controller.clearSelection()}>Cerrar</button>
      </div>

      {actions.includes("adjust") && (
        <form className="opsForm" autoComplete="off" onSubmit={(event) => {
          event.preventDefault();
          const data = new FormData(event.currentTarget);
          const amount = String(data.get("amount") ?? "");
          const reason = String(data.get("reason") ?? "");
          const text = settlementAdjustmentConfirmation(settlement, amount);
          if (text === null) {
            void controller.addAdjustment(amount, reason);
            return;
          }
          confirm({ ...text, onConfirm: () => void controller.addAdjustment(amount, reason) });
        }}>
          <h3>Agregar ajuste</h3>
          <label>Monto en MXN (negativo para descontar)
            <input name="amount" inputMode="decimal" pattern="-?[0-9]+(\.[0-9]{1,2})?" required />
          </label>
          <label>Motivo<textarea name="reason" maxLength={500} required /></label>
          <button className="btn btnPrimary" type="submit" disabled={locked}>Agregar ajuste</button>
        </form>
      )}

      {actions.includes("void") && (
        <form className="opsForm" autoComplete="off" onSubmit={(event) => {
          event.preventDefault();
          const reason = String(new FormData(event.currentTarget).get("reason") ?? "");
          confirm({
            ...settlementVoidConfirmation(settlement),
            onConfirm: () => void controller.voidSettlement(reason),
          });
        }}>
          <h3>Anular liquidación</h3>
          <p>Las líneas y el total se conservan; anular solo libera sus fuentes para otra liquidación.</p>
          <label>Motivo<textarea name="reason" maxLength={500} required /></label>
          <button className="btn btnDanger" type="submit" disabled={locked}>Anular</button>
        </form>
      )}
    </>
  );
}
