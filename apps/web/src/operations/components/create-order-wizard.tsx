"use client";

import type { ReactNode } from "react";
import { acceptanceVersionsUnavailableMessage } from "../contracts/acceptance-versions";
import { mayHandleExactCoordinates } from "../contracts/capabilities";
import {
  isHeldByLowPriceGuard,
  lowPriceAuthorizationReasonMaximum,
  maximumPackages,
  maximumPhoneInputLength,
  payerTypes,
  serviceTypes,
  vatIncludedLabel,
  type AddressDraft,
  type FieldError,
} from "../contracts/create-order";
import { maximumCodExpectedCents } from "../contracts/money";
import { serviceTypeLabel } from "../contracts/operations-formatters";
import {
  fieldControlId,
  orderWizardSteps,
  wizardStepIndex,
  type OrderWizardStep,
} from "../contracts/order-wizard";
import { maximumServiceWindowHours } from "../contracts/service-window";
import type { CreateOrderController, CreateOrderState } from "../state/create-order-controller";
import { Feedback } from "../../components/ui/feedback";
import { Field } from "../../components/ui/field";
import { Money } from "../../components/ui/money";
import { OrderSummary, payerLabels } from "./create-order-summary";
import { focusLater, orderStepTitleId as stepHeadingId } from "./focus-later";

const quoteButtonId = fieldControlId("quote");

function errorFor(errors: readonly FieldError[], field: string): string | undefined {
  return errors.find((error) => error.field === field)?.message;
}

/**
 * UI-PHASE3-ORDER-WIZARD-2026-10-10: the four steps of "Nueva orden" with the stepper and the
 * fixed summary. All state lives in the controller; this component only renders it and forwards
 * what the person types. After a step change focus goes to the step title, and after a refused
 * step to the first field with a message.
 */
export function CreateOrderWizard({
  state,
  controller,
  versionsConfigured,
}: {
  readonly state: CreateOrderState;
  readonly controller: CreateOrderController;
  readonly versionsConfigured: boolean;
}) {
  const index = wizardStepIndex(state.step);
  const step = orderWizardSteps[index];
  const busy = state.busy !== null;

  const run = async (action: () => void | Promise<void>) => {
    const before = controller.getSnapshot();
    await action();
    const after = controller.getSnapshot();
    if (after.outcome !== null) return;
    if (after.validationAttempt !== before.validationAttempt) {
      const first = after.fieldErrors[0];
      focusLater(...(first === undefined ? [] : [fieldControlId(first.field)]), stepHeadingId);
    } else if (after.step !== before.step) {
      focusLater(stepHeadingId);
    }
  };

  const primary =
    state.step === "confirm"
      ? { label: state.busy === "order" ? "Creando la orden…" : state.busy === "confirm" ? "Confirmando la orden…" : "Crear y confirmar orden", action: () => controller.submit() }
      : state.step === "service" && state.quote === null
        ? { label: state.busy === "quote" ? "Calculando el precio…" : "Calcular precio", action: () => controller.requestQuote() }
        : { label: "Siguiente", action: () => controller.next() };

  // On phones the summary is stacked: after the fields while capturing, before them on the last
  // step so the price breakdown is read before the order is confirmed (AI-07).
  const summary = <OrderSummary state={state} />;
  const summaryFirst = state.step === "confirm";

  return (
    <div className="opsWizard">
      <WizardStepper current={state.step} busy={busy} onGoTo={(target) => void run(() => controller.goTo(target))} />
      <div className="opsWizardLayout">
        {summaryFirst && summary}
        <form
          className="opsForm opsWizardForm"
          noValidate
          autoComplete="off"
          aria-labelledby={stepHeadingId}
          onSubmit={(event) => {
            event.preventDefault();
            void run(primary.action);
          }}
        >
          <div className="opsWizardStepHeader">
            <h2 id={stepHeadingId} tabIndex={-1}>
              <span className="opsWizardStepCount">
                Paso {index + 1} de {orderWizardSteps.length}:
              </span>{" "}
              {step.title}
            </h2>
            <p className="fieldHint">{step.description}</p>
          </div>
          <Feedback
            errors={state.fieldErrors.map((error) => error.message)}
            message={state.message}
            stepUpHref={state.stepUpHref}
          />
          {state.step === "where" && <WhereStep state={state} controller={controller} />}
          {state.step === "what" && <WhatStep state={state} controller={controller} />}
          {state.step === "service" && <ServiceStep state={state} controller={controller} />}
          {state.step === "confirm" && (
            <ConfirmStep state={state} controller={controller} versionsConfigured={versionsConfigured} />
          )}
          {state.step === "confirm" && !state.canOrder ? (
            <p className="notice noticeWarn">Tu rol no puede crear órdenes en la organización activa.</p>
          ) : (
            <div className="opsWizardNav">
              {index > 0 && (
                <button
                  type="button"
                  className="btn btnSecondary"
                  aria-disabled={busy}
                  onClick={() => void run(() => controller.back())}
                >
                  <span aria-hidden="true">←</span> Atrás
                </button>
              )}
              <button
                id={state.step === "service" ? quoteButtonId : undefined}
                type="submit"
                className="btn btnPrimary opsWizardPrimary"
                aria-disabled={busy}
                disabled={state.step === "confirm" && !versionsConfigured}
              >
                {primary.label}
                {primary.label === "Siguiente" && <span aria-hidden="true"> →</span>}
              </button>
            </div>
          )}
        </form>
        {!summaryFirst && summary}
      </div>
    </div>
  );
}

