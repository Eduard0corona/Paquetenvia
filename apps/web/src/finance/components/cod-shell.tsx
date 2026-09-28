"use client";

import Link from "next/link";
import { ScreenGate, TenantFeedback } from "../../operations/components/tenant-feedback";
import { formatMxnCentsWithCurrency } from "../../operations/contracts/money";
import { formatMazatlanTime, orderStatusLabels } from "../../operations/contracts/operations-formatters";
import { operationsOrderHref } from "../../operations/routing/operations-routing";
import {
  canRecordCollection,
  codStatusLabels,
  formatBasisPoints,
  modalityLabels,
  type CodTransaction,
  type OrderFinancials,
} from "../contracts/cod";
import type { CodController, CodState } from "../state/cod-controller";
import { useCod } from "../state/use-cod";

export function CodShell() {
  const { state, controller } = useCod();
  return (
    <main className="opsShell" aria-busy={state.phase === "loading" || state.loadingOrder !== null || state.busy}>
      <header className="opsHeader">
        <div>
          <p className="opsEyebrow">Finanzas</p>
          <h1>Cobro contra entrega</h1>
          <p>Registro y conciliación de efectivo por orden con la API como autoridad. Montos en centavos exactos.</p>
        </div>
        <div className="opsHeaderStatus">
          <button className="opsPrimary" type="button"
            disabled={state.phase !== "ready" || state.financials === null || state.loadingOrder !== null}
            onClick={() => void controller.refresh()}>Actualizar</button>
        </div>
      </header>

      <ScreenGate phase={state.phase} accessMessage="Tu rol en la organización activa no consulta cobros contra entrega." />
      <TenantFeedback errors={state.errors} message={state.message} stepUpHref={state.stepUpHref} />
      {state.mfaHint !== null && <p className="opsTimezone">{state.mfaHint}</p>}

      {state.phase === "ready" && (
        <section className="opsFormLayout">
          <div>
            <LookupForm key={`lookup-${state.formKey}`} controller={controller} disabled={state.loadingOrder !== null} />
            {state.canReconcile && (
              <ReconcileByIdForm key={`reconcile-${state.formKey}`} controller={controller} disabled={state.busy} />
            )}
          </div>
          <section>
            {state.loadingOrder !== null && <p className="opsLive" aria-live="polite">Cargando la orden.</p>}
            {state.financials !== null && state.loadingOrder === null && (
              <Financials key={`${state.financials.order_id}-${state.formKey}`} financials={state.financials}
                state={state} controller={controller} />
            )}
            {state.transaction !== null && <Transaction transaction={state.transaction} state={state} controller={controller} />}
          </section>
        </section>
      )}
    </main>
  );
}

function LookupForm({ controller, disabled }: { readonly controller: CodController; readonly disabled: boolean }) {
  return (
    <form className="opsForm" autoComplete="off" onSubmit={(event) => {
      event.preventDefault();
      void controller.load(String(new FormData(event.currentTarget).get("order_id") ?? "").trim());
    }}>
      <fieldset>
        <legend>Consultar orden</legend>
        <label>Orden (UUID)<input name="order_id" required /></label>
        <button className="opsSecondary" type="submit" disabled={disabled}>Consultar</button>
      </fieldset>
    </form>
  );
}

function ReconcileByIdForm({ controller, disabled }: { readonly controller: CodController; readonly disabled: boolean }) {
  return (
    <form className="opsForm" autoComplete="off" onSubmit={(event) => {
      event.preventDefault();
      void controller.reconcile(String(new FormData(event.currentTarget).get("cod_id") ?? "").trim());
    }}>
      <fieldset>
        <legend>Conciliar por registro de cobro</legend>
        <p>La API no permite consultar registros de cobro; usa esta opción solo si conoces el identificador.</p>
        <label>Registro de cobro (UUID)<input name="cod_id" required /></label>
        <button className="opsSecondary" type="submit" disabled={disabled}>Conciliar</button>
      </fieldset>
    </form>
  );
}

