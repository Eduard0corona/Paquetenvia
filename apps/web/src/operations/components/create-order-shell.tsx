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
import { formatMxnCentsWithCurrency } from "../contracts/money";
import { formatMazatlanTime, serviceTypeLabel } from "../contracts/operations-formatters";
import { operationsOrderHref } from "../routing/operations-routing";
import type { CreateOrderController, CreateOrderState } from "../state/create-order-controller";
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
    <main className="opsShell" aria-busy={state.phase === "loading" || state.busy}>
      <header className="opsHeader">
        <div>
          <p className="opsEyebrow">Despacho</p>
          <h1>Nueva orden</h1>
          <p>Cotización y aceptación con la API como autoridad.</p>
        </div>
        <div className="opsHeaderStatus">
          <Link className="opsPrimary" href="/ops/dashboard">Volver a Operaciones</Link>
        </div>
      </header>

      {state.phase === "no_session" && (
        <section className="opsMessage" role="alert">
          <h2>Sin sesión de Operaciones</h2>
          <p>Inicia sesión y selecciona una organización.</p>
        </section>
      )}
      {state.phase === "access_unavailable" && (
        <section className="opsMessage" role="alert">
          <h2>Acceso no disponible</h2>
          <p>Tu rol en la organización activa no puede crear cotizaciones ni órdenes.</p>
        </section>
      )}
      {state.phase === "loading" && <p className="opsLive" aria-live="polite">Cargando permisos.</p>}

      <Feedback state={state} />

      {state.phase === "ready" && state.order !== null && (
        <section className="opsMessage" aria-labelledby="order-created">
          <h2 id="order-created">Orden {state.order.public_id}</h2>
          <dl className="opsMoneyList">
            <MoneyRow label="Neto sin IVA" cents={state.order.price_net.amount_cents} />
            <MoneyRow label={`Total (${vatIncludedLabel})`} cents={state.order.total.amount_cents} />
            {state.orderCodExpectedCents !== null && (
              <div>
                <dt>Cobro contra entrega declarado</dt>
                <dd>{state.orderCodExpectedCents === 0 ? "Sin cobro" : formatMxnCentsWithCurrency(state.orderCodExpectedCents)}</dd>
              </div>
            )}
          </dl>
          <p>Servicio: {serviceTypeLabel(state.order.service_type)} · versión {state.order.version}</p>
          <div className="opsFormActions">
            <Link className="opsPrimary" href={operationsOrderHref(state.order.id)}>Abrir orden</Link>
            <button type="button" className="opsSecondary" onClick={() => controller.reset()}>Capturar otra orden</button>
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
    </main>
  );
}

function Feedback({ state }: { readonly state: CreateOrderState }) {
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
          <fieldset key={index} className="opsPackage">
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
          <button type="button" className="opsSecondary" disabled={packages >= maximumPackages}
            onClick={() => setPackages((count) => Math.min(maximumPackages, count + 1))}>Agregar paquete</button>
          <button type="button" className="opsSecondary" disabled={packages <= 1}
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
        <label>Cuenta cliente (UUID, opcional)<input name="client_account_id" /></label>
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
      <button className="opsPrimary" type="submit" disabled={disabled}>
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
          pattern="[ \-]*(?:[0-9][ \-]*){10}"
          title="10 dígitos de México, sin +52; puedes separarlos con espacios o guiones."
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
    <section className="opsMessage" aria-labelledby="quote-title">
      <h2 id="quote-title">Cotización</h2>
      <dl className="opsMoneyList">
        <MoneyRow label="Neto sin IVA" cents={quote.net.amount_cents} />
        <MoneyRow label="IVA" cents={quote.tax.amount_cents} />
        {quote.breakdown.map((line, index) => (
          <div key={index}>
            <dt>{line.line_type === null ? "Concepto" : breakdownLabels[line.line_type] ?? line.line_type}</dt>
            <dd>{line.amount_cents === null ? "Sin monto" : formatMxnCentsWithCurrency(line.amount_cents)}</dd>
          </div>
        ))}
        <MoneyRow label={`Total (${vatIncludedLabel})`} cents={quote.total.amount_cents} strong />
      </dl>
      <p>
        Regla aplicada: tarifa {quote.pricing_tier}, política {quote.pricing_policy_version},
        {" "}{quote.rule_ids.length} regla(s). Mínimo de referencia {formatMxnCentsWithCurrency(quote.minimum_total_cents_snapshot)} ({vatIncludedLabel}).
      </p>
      <p>
        {serviceTypeLabel(quote.service_type)} · {quote.package_count} paquete(s) ·
        {quote.consolidated_route ? " ruta consolidada" : " sin ruta consolidada"} · vence {formatMazatlanTime(quote.expires_at)} (hora de Mazatlán)
      </p>
      {quote.low_price_authorization !== null && (
        <p className="opsWarning" role="status">
          Envío de bajo monto autorizado; vigente hasta {formatMazatlanTime(quote.low_price_authorization.valid_until)} (hora de Mazatlán).
          {quote.low_price_authorization.reason !== null && <> Motivo: {quote.low_price_authorization.reason}</>}
        </p>
      )}
      {blockers.length > 0 && (
        <ul className="opsWarning" role="status">
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
            });
          }}
        >
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
              <p className="opsWarning" role="alert">{acceptanceVersionsUnavailableMessage}</p>
            ) : (
              <dl className="opsMoneyList" aria-label="Documentos que acepta el cliente">
                <div><dt>Versión de términos vigente</dt><dd>{acceptanceVersions.termsVersion}</dd></div>
                <div><dt>Versión del aviso de privacidad vigente</dt><dd>{acceptanceVersions.privacyVersion}</dd></div>
                <div><dt>Canal de aceptación</dt><dd>Asistido por operador</dd></div>
              </dl>
            )}
            <label className="opsCheckbox">
              <input type="checkbox" name="accepted" required /> El cliente vio el desglose y aceptó términos y aviso de privacidad
            </label>
            <label className="opsCheckbox">
              <input type="checkbox" name="restricted_goods_acknowledged" required /> Confirmo que el envío no contiene
              artículos prohibidos (queda registrado en la orden)
            </label>
          </fieldset>
          <button className="opsPrimary" type="submit" disabled={busy || blockers.length > 0 || acceptanceVersions === null}>
            {busy ? "Confirmando..." : "Confirmar orden"}
          </button>
        </form>
      ) : (
        <p className="opsWarning">Tu rol no puede confirmar órdenes.</p>
      )}
    </section>
  );
}

function MoneyRow({ label, cents, strong = false }: { readonly label: string; readonly cents: number; readonly strong?: boolean }) {
  return (
    <div>
      <dt>{label}</dt>
      <dd>{strong ? <strong>{formatMxnCentsWithCurrency(cents)}</strong> : formatMxnCentsWithCurrency(cents)}</dd>
    </div>
  );
}