function WizardStepper({
  current,
  busy,
  onGoTo,
}: {
  readonly current: OrderWizardStep;
  readonly busy: boolean;
  readonly onGoTo: (step: OrderWizardStep) => void;
}) {
  const currentIndex = wizardStepIndex(current);
  return (
    <ol className="opsWizardSteps" aria-label="Pasos de la nueva orden">
      {orderWizardSteps.map((item, index) => {
        const status = index < currentIndex ? "done" : index === currentIndex ? "current" : "pending";
        const content = (
          <>
            <span className="opsWizardStepMarker" aria-hidden="true">
              {status === "done" ? "✓" : index + 1}
            </span>
            <span className="opsWizardStepLabel">
              <span className="srOnly">Paso {index + 1}: </span>
              {item.title}
              {status === "done" && <span className="srOnly"> (completado; volver a este paso)</span>}
              {status === "pending" && <span className="srOnly"> (pendiente)</span>}
            </span>
          </>
        );
        return (
          <li
            key={item.id}
            className={`opsWizardStep opsWizardStep-${status}`}
            aria-current={status === "current" ? "step" : undefined}
          >
            {status === "done" ? (
              <button type="button" className="opsWizardStepItem" aria-disabled={busy} onClick={() => onGoTo(item.id)}>
                {content}
              </button>
            ) : (
              <span className="opsWizardStepItem">{content}</span>
            )}
          </li>
        );
      })}
    </ol>
  );
}

// ---------------------------------------------------------------------------
// 1. Dónde

function WhereStep({ state, controller }: { readonly state: CreateOrderState; readonly controller: CreateOrderController }) {
  const coordinates = mayHandleExactCoordinates(state.role);
  return (
    <>
      <AddressFieldset
        side="origin"
        legend="Origen: quién envía"
        address={state.draft.origin}
        errors={state.fieldErrors}
        coordinates={coordinates}
        onChange={(patch) => controller.updateAddress("origin", patch)}
      />
      <AddressFieldset
        side="destination"
        legend="Destino: quién recibe"
        address={state.draft.destination}
        errors={state.fieldErrors}
        coordinates={coordinates}
        onChange={(patch) => controller.updateAddress("destination", patch)}
      />
    </>
  );
}