function Financials({
  financials,
  state,
  controller,
}: {
  readonly financials: OrderFinancials;
  readonly state: CodState;
  readonly controller: CodController;
}) {
  const cod = financials.cod;
  return (
    <>
      <h2>Orden {financials.order_id}</h2>
      <p>
        Estado: {orderStatusLabels[financials.order_status]} ·{" "}
        <Link href={operationsOrderHref(financials.order_id)}>Abrir orden</Link>
      </p>
      <dl className="opsMoneyList">
        <div><dt>Ingreso</dt><dd>{formatMxnCentsWithCurrency(financials.revenue_cents)}</dd></div>
        {financials.cost_by_modality.map((bucket) => (
          <div key={bucket.modality}>
            <dt>Costo {modalityLabels[bucket.modality]} ({bucket.assignment_count})</dt>
            <dd>{formatMxnCentsWithCurrency(bucket.cost_cents)}</dd>
          </div>
        ))}
        <div><dt>Costo total</dt><dd>{formatMxnCentsWithCurrency(financials.cost_cents)}</dd></div>
        <div>
          <dt>Margen</dt>
          <dd>
            {formatMxnCentsWithCurrency(financials.margin_cents)}
            {financials.margin_basis_points !== null && ` (${formatBasisPoints(financials.margin_basis_points)})`}
          </dd>
        </div>
      </dl>

      <h3>Cobro contra entrega</h3>
      {cod.expected_cents === 0 && cod.status === null ? <p>La orden no tiene cobro contra entrega.</p> : (
        <dl className="opsMoneyList">
          <div><dt>Esperado</dt><dd>{formatMxnCentsWithCurrency(cod.expected_cents)}</dd></div>
          <div><dt>Estado</dt><dd>{cod.status === null ? "Sin cobro registrado" : codStatusLabels[cod.status]}</dd></div>
          {cod.amount_cents !== null && (
            <div><dt>Cobrado</dt><dd>{formatMxnCentsWithCurrency(cod.amount_cents)}</dd></div>
          )}
          <div><dt>Permite entregar</dt><dd>{cod.satisfies_delivery_requirement ? "Sí" : "No"}</dd></div>
          <div><dt>Permite cerrar</dt><dd>{cod.satisfies_close_requirement ? "Sí" : "No"}</dd></div>
        </dl>
      )}

      {state.canRecord && canRecordCollection(financials) && (
        <form className="opsForm" autoComplete="off" onSubmit={(event) => {
          event.preventDefault();
          const data = new FormData(event.currentTarget);
          void controller.record(String(data.get("amount") ?? ""), String(data.get("reference") ?? ""));
        }}>
          <fieldset>
            <legend>Registrar cobro</legend>
            <p>El monto debe ser exactamente {formatMxnCentsWithCurrency(cod.expected_cents)}.</p>
            <label>Monto cobrado (MXN)<input name="amount" inputMode="decimal" required /></label>
            <label>Referencia (máximo 200 caracteres, sin espacios al inicio o al final)
              <input name="reference" maxLength={200} required />
            </label>
            <button className="opsPrimary" type="submit" disabled={state.busy}>Registrar cobro</button>
          </fieldset>
        </form>
      )}
      {cod.status === "RECORDED" && state.canReconcile && state.transaction?.order_id !== financials.order_id && (
        <p>El cobro está registrado. Para conciliarlo indica el registro de cobro; la API no lo expone en esta consulta.</p>
      )}
    </>
  );
}

function Transaction({
  transaction,
  state,
  controller,
}: {
  readonly transaction: CodTransaction;
  readonly state: CodState;
  readonly controller: CodController;
}) {
  return (
    <section>
      <h3>Registro de cobro {transaction.id}</h3>
      <dl className="opsMoneyList">
        <div><dt>Monto</dt><dd>{formatMxnCentsWithCurrency(transaction.amount_cents)}</dd></div>
        <div><dt>Estado</dt><dd>{codStatusLabels[transaction.status]}</dd></div>
        <div><dt>Registrado</dt><dd>{transaction.recorded_at === null ? "—" : formatMazatlanTime(transaction.recorded_at)}</dd></div>
        <div><dt>Conciliado</dt><dd>{transaction.reconciled_at === null ? "—" : formatMazatlanTime(transaction.reconciled_at)}</dd></div>
      </dl>
      {transaction.status === "RECORDED" && state.canReconcile && (
        <button className="opsPrimary" type="button" disabled={state.busy} onClick={() => void controller.reconcile()}>
          Conciliar este cobro
        </button>
      )}
    </section>
  );
}
