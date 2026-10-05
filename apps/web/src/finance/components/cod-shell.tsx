"use client";

import Link from "next/link";
import { useConfirmDialog, type ConfirmRequest } from "../../components/confirm-dialog";
import { DateTime } from "../../components/ui/date-time";
import { DescriptionList } from "../../components/ui/description-list";
import { EmptyState } from "../../components/ui/empty-state";
import { Feedback, ScreenGate } from "../../components/ui/feedback";
import { Money } from "../../components/ui/money";
import { PageHeader } from "../../components/ui/page-header";
import { orderStatusLabels } from "../../operations/contracts/operations-formatters";
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
import { codReconciliationConfirmation, pendingCodReconciliationConfirmation } from "../contracts/confirmations";
import type { CodController, CodState } from "../state/cod-controller";
import { useCod } from "../state/use-cod";

export function CodShell() {
  const { state, controller } = useCod();
  const { confirm, dialog } = useConfirmDialog();
  return (
    <div className="page" aria-busy={state.phase === "loading" || state.loadingOrder !== null || state.busy}>
      <PageHeader
        eyebrow="Finanzas"
        title="Cobro contra entrega"
        description="Registra y concilia el efectivo cobrado al entregar cada orden."
        actions={
          <button className="btn btnSecondary" type="button"
            disabled={state.phase !== "ready" || state.financials === null || state.loadingOrder !== null}
            onClick={() => void controller.refresh()}>Actualizar</button>
        }
      />

      <ScreenGate phase={state.phase} accessMessage="Tu rol en la organización activa no consulta cobros contra entrega." />
      <Feedback errors={state.errors} message={state.message} stepUpHref={state.stepUpHref} />
      {state.mfaHint !== null && <p className="notice noticeInfo">{state.mfaHint}</p>}

      {state.phase === "ready" && (
        <section className="opsFormLayout">
          <div className="page">
            {state.canListPending && <PendingList state={state} controller={controller} confirm={confirm} />}
            <LookupForm key={`lookup-${state.formKey}`} controller={controller} disabled={state.loadingOrder !== null} />
          </div>
          <section className="page">
            {state.financials === null && state.transaction === null && state.loadingOrder === null && (
              <EmptyState>Consulta una orden para ver su cobro contra entrega.</EmptyState>
            )}
            {state.loadingOrder !== null && <p className="live" aria-live="polite">Cargando la orden.</p>}
            {state.financials !== null && state.loadingOrder === null && (
              <Financials key={`${state.financials.order_id}-${state.formKey}`} financials={state.financials}
                state={state} controller={controller} />
            )}
            {state.transaction !== null && (
              <Transaction transaction={state.transaction} state={state} controller={controller} confirm={confirm} />
            )}
          </section>
        </section>
      )}
      {dialog}
    </div>
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
        <label>ID de la orden<input name="order_id" required aria-describedby="cod-lookup-help" /></label>
        <p id="cod-lookup-help" className="fieldHint">Pega el ID completo de la orden; lo encuentras al abrirla desde Operaciones.</p>
        <button className="btn btnSecondary" type="submit" disabled={disabled}>Consultar</button>
      </fieldset>
    </form>
  );
}