function AddressFieldset({
  side,
  legend,
  address,
  errors,
  coordinates,
  onChange,
}: {
  readonly side: "origin" | "destination";
  readonly legend: string;
  readonly address: AddressDraft;
  readonly errors: readonly FieldError[];
  /** Exact coordinates are only ever handled by DISPATCHER and PLATFORM_ADMIN (D5). */
  readonly coordinates: boolean;
  readonly onChange: (patch: Partial<AddressDraft>) => void;
}) {
  const id = (field: string) => fieldControlId(`${side}.${field}`);
  const error = (field: string) => errorFor(errors, `${side}.${field}`);
  return (
    <fieldset>
      <legend>{legend}</legend>
      <Field id={id("addressText")} label="Dirección" hint="Calle, número, colonia y ciudad." error={error("addressText")}>
        {(control) => (
          <input
            {...control}
            required
            minLength={8}
            value={address.addressText}
            onChange={(event) => onChange({ addressText: event.currentTarget.value })}
          />
        )}
      </Field>
      <Field id={id("contactName")} label="Nombre de contacto" error={error("contactName")}>
        {(control) => (
          <input
            {...control}
            required
            value={address.contactName}
            onChange={(event) => onChange({ contactName: event.currentTarget.value })}
          />
        )}
      </Field>
      <Field
        id={id("phone")}
        label="Teléfono"
        hint="10 dígitos; puedes anteponer +52 y separarlos con espacios o guiones."
        error={error("phone")}
      >
        {(control) => (
          <input
            {...control}
            type="tel"
            inputMode="tel"
            required
            maxLength={maximumPhoneInputLength}
            placeholder="667 123 4567"
            value={address.phone}
            onChange={(event) => onChange({ phone: event.currentTarget.value })}
          />
        )}
      </Field>
      {coordinates && (
        <div className="opsWizardPair">
          <Field id={id("coordinates")} label="Latitud" hint="Por ejemplo 24.8091." error={error("coordinates")}>
            {(control) => (
              <input
                {...control}
                inputMode="decimal"
                required
                value={address.lat}
                onChange={(event) => onChange({ lat: event.currentTarget.value })}
              />
            )}
          </Field>
          <Field id={id("longitude")} label="Longitud" hint="Por ejemplo -107.3940.">
            {(control) => (
              <input
                {...control}
                inputMode="decimal"
                required
                aria-invalid={error("coordinates") === undefined ? undefined : true}
                value={address.lng}
                onChange={(event) => onChange({ lng: event.currentTarget.value })}
              />
            )}
          </Field>
        </div>
      )}
      <Field
        id={id("references")}
        label="Referencias (opcional)"
        hint="Entre qué calles está, color de la fachada u otra seña."
        error={error("references")}
      >
        {(control) => (
          <input
            {...control}
            maxLength={500}
            value={address.references}
            onChange={(event) => onChange({ references: event.currentTarget.value })}
          />
        )}
      </Field>
    </fieldset>
  );
}

// ---------------------------------------------------------------------------
// 2. Qué se envía

