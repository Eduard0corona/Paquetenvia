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
  type PendingCodOrder,
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
            {state.canListPending && <PendingList state={state} controller={controller} />}
            <LookupForm key={`lookup-${state.formKey}`} controller={controller} disabled={state.loadingOrder !== null} />
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

/** API-FIN-COD-VISIBILITY-2026-09-29: collections recorded and not yet reconciled, from listOrders. */
function PendingList({ state, controller }: { readonly state: CodState; readonly controller: CodController }) {
  const busy = state.busy || state.loadingOrder !== null;
  return (
    <section aria-busy={state.pendingLoading}>
      <h2>Cobros pendientes de conciliar</h2>
      <button className="opsSecondary" type="button" disabled={state.pendingLoading}
        onClick={() => void controller.loadPending()}>Actualizar lista</button>
      {state.pending === null ? (
        state.pendingLoading ? <p className="opsLive" aria-live="polite">Cargando cobros pendientes.</p> : null
      ) : state.pending.length === 0 ? (
        <p>No hay cobros registrados pendientes de conciliar.</p>
      ) : (
        <ul>
          {state.pending.map((order) => (
            <PendingRow key={order.id} order={order} busy={busy} canReconcile={state.canReconcile} controller={controller} />
          ))}
        </ul>
      )}
      {state.pendingCursor !== null && (
        <button className="opsSecondary" type="button" disabled={state.pendingLoading}
          onClick={() => void controller.loadPending(true)}>Cargar más</button>
      )}
    </section>
  );
}

function PendingRow({
  order,
  busy,
  canReconcile,
  controller,
}: {
  readonly order: PendingCodOrder;
  readonly busy: boolean;
  readonly canReconcile: boolean;
  readonly controller: CodController;
}) {
  const status = (orderStatusLabels as Readonly<Record<string, string>>)[order.status] ?? order.status;
  return (
    <li>
      <span>{order.public_id} · {status}</span>{" "}
      <button className="opsSecondary" type="button" disabled={busy} onClick={() => void controller.load(order.id)}>
        Ver cobro
      </button>
      {canReconcile && (
        <button className="opsPrimary" type="button" disabled={busy}
          onClick={() => void controller.reconcileFromList(order.id)}>Conciliar</button>
      )}
    </li>
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