/** API-FIN-COD-VISIBILITY-2026-09-29: collections recorded and not yet reconciled, from listOrders. */
function PendingList({
  state,
  controller,
  confirm,
}: {
  readonly state: CodState;
  readonly controller: CodController;
  readonly confirm: (request: ConfirmRequest) => void;
}) {
  const busy = state.busy || state.loadingOrder !== null;
  return (
    <section className="panel" aria-busy={state.pendingLoading}>
      <h2>Cobros pendientes de conciliar</h2>
      <button className="btn btnSecondary" type="button" disabled={state.pendingLoading}
        onClick={() => void controller.loadPending()}>Actualizar lista</button>
      {state.pending === null ? (
        state.pendingLoading ? <p className="live" aria-live="polite">Cargando cobros pendientes.</p> : null
      ) : state.pending.length === 0 ? (
        <EmptyState>No hay cobros registrados pendientes de conciliar.</EmptyState>
      ) : (
        <ul className="opsPlainList">
          {state.pending.map((order) => (
            <PendingRow key={order.id} order={order} busy={busy} canReconcile={state.canReconcile} controller={controller}
              confirm={confirm} />
          ))}
        </ul>
      )}
      {state.pendingCursor !== null && (
        <button className="btn btnSecondary" type="button" disabled={state.pendingLoading}
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
  confirm,
}: {
  readonly order: PendingCodOrder;
  readonly busy: boolean;
  readonly canReconcile: boolean;
  readonly controller: CodController;
  readonly confirm: (request: ConfirmRequest) => void;
}) {
  const status = (orderStatusLabels as Readonly<Record<string, string>>)[order.status] ?? order.status;
  return (
    <li>
      <span>{order.public_id} · {status}</span>{" "}
      <button className="btn btnSecondary" type="button" disabled={busy} onClick={() => void controller.load(order.id)}>
        Ver cobro
      </button>
      {canReconcile && (
        <button className="btn btnPrimary" type="button" disabled={busy}
          onClick={() => confirm({
            ...pendingCodReconciliationConfirmation(order),
            onConfirm: () => void controller.reconcileFromList(order.id),
          })}>Conciliar</button>
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
      <DescriptionList
        variant="money"
        items={[
          { label: "Ingreso", value: <Money cents={financials.revenue_cents} /> },
          ...financials.cost_by_modality.map((bucket) => ({
            key: `cost-${bucket.modality}`,
            label: `Costo ${modalityLabels[bucket.modality]} (${bucket.assignment_count})`,
            value: <Money cents={bucket.cost_cents} />,
          })),
          { label: "Costo total", value: <Money cents={financials.cost_cents} /> },
          {
            label: "Margen",
            value: (
              <>
                <Money cents={financials.margin_cents} />
                {financials.margin_basis_points !== null && ` (${formatBasisPoints(financials.margin_basis_points)})`}
              </>
            ),
          },
        ]}
      />

      <h3>Cobro contra entrega</h3>
      {cod.expected_cents === 0 && cod.status === null ? <p>La orden no tiene cobro contra entrega.</p> : (
        <DescriptionList
          variant="money"
          items={[
            { label: "Esperado", value: <Money cents={cod.expected_cents} /> },
            { label: "Estado", value: cod.status === null ? "Sin cobro registrado" : codStatusLabels[cod.status] },
            cod.amount_cents !== null && { label: "Cobrado", value: <Money cents={cod.amount_cents} /> },
            { label: "Permite entregar", value: cod.satisfies_delivery_requirement ? "Sí" : "No" },
            { label: "Permite cerrar", value: cod.satisfies_close_requirement ? "Sí" : "No" },
          ]}
        />
      )}

      {state.canRecord && canRecordCollection(financials) && (
        <form className="opsForm" autoComplete="off" onSubmit={(event) => {
          event.preventDefault();
          const data = new FormData(event.currentTarget);
          void controller.record(String(data.get("amount") ?? ""), String(data.get("reference") ?? ""));
        }}>
          <fieldset>
            <legend>Registrar cobro</legend>
            <p>El monto debe ser exactamente <Money cents={cod.expected_cents} />.</p>
            <label>Monto cobrado (MXN)<input name="amount" inputMode="decimal" required /></label>
            <label>Referencia (máximo 200 caracteres, sin espacios al inicio o al final)
              <input name="reference" maxLength={200} required />
            </label>
            <button className="btn btnPrimary" type="submit" disabled={state.busy}>Registrar cobro</button>
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
  confirm,
}: {
  readonly transaction: CodTransaction;
  readonly state: CodState;
  readonly controller: CodController;
  readonly confirm: (request: ConfirmRequest) => void;
}) {
  return (
    <section>
      <h3>Registro de cobro {transaction.id}</h3>
      <DescriptionList
        variant="money"
        items={[
          { label: "Monto", value: <Money cents={transaction.amount_cents} /> },
          { label: "Estado", value: codStatusLabels[transaction.status] },
          { label: "Registrado", value: transaction.recorded_at === null ? "—" : <DateTime value={transaction.recorded_at} /> },
          { label: "Conciliado", value: transaction.reconciled_at === null ? "—" : <DateTime value={transaction.reconciled_at} /> },
        ]}
      />
      {transaction.status === "RECORDED" && state.canReconcile && (
        <button className="btn btnPrimary" type="button" disabled={state.busy} onClick={() => confirm({
          ...codReconciliationConfirmation(transaction),
          onConfirm: () => void controller.reconcile(),
        })}>
          Conciliar este cobro
        </button>
      )}
    </section>
  );
}