function WhatStep({ state, controller }: { readonly state: CreateOrderState; readonly controller: CreateOrderController }) {
  const packages = state.draft.packages;
  const errors = state.fieldErrors;
  return (
    <>
      {packages.map((item, index) => {
        const id = (field: string) => fieldControlId(`packages.${index}.${field}`);
        const error = (field: string) => errorFor(errors, `packages.${index}.${field}`);
        const change = (patch: Partial<typeof item>) => controller.updatePackage(index, patch);
        return (
          <fieldset key={index}>
            <legend>Paquete {index + 1}</legend>
            <Field
              id={id("description")}
              label="Descripción"
              hint="Qué contiene, por ejemplo documentos o ropa."
              error={error("description")}
            >
              {(control) => (
                <input
                  {...control}
                  required
                  maxLength={250}
                  value={item.description}
                  onChange={(event) => change({ description: event.currentTarget.value })}
                />
              )}
            </Field>
            <div className="opsWizardPair">
              <Field id={id("weightGrams")} label="Peso (gramos)" error={error("weightGrams")}>
                {(control) => (
                  <input
                    {...control}
                    inputMode="numeric"
                    required
                    value={item.weightGrams}
                    onChange={(event) => change({ weightGrams: event.currentTarget.value })}
                  />
                )}
              </Field>
              <Field
                id={id("declaredValue")}
                label="Valor declarado (MXN)"
                hint="Hasta 2 decimales."
                error={error("declaredValue")}
              >
                {(control) => (
                  <input
                    {...control}
                    inputMode="decimal"
                    required
                    value={item.declaredValue}
                    onChange={(event) => change({ declaredValue: event.currentTarget.value })}
                  />
                )}
              </Field>
            </div>
            <fieldset>
              <legend>Medidas en milímetros (opcional)</legend>
              {error("dimensions") !== undefined && <p className="fieldError">{error("dimensions")}</p>}
              <div className="opsWizardTrio">
                <Field id={id("dimensions")} label="Largo">
                  {(control) => (
                    <input
                      {...control}
                      inputMode="numeric"
                      aria-invalid={error("dimensions") === undefined ? undefined : true}
                      value={item.lengthMm}
                      onChange={(event) => change({ lengthMm: event.currentTarget.value })}
                    />
                  )}
                </Field>
                <Field id={id("width")} label="Ancho">
                  {(control) => (
                    <input
                      {...control}
                      inputMode="numeric"
                      aria-invalid={error("dimensions") === undefined ? undefined : true}
                      value={item.widthMm}
                      onChange={(event) => change({ widthMm: event.currentTarget.value })}
                    />
                  )}
                </Field>
                <Field id={id("height")} label="Alto">
                  {(control) => (
                    <input
                      {...control}
                      inputMode="numeric"
                      aria-invalid={error("dimensions") === undefined ? undefined : true}
                      value={item.heightMm}
                      onChange={(event) => change({ heightMm: event.currentTarget.value })}
                    />
                  )}
                </Field>
              </div>
            </fieldset>
            {packages.length > 1 && (
              <div className="opsFormActions">
                <button
                  type="button"
                  className="btn btnSecondary"
                  onClick={() => {
                    controller.removePackage(index);
                    focusLater("order-add-package", stepHeadingId);
                  }}
                >
                  Quitar paquete {index + 1}
                </button>
              </div>
            )}
          </fieldset>
        );
      })}
      {packages.length < maximumPackages ? (
        <div className="opsFormActions">
          <button
            id="order-add-package"
            type="button"
            className="btn btnSecondary"
            onClick={() => {
              controller.addPackage();
              focusLater(fieldControlId(`packages.${packages.length}.description`));
            }}
          >
            Agregar paquete
          </button>
        </div>
      ) : (
        <p className="fieldHint">Máximo {maximumPackages} paquetes por orden.</p>
      )}
    </>
  );
}

// ---------------------------------------------------------------------------
// 3. Servicio y precio

