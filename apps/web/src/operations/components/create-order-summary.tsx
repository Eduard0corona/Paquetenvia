"use client";

import Link from "next/link";
import { useEffect, useRef } from "react";
import { codExpectedCents, vatIncludedLabel, type PayerType } from "../contracts/create-order";
import { serviceTypeLabel } from "../contracts/operations-formatters";
import {
  breakdownLineLabel,
  packagesSummaryText,
  pricingTierLabels,
  serviceWindowSummary,
  summarizePackages,
} from "../contracts/order-wizard";
import { formatServiceWindow } from "../contracts/service-window";
import { operationsOrderHref } from "../routing/operations-routing";
import type { CreateOrderController, CreateOrderOutcome, CreateOrderState } from "../state/create-order-controller";
import { DateTime } from "../../components/ui/date-time";
import { DescriptionList } from "../../components/ui/description-list";
import { Money } from "../../components/ui/money";
import { StatusBadge } from "../../components/ui/status-badge";
import { focusLater, orderStepTitleId } from "./focus-later";

export const payerLabels: Readonly<Record<PayerType, string>> = {
  SENDER: "Remitente",
  RECIPIENT: "Destinatario",
  BUSINESS_ACCOUNT: "Cuenta empresarial",
};

const notCaptured = "Sin capturar";

/**
 * UI-PHASE3-ORDER-WIZARD-2026-10-10: the fixed summary next to the steps (stacked on phones). It
 * restates what was captured and, once calculated, the price breakdown the customer accepts
 * (AI-07: the breakdown is shown before confirmation). Nothing here is an identifier.
 */
export function OrderSummary({ state }: { readonly state: CreateOrderState }) {
  const draft = state.draft;
  const packages = summarizePackages(draft.packages);
  const described = draft.packages.some((item) => item.description.trim() !== "");
  const cod = codExpectedCents(draft.codAmount);
  const service =
    draft.serviceType === ""
      ? "Sin elegir"
      : `${serviceTypeLabel(draft.serviceType)}${draft.consolidatedRoute ? " · ruta consolidada" : ""}`;
  const quote = state.quote;
  return (
    <aside className="panel opsWizardSummary" aria-labelledby="order-summary-title">
      <h2 id="order-summary-title">Resumen</h2>
      <DescriptionList
        items={[
          { label: "Origen", value: draft.origin.addressText.trim() || notCaptured },
          { label: "Destino", value: draft.destination.addressText.trim() || notCaptured },
          { label: "Qué se envía", value: described ? packagesSummaryText(packages) : notCaptured },
          described &&
            packages.declaredCents !== null && {
              label: "Valor declarado",
              value: <Money cents={packages.declaredCents} />,
            },
          { label: "Servicio", value: service },
          { label: "Entrega", value: serviceWindowSummary(draft.serviceWindowFrom, draft.serviceWindowTo) },
          {
            label: "Cobro contra entrega",
            value: cod === null ? "Por corregir" : cod === 0 ? "Sin cobro" : <Money cents={cod} />,
          },
          draft.payerType !== "" && {
            label: "Quién paga",
            value: payerLabels[draft.payerType as PayerType] ?? notCaptured,
          },
        ]}
      />
      <section className="opsWizardPrice" aria-labelledby="order-summary-price">
        <h3 id="order-summary-price">Precio</h3>
        {quote === null ? (
          <p className="fieldHint">
            {state.busy === "quote" ? "Calculando el precio…" : "Se calcula en el paso 3, con el servicio elegido."}
          </p>
        ) : (
          <>
            <DescriptionList
              variant="money"
              label="Desglose del precio"
              items={[
                { label: "Neto sin IVA", value: <Money cents={quote.net.amount_cents} /> },
                { label: "IVA", value: <Money cents={quote.tax.amount_cents} /> },
                {
                  label: `Total (${vatIncludedLabel})`,
                  value: <Money cents={quote.total.amount_cents} strong />,
                },
              ]}
            />
            <p className="fieldHint">
              Regla aplicada: tarifa {pricingTierLabels[quote.pricing_tier]}; mínimo de referencia{" "}
              <Money cents={quote.minimum_total_cents_snapshot} /> ({vatIncludedLabel}).
            </p>
            {quote.breakdown.length > 0 && (
              <DescriptionList
                label="Conceptos de la tarifa"
                items={quote.breakdown.map((line, index) => ({
                  key: `line-${index}`,
                  label: breakdownLineLabel(line.line_type),
                  value: line.amount_cents === null ? "Sin monto" : <Money cents={line.amount_cents} />,
                }))}
              />
            )}
            <p className="fieldHint">
              Precio válido hasta <DateTime value={quote.expires_at} /> (hora de Mazatlán).
            </p>
            {quote.low_price_authorization !== null && (
              <p className="notice noticeWarn">
                Envío de bajo monto autorizado; vigente hasta{" "}
                <DateTime value={quote.low_price_authorization.valid_until} /> (hora de Mazatlán).
                {quote.low_price_authorization.reason !== null && <> Motivo: {quote.low_price_authorization.reason}</>}
              </p>
            )}
          </>
        )}
      </section>
    </aside>
  );
}

