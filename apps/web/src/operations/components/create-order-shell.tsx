"use client";

import Link from "next/link";
import { useState } from "react";
import {
  acceptanceVersionsUnavailableMessage,
  type AcceptanceVersions,
} from "../contracts/acceptance-versions";
import {
  evaluateConfirmation,
  confirmationBlockerLabels,
  lowPriceAuthorizationReasonMaximum,
  maximumPackages,
  maximumPhoneInputLength,
  payerTypes,
  type AddressDraft,
  type PackageDraft,
  type Quote,
  vatIncludedLabel,
} from "../contracts/create-order";
import { lowPriceAuthorizationRequiresMfa, mayHandleExactCoordinates } from "../contracts/capabilities";
import { maximumServiceWindowHours, serviceWindowTimeZone } from "../contracts/service-window";
import { serviceTypeLabel } from "../contracts/operations-formatters";
import { operationsOrderHref } from "../routing/operations-routing";
import type { CreateOrderController } from "../state/create-order-controller";
import { DateTime } from "../../components/ui/date-time";
import { DescriptionList } from "../../components/ui/description-list";
import { Feedback, ScreenGate } from "../../components/ui/feedback";
import { Money } from "../../components/ui/money";
import { PageHeader } from "../../components/ui/page-header";
import { useCreateOrder } from "../state/use-create-order";

const payerLabels: Readonly<Record<string, string>> = {
  SENDER: "Remitente",
  RECIPIENT: "Destinatario",
  BUSINESS_ACCOUNT: "Cuenta empresarial",
};
const breakdownLabels: Readonly<Record<string, string>> = {
  BASE_TARIFF: `Tarifa base (${vatIncludedLabel})`,
};

export function CreateOrderShell({
  acceptanceVersions,
}: {
  readonly acceptanceVersions: AcceptanceVersions | null;
}) {
  const { state, controller } = useCreateOrder(acceptanceVersions);
  return (
    <div className="page" aria-busy={state.phase === "loading" || state.busy}>
      <PageHeader
        eyebrow="Despacho"
        title="Nueva orden"
        description="Cotiza el envío y confirma la orden con la aceptación del cliente."
      />

      <ScreenGate
        phase={state.phase}
        accessMessage="Tu rol en la organización activa no puede crear cotizaciones ni órdenes."
      />
      <Feedback errors={state.errors} message={state.message} stepUpHref={state.stepUpHref} />

      {state.phase === "ready" && state.order !== null && (
        <section className="panel" aria-labelledby="order-created">
          <h2 id="order-created">Orden {state.order.public_id}</h2>
          <DescriptionList
            variant="money"
            items={[
              { label: "Neto sin IVA", value: <Money cents={state.order.price_net.amount_cents} /> },
              { label: `Total (${vatIncludedLabel})`, value: <Money cents={state.order.total.amount_cents} /> },
              state.orderCodExpectedCents !== null && {
                label: "Cobro contra entrega declarado",
                value: state.orderCodExpectedCents === 0 ? "Sin cobro" : <Money cents={state.orderCodExpectedCents} />,
              },
            ]}
          />
          <p>Servicio: {serviceTypeLabel(state.order.service_type)} · versión {state.order.version}</p>
          <p>
            Ventana de entrega:{" "}
            {state.order.service_window === null
              ? "horario de la zona"
              : <><DateTime value={state.order.service_window.from} /> a <DateTime value={state.order.service_window.to} /> (hora de Mazatlán)</>}
          </p>
          <div className="opsFormActions">
            <Link className="btn btnPrimary" href={operationsOrderHref(state.order.id)}>Abrir orden</Link>
            <button type="button" className="btn btnSecondary" onClick={() => controller.reset()}>Capturar otra orden</button>
          </div>
        </section>
      )}

      {state.phase === "ready" && state.order === null && (
        <div className="opsFormLayout">
          <QuoteForm
            key={state.formKey}
            controller={controller}
            disabled={state.busy}
            coordinates={mayHandleExactCoordinates(state.role)}
            lowPriceAuthorization={state.canAuthorizeLowPrice}
            lowPriceAuthorizationNeedsMfa={lowPriceAuthorizationRequiresMfa(state.role)}
          />
          {state.quote !== null && (
            <QuoteSummary
              key={state.quote.id}
              quote={state.quote}
              controller={controller}
              canOrder={state.canOrder}
              busy={state.busy}
              acceptanceVersions={acceptanceVersions}
            />
          )}
        </div>
      )}
    </div>
  );
}