function ServiceStep({ state, controller }: { readonly state: CreateOrderState; readonly controller: CreateOrderController }) {
  const draft = state.draft;
  const errors = state.fieldErrors;
  const quote = state.quote;
  return (
    <>
      <fieldset>
        <legend>Servicio</legend>
        <Field id={fieldControlId("serviceType")} label="Tipo de servicio" error={errorFor(errors, "serviceType")}>
          {(control) => (
            <select
              {...control}
              required
              value={draft.serviceType}
              onChange={(event) => controller.updateDraft({ serviceType: event.currentTarget.value })}
            >
              <option value="" disabled>
                Selecciona
              </option>
              {serviceTypes.map((value) => (
                <option key={value} value={value}>
                  {serviceTypeLabel(value)}
                </option>
              ))}
            </select>
          )}
        </Field>
        <CheckboxField
          id={fieldControlId("consolidatedRoute")}
          checked={draft.consolidatedRoute}
          onChange={(checked) => controller.updateDraft({ consolidatedRoute: checked })}
        >
          Ruta consolidada
        </CheckboxField>
      </fieldset>

      <fieldset>
        <legend>Ventana de entrega (opcional)</legend>
        <p className="fieldHint">
          En hora de Mazatlán, no en la de este equipo. Déjala vacía para usar el horario de la zona; máximo{" "}
          {maximumServiceWindowHours} horas.
        </p>
        <div className="opsWizardPair">
          <Field id={fieldControlId("serviceWindow")} label="Desde" error={errorFor(errors, "serviceWindow")}>
            {(control) => (
              <input
                {...control}
                type="datetime-local"
                step={60}
                value={draft.serviceWindowFrom}
                onChange={(event) => controller.updateDraft({ serviceWindowFrom: event.currentTarget.value })}
              />
            )}
          </Field>
          <Field id={fieldControlId("serviceWindowTo")} label="Hasta">
            {(control) => (
              <input
                {...control}
                type="datetime-local"
                step={60}
                aria-invalid={errorFor(errors, "serviceWindow") === undefined ? undefined : true}
                value={draft.serviceWindowTo}
                onChange={(event) => controller.updateDraft({ serviceWindowTo: event.currentTarget.value })}
              />
            )}
          </Field>
        </div>
      </fieldset>

      <fieldset>
        <legend>Cobro y cuenta cliente</legend>
        <Field
          id={fieldControlId("codAmount")}
          label="Cobro contra entrega (MXN, opcional)"
          hint={
            <>
              Lo cobra el repartidor al entregar, en pesos con hasta 2 decimales (por ejemplo 150.50); máximo{" "}
              <Money cents={maximumCodExpectedCents} /> por pedido. Vacío si no hay cobro.
            </>
          }
          error={errorFor(errors, "codAmount")}
        >
          {(control) => (
            <input
              {...control}
              inputMode="decimal"
              value={draft.codAmount}
              onChange={(event) => controller.updateDraft({ codAmount: event.currentTarget.value })}
            />
          )}
        </Field>
        <Field
          id={fieldControlId("clientAccountId")}
          label="ID de la cuenta cliente (opcional)"
          hint="Déjalo vacío si el envío no pertenece a una cuenta cliente; se aplica la tarifa ocasional."
          error={errorFor(errors, "clientAccountId")}
        >
          {(control) => (
            <input
              {...control}
              spellCheck={false}
              value={draft.clientAccountId}
              onChange={(event) => controller.updateDraft({ clientAccountId: event.currentTarget.value })}
            />
          )}
        </Field>
      </fieldset>

      {state.canAuthorizeLowPrice && (
        <fieldset>
          <legend>Envío de bajo monto</legend>
          <CheckboxField
            id={fieldControlId("authorizeLowPrice")}
            checked={draft.authorizeLowPrice}
            onChange={(checked) => controller.updateDraft({ authorizeLowPrice: checked })}
          >
            Autorizar envío de bajo monto
          </CheckboxField>
          {draft.authorizeLowPrice && (
            <Field
              id={fieldControlId("lowPriceReason")}
              label="Motivo de la autorización"
              error={errorFor(errors, "lowPriceReason")}
            >
              {(control) => (
                <input
                  {...control}
                  required
                  maxLength={lowPriceAuthorizationReasonMaximum}
                  value={draft.lowPriceReason}
                  onChange={(event) => controller.updateDraft({ lowPriceReason: event.currentTarget.value })}
                />
              )}
            </Field>
          )}
          <p className="fieldHint">
            Solo para envíos de 52 MXN o menos ({vatIncludedLabel}) sin ruta consolidada. El motivo queda auditado con
            tu usuario; no escribas nombres, teléfonos, correos ni direcciones.
            {state.role === "PLATFORM_ADMIN" && " Requiere verificar tu identidad (MFA)."}
          </p>
        </fieldset>
      )}

      <div className="opsWizardPriceStatus" role="status" aria-live="polite">
        {state.busy === "quote" ? (
          <p>Calculando el precio…</p>
        ) : quote === null ? (
          <p className="fieldHint">Cuando los datos estén completos, calcula el precio para ver el desglose.</p>
        ) : (
          <>
            <p>
              Precio calculado: <Money cents={quote.total.amount_cents} strong /> ({vatIncludedLabel}). El desglose está
              en el resumen.
            </p>
            {isHeldByLowPriceGuard(quote) && (
              <p className="notice noticeWarn">
                El total es de 52 MXN o menos ({vatIncludedLabel}): marca “Ruta consolidada” o “Autorizar envío de bajo
                monto” con su motivo y calcula el precio de nuevo.
              </p>
            )}
          </>
        )}
      </div>
    </>
  );
}