/**
 * What the wizard did: the order created and confirmed, or created and not confirmed. The
 * second case never reads as confirmed; it names what failed and links to the order detail,
 * where "Siguiente paso" can confirm it. Focus moves to the title when the panel appears.
 */
export function CreateOrderOutcomePanel({
  outcome,
  busy,
  controller,
}: {
  readonly outcome: CreateOrderOutcome;
  readonly busy: boolean;
  readonly controller: CreateOrderController;
}) {
  const headingRef = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    headingRef.current?.focus();
  }, []);
  const order = outcome.order;
  const detailHref = operationsOrderHref(order.id);
  // The panel goes away with the outcome; focus moves to the first step of the new capture.
  const captureAnother = () => {
    if (busy) return;
    controller.reset();
    focusLater(orderStepTitleId);
  };
  const details = (
    <DescriptionList
      items={[
        { label: "Número de guía", value: <span className="opsWizardGuide">{order.public_id}</span> },
        outcome.kind === "confirmed" && { label: "Estado", value: <StatusBadge status={outcome.status} /> },
        { label: "Servicio", value: serviceTypeLabel(order.service_type) },
        {
          label: "Entrega",
          value:
            order.service_window === null
              ? "Horario de la zona"
              : `${formatServiceWindow(order.service_window)} (hora de Mazatlán)`,
        },
        { label: "Neto sin IVA", value: <Money cents={order.price_net.amount_cents} /> },
        { label: `Total (${vatIncludedLabel})`, value: <Money cents={order.total.amount_cents} strong /> },
        {
          label: "Cobro contra entrega",
          value: outcome.codExpectedCents === 0 ? "Sin cobro" : <Money cents={outcome.codExpectedCents} />,
        },
      ]}
    />
  );

  if (outcome.kind === "confirmed") {
    return (
      <section className="panel opsWizardOutcome" aria-labelledby="order-outcome-title">
        <h2 id="order-outcome-title" ref={headingRef} tabIndex={-1}>
          Orden creada y confirmada
        </h2>
        <p className="notice noticeOk" role="status">
          La orden quedó confirmada y lista para preparar.
        </p>
        {details}
        <div className="opsFormActions">
          <Link className="btn btnPrimary" href={detailHref}>
            Abrir orden
          </Link>
          <button type="button" className="btn btnSecondary" onClick={captureAnother}>
            Capturar otra orden
          </button>
        </div>
      </section>
    );
  }

  const draft = outcome.certainty === "draft";
  return (
    <section className="panel opsWizardOutcome" aria-labelledby="order-outcome-title">
      <h2 id="order-outcome-title" ref={headingRef} tabIndex={-1}>
        {draft ? "Orden creada en borrador" : "Orden creada sin confirmación"}
      </h2>
      <p className={outcome.stepUpHref === null ? "notice noticeWarn" : "notice noticeCrit"} role="alert">
        No se pudo confirmar. {outcome.message}{" "}
        {outcome.stepUpHref !== null && (
          <Link className="btn btnPrimary" href={outcome.stepUpHref}>
            Verificar identidad
          </Link>
        )}
      </p>
      <p>
        {draft
          ? `La orden ${order.public_id} se creó, pero no quedó confirmada. Ábrela y confírmala desde “Siguiente paso”.`
          : `La orden ${order.public_id} se creó, pero no sabemos si quedó confirmada. ${
              outcome.retryable ? "Reintenta la confirmación (no se duplica) o ábrela" : "Ábrela"
            } para revisar su estado; si sigue en borrador, confírmala desde “Siguiente paso”.`}
      </p>
      {details}
      <div className="opsFormActions">
        {outcome.retryable && (
          <button
            type="button"
            className="btn btnPrimary"
            aria-disabled={busy}
            onClick={() => void controller.retryConfirmation()}
          >
            {busy ? "Confirmando la orden…" : "Reintentar confirmación"}
          </button>
        )}
        <Link className={outcome.retryable ? "btn btnSecondary" : "btn btnPrimary"} href={detailHref}>
          Abrir orden
        </Link>
        <button type="button" className="btn btnSecondary" aria-disabled={busy} onClick={captureAnother}>
          Capturar otra orden
        </button>
      </div>
    </section>
  );
}