function QuoteForm({
  controller,
  disabled,
  coordinates,
  lowPriceAuthorization,
  lowPriceAuthorizationNeedsMfa,
}: {
  readonly controller: CreateOrderController;
  readonly disabled: boolean;
  /** Exact coordinates are only ever handled by DISPATCHER and PLATFORM_ADMIN (D5). */
  readonly coordinates: boolean;
  /** LOW-PRICE-MANUAL-AUTH-2026-10-02: only DISPATCHER and PLATFORM_ADMIN see the option. */
  readonly lowPriceAuthorization: boolean;
  readonly lowPriceAuthorizationNeedsMfa: boolean;
}) {
  const [packages, setPackages] = useState(1);
  const [authorizeLowPrice, setAuthorizeLowPrice] = useState(false);
  return (
    <form
      className="opsForm"
      autoComplete="off"
      onSubmit={(event) => {
        event.preventDefault();
        const data = new FormData(event.currentTarget);
        const text = (name: string) => String(data.get(name) ?? "");
        const addressFrom = (prefix: string): AddressDraft => ({
          addressText: text(`${prefix}_address`),
          contactName: text(`${prefix}_contact`),
          phone: text(`${prefix}_phone`),
          lat: text(`${prefix}_lat`),
          lng: text(`${prefix}_lng`),
          references: text(`${prefix}_references`),
        });
        const packageDrafts: PackageDraft[] = Array.from({ length: packages }, (_, index) => ({
          description: text(`package_${index}_description`),
          weightGrams: text(`package_${index}_weight`),
          declaredValue: text(`package_${index}_declared`),
          lengthMm: text(`package_${index}_length`),
          widthMm: text(`package_${index}_width`),
          heightMm: text(`package_${index}_height`),
        }));
        void controller.requestQuote({
          clientAccountId: text("client_account_id"),
          origin: addressFrom("origin"),
          destination: addressFrom("destination"),
          serviceType: text("service_type"),
          consolidatedRoute: data.get("consolidated_route") === "on",
          packages: packageDrafts,
          authorizeLowPrice: lowPriceAuthorization && data.get("authorize_low_price") === "on",
          lowPriceReason: text("low_price_reason"),
        });
      }}
    >
      <AddressFieldset prefix="origin" legend="1. Origen" coordinates={coordinates} />
      <AddressFieldset prefix="destination" legend="2. Destino" coordinates={coordinates} />
      <fieldset>
        <legend>3. Paquetes</legend>
        {Array.from({ length: packages }, (_, index) => (
          <fieldset key={index}>
            <legend>Paquete {index + 1}</legend>
            <label>Descripción<input name={`package_${index}_description`} maxLength={250} required /></label>
            <label>Peso (gramos)<input name={`package_${index}_weight`} inputMode="numeric" pattern="[0-9]+" required /></label>
            <label>Valor declarado (MXN)<input name={`package_${index}_declared`} inputMode="decimal" pattern="[0-9]+(\.[0-9]{1,2})?" required /></label>
            <label>Largo (mm, opcional)<input name={`package_${index}_length`} inputMode="numeric" pattern="[0-9]+" /></label>
            <label>Ancho (mm, opcional)<input name={`package_${index}_width`} inputMode="numeric" pattern="[0-9]+" /></label>
            <label>Alto (mm, opcional)<input name={`package_${index}_height`} inputMode="numeric" pattern="[0-9]+" /></label>
          </fieldset>
        ))}
        <div className="opsFormActions">
          <button type="button" className="btn btnSecondary" disabled={packages >= maximumPackages}
            onClick={() => setPackages((count) => Math.min(maximumPackages, count + 1))}>Agregar paquete</button>
          <button type="button" className="btn btnSecondary" disabled={packages <= 1}
            onClick={() => setPackages((count) => Math.max(1, count - 1))}>Quitar último paquete</button>
        </div>
      </fieldset>
      <fieldset>
        <legend>4. Servicio</legend>
        <label>Tipo de servicio
          <select name="service_type" defaultValue="" required>
            <option value="" disabled>Selecciona</option>
            <option value="SAME_DAY">{serviceTypeLabel("SAME_DAY")}</option>
            <option value="URGENT">{serviceTypeLabel("URGENT")}</option>
            <option value="SCHEDULED_ROUTE">{serviceTypeLabel("SCHEDULED_ROUTE")}</option>
          </select>
        </label>
        <label className="opsCheckbox"><input type="checkbox" name="consolidated_route" /> Ruta consolidada</label>
        <label>ID de la cuenta cliente (opcional)<input name="client_account_id" aria-describedby="create-order-client-help" /></label>
        <p id="create-order-client-help" className="fieldHint">Déjalo vacío si la orden no pertenece a una cuenta cliente.</p>
      </fieldset>
      {lowPriceAuthorization && (
        <fieldset>
          <legend>Autorizar envío de bajo monto</legend>
          <label className="opsCheckbox">
            <input
              type="checkbox"
              name="authorize_low_price"
              checked={authorizeLowPrice}
              onChange={(event) => setAuthorizeLowPrice(event.currentTarget.checked)}
            />{" "}
            Autorizar envío de bajo monto
          </label>
          {authorizeLowPrice && (
            <label>Motivo de la autorización
              <input
                name="low_price_reason"
                maxLength={lowPriceAuthorizationReasonMaximum}
                required
                aria-describedby="low-price-reason-help"
              />
            </label>
          )}
          <p id="low-price-reason-help">
            Solo para envíos de 52 MXN o menos (IVA incluido) sin ruta consolidada. El motivo queda auditado
            con tu usuario; no escribas nombres, teléfonos, correos ni direcciones.
            {lowPriceAuthorizationNeedsMfa && " Requiere verificar tu identidad (MFA)."}
          </p>
        </fieldset>
      )}
      <button className="btn btnPrimary" type="submit" disabled={disabled}>
        {disabled ? "Cotizando..." : "5. Cotizar"}
      </button>
    </form>
  );
}