// ---------------------------------------------------------------------------
// 4. Confirmar

function ConfirmStep({
  state,
  controller,
  versionsConfigured,
}: {
  readonly state: CreateOrderState;
  readonly controller: CreateOrderController;
  readonly versionsConfigured: boolean;
}) {
  const draft = state.draft;
  const errors = state.fieldErrors;
  return (
    <>
      <Field id={fieldControlId("payerType")} label="Quién paga" error={errorFor(errors, "payerType")}>
        {(control) => (
          <select
            {...control}
            required
            value={draft.payerType}
            onChange={(event) => controller.updateDraft({ payerType: event.currentTarget.value })}
          >
            <option value="" disabled>
              Selecciona
            </option>
            {payerTypes.map((value) => (
              <option key={value} value={value}>
                {payerLabels[value]}
              </option>
            ))}
          </select>
        )}
      </Field>
      <fieldset>
        <legend>Aceptación del cliente</legend>
        <CheckboxField
          id={fieldControlId("accepted")}
          checked={draft.accepted}
          error={errorFor(errors, "accepted")}
          onChange={(checked) => controller.updateDraft({ accepted: checked })}
        >
          El cliente vio el desglose y aceptó los términos y el aviso de privacidad vigentes.
        </CheckboxField>
        <CheckboxField
          id={fieldControlId("restrictedGoodsAcknowledged")}
          checked={draft.restrictedGoodsAcknowledged}
          error={errorFor(errors, "restrictedGoodsAcknowledged")}
          onChange={(checked) => controller.updateDraft({ restrictedGoodsAcknowledged: checked })}
        >
          Confirmo que el envío no contiene artículos prohibidos (queda registrado en la orden).
        </CheckboxField>
        <p className="fieldHint">
          La aceptación queda registrada como asistida por ti, con las versiones vigentes de los términos y del aviso de
          privacidad.
        </p>
      </fieldset>
      {!versionsConfigured && (
        <p id={fieldControlId("acceptanceVersions")} className="notice noticeWarn" role="alert" tabIndex={-1}>
          {acceptanceVersionsUnavailableMessage}
        </p>
      )}
      {state.confirmationNeedsMfa && (
        <p className="notice noticeInfo">
          Confirmar la orden requiere verificar tu identidad (MFA). Sin esa verificación, la orden se crea en borrador y
          la confirmas desde su detalle.
        </p>
      )}
      <p className="fieldHint">Al continuar, la orden se crea y se confirma: queda lista para preparar.</p>
    </>
  );
}

function CheckboxField({
  id,
  checked,
  error,
  onChange,
  children,
}: {
  readonly id: string;
  readonly checked: boolean;
  readonly error?: string;
  readonly onChange: (checked: boolean) => void;
  readonly children: ReactNode;
}) {
  const errorId = `${id}-error`;
  return (
    <div className="field">
      <label className="opsCheckbox" htmlFor={id}>
        <input
          id={id}
          type="checkbox"
          checked={checked}
          aria-invalid={error === undefined ? undefined : true}
          aria-describedby={error === undefined ? undefined : errorId}
          onChange={(event) => onChange(event.currentTarget.checked)}
        />
        <span>{children}</span>
      </label>
      {error !== undefined && (
        <p id={errorId} className="fieldError">
          {error}
        </p>
      )}
    </div>
  );
}