function AddressFieldset({
  prefix,
  legend,
  coordinates,
}: {
  readonly prefix: string;
  readonly legend: string;
  readonly coordinates: boolean;
}) {
  return (
    <fieldset>
      <legend>{legend}</legend>
      <label>Dirección<input name={`${prefix}_address`} minLength={8} required /></label>
      <label>Contacto<input name={`${prefix}_contact`} required /></label>
      <label>Teléfono (10 dígitos)
        <input
          name={`${prefix}_phone`}
          type="tel"
          inputMode="tel"
          maxLength={maximumPhoneInputLength}
          pattern="[ \-]*(?:\+52[ \-]*)?(?:[0-9][ \-]*){10}"
          title="10 dígitos de México; puedes anteponer +52 y separarlos con espacios o guiones."
          placeholder="667 123 4567"
          required
        />
      </label>
      {coordinates && (
        <>
          <label>Latitud<input name={`${prefix}_lat`} inputMode="decimal" required /></label>
          <label>Longitud<input name={`${prefix}_lng`} inputMode="decimal" required /></label>
        </>
      )}
      <label>Referencias (opcional)<input name={`${prefix}_references`} maxLength={500} /></label>
    </fieldset>
  );
}

function QuoteSummary({
  quote,
  controller,
  canOrder,
  busy,
  acceptanceVersions,
}: {
  readonly quote: Quote;
  readonly controller: CreateOrderController;
  readonly canOrder: boolean;
  readonly busy: boolean;
  readonly acceptanceVersions: AcceptanceVersions | null;
}) {
  const [now] = useState(() => new Date());
  const blockers = evaluateConfirmation(quote, now);
  return (
    <section className="panel" aria-labelledby="quote-title">
      <h2 id="quote-title">Cotización</h2>
      <DescriptionList
        variant="money"
        items={[
          { label: "Neto sin IVA", value: <Money cents={quote.net.amount_cents} /> },
          { label: "IVA", value: <Money cents={quote.tax.amount_cents} /> },
          ...quote.breakdown.map((line, index) => ({
            key: `line-${index}`,
            label: line.line_type === null ? "Concepto" : breakdownLabels[line.line_type] ?? line.line_type,
            value: line.amount_cents === null ? "Sin monto" : <Money cents={line.amount_cents} />,
          })),
          { label: `Total (${vatIncludedLabel})`, value: <Money cents={quote.total.amount_cents} strong /> },
        ]}
      />
      <p>
        Regla aplicada: tarifa {quote.pricing_tier}, política {quote.pricing_policy_version},
        {" "}{quote.rule_ids.length} regla(s). Mínimo de referencia <Money cents={quote.minimum_total_cents_snapshot} /> ({vatIncludedLabel}).
      </p>
      <p>
        {serviceTypeLabel(quote.service_type)} · {quote.package_count} paquete(s) ·
        {quote.consolidated_route ? " ruta consolidada" : " sin ruta consolidada"} · vence <DateTime value={quote.expires_at} /> (hora de Mazatlán)
      </p>
      {quote.low_price_authorization !== null && (
        <p className="notice noticeWarn" role="status">
          Envío de bajo monto autorizado; vigente hasta <DateTime value={quote.low_price_authorization.valid_until} /> (hora de Mazatlán).
          {quote.low_price_authorization.reason !== null && <> Motivo: {quote.low_price_authorization.reason}</>}
        </p>
      )}
      {blockers.length > 0 && (
        <ul className="notice noticeWarn" role="status">
          {blockers.map((blocker) => <li key={blocker}>{confirmationBlockerLabels[blocker]}</li>)}
        </ul>
      )}
      {canOrder ? (
        <form
          className="opsForm"
          autoComplete="off"
          onSubmit={(event) => {
            event.preventDefault();
            const data = new FormData(event.currentTarget);
            void controller.confirmOrder({
              payerType: String(data.get("payer_type") ?? ""),
              accepted: data.get("accepted") === "on",
              restrictedGoodsAcknowledged: data.get("restricted_goods_acknowledged") === "on",
              codAmount: String(data.get("cod_amount") ?? ""),
              serviceWindowFrom: String(data.get("service_window_from") ?? ""),
              serviceWindowTo: String(data.get("service_window_to") ?? ""),
            });
          }}
        >
          <fieldset aria-describedby="service-window-help">
            <legend>Ventana de entrega (opcional)</legend>
            <label>Desde (hora de Mazatlán)
              <input name="service_window_from" type="datetime-local" step={60} />
            </label>
            <label>Hasta (hora de Mazatlán)
              <input name="service_window_to" type="datetime-local" step={60} />
            </label>
            <p id="service-window-help">
              Déjala vacía para usar el horario de la zona. Máximo {maximumServiceWindowHours} horas; las horas se
              interpretan en {serviceWindowTimeZone}, no en la zona de este equipo.
            </p>
          </fieldset>
          <fieldset>
            <legend>6. Aceptación</legend>
            <label>Quién paga
              <select name="payer_type" defaultValue="" required>
                <option value="" disabled>Selecciona</option>
                {payerTypes.map((value) => <option key={value} value={value}>{payerLabels[value]}</option>)}
              </select>
            </label>
            <label>Cobro contra entrega (MXN, opcional)
              <input
                name="cod_amount"
                inputMode="decimal"
                pattern="[0-9]+(\.[0-9]{1,2})?"
                placeholder="Vacío si no hay cobro"
                aria-describedby="cod-amount-help"
              />
            </label>
            <p id="cod-amount-help">Monto que el repartidor cobrará al entregar, en pesos con hasta 2 decimales (por ejemplo 150.50), máximo $20,000.00 por pedido.</p>
            {acceptanceVersions === null ? (
              <p className="notice noticeWarn" role="alert">{acceptanceVersionsUnavailableMessage}</p>
            ) : (
              <DescriptionList
                variant="money"
                label="Documentos que acepta el cliente"
                items={[
                  { label: "Versión de términos vigente", value: acceptanceVersions.termsVersion },
                  { label: "Versión del aviso de privacidad vigente", value: acceptanceVersions.privacyVersion },
                  { label: "Canal de aceptación", value: "Asistido por operador" },
                ]}
              />
            )}
            <label className="opsCheckbox">
              <input type="checkbox" name="accepted" required /> El cliente vio el desglose y aceptó términos y aviso de privacidad
            </label>
            <label className="opsCheckbox">
              <input type="checkbox" name="restricted_goods_acknowledged" required /> Confirmo que el envío no contiene
              artículos prohibidos (queda registrado en la orden)
            </label>
          </fieldset>
          <button className="btn btnPrimary" type="submit" disabled={busy || blockers.length > 0 || acceptanceVersions === null}>
            {busy ? "Confirmando..." : "Confirmar orden"}
          </button>
        </form>
      ) : (
        <p className="notice noticeWarn">Tu rol no puede confirmar órdenes.</p>
      )}
    </section>
  );
}
