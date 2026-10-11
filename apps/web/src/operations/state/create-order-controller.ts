import type { OrderActionsApi } from "../api/order-actions-api";
import type { OrdersApi } from "../api/orders-api";
import type { AcceptanceVersions } from "../contracts/acceptance-versions";
import { TenantApiError } from "../api/tenant-request";
import { canAuthorizeLowPrice, canPerform, requiresMfa } from "../contracts/capabilities";
import {
  buildCreateOrderBody,
  buildCreateQuoteBody,
  lowPriceAuthorizationNotNeededMessage,
  maximumPackages,
  quoteDraftErrors,
  type AddressDraft,
  type CreatedOrder,
  type FieldError,
  type PackageDraft,
  type Quote,
} from "../contracts/create-order";
import type { OrderStatus } from "../contracts/operations-dashboard";
import { transitionRejectionMessage } from "../contracts/order-transitions";
import {
  acceptanceDraftOf,
  emptyPackageDraft,
  firstInvalidStep,
  initialOrderWizardDraft,
  nextWizardStep,
  previousWizardStep,
  quoteDraftOf,
  quoteIsStale,
  stepOfField,
  wizardConfirmAction,
  wizardConfirmationReason,
  wizardStepErrors,
  wizardStepIndex,
  type OrderWizardDraft,
  type OrderWizardStep,
  type WizardValidationContext,
} from "../contracts/order-wizard";
import type { OperationsSession } from "../session/operations-session";
import { ExternalStore } from "./external-store";
import { transitionReturnUrl } from "./order-transition-controller";
import { PendingSubmissions } from "./pending-submissions";
import { describeFailure } from "./tenant-error-messages";

export const createOrderPath = "/ops/orders/new";

/** The request in flight: the price, the order, or the confirmation that follows it. */
export type CreateOrderBusy = "quote" | "order" | "confirm";

/**
 * The order the wizard created and what its confirmation returned. A confirmation that did not
 * succeed never reads as confirmed: `draft` means the server refused it and the order stays in
 * DRAFT; `unknown` means there was no valid answer, so the person checks the order detail.
 */
export type CreateOrderOutcome =
  | {
      readonly kind: "confirmed";
      readonly order: CreatedOrder;
      /** The status transitionOrder returned (CONFIRMED). */
      readonly status: OrderStatus;
      /**
       * D6-COD-EXPECTED: the COD (integer cents, 0 = none) sent with the order. Order responses
       * do not carry it, so the screen shows the amount it submitted.
       */
      readonly codExpectedCents: number;
    }
  | {
      readonly kind: "not_confirmed";
      readonly order: CreatedOrder;
      readonly codExpectedCents: number;
      readonly certainty: "draft" | "unknown";
      readonly message: string;
      /** `/login?mfa=required&return_url=<order detail>` when only MFA is missing. */
      readonly stepUpHref: string | null;
      /** A network or server failure: the same confirmation is retried with the same key. */
      readonly retryable: boolean;
    };

export interface CreateOrderState {
  readonly phase: "no_session" | "loading" | "ready" | "access_unavailable";
  readonly role: string | null;
  readonly canQuote: boolean;
  /** createOrder and the transitionOrder that confirms it (DISPATCHER, PLATFORM_ADMIN). */
  readonly canOrder: boolean;
  /** LOW-PRICE-MANUAL-AUTH-2026-10-02: shows "Autorizar envío de bajo monto" (DISPATCHER, PLATFORM_ADMIN). */
  readonly canAuthorizeLowPrice: boolean;
  /** PLATFORM_ADMIN confirms only with MFA; the last step says so beforehand. */
  readonly confirmationNeedsMfa: boolean;
  readonly step: OrderWizardStep;
  readonly draft: OrderWizardDraft;
  /** Messages of the current step, each with its field. */
  readonly fieldErrors: readonly FieldError[];
  /** Increments whenever a step is refused, so the screen can move focus to the first message. */
  readonly validationAttempt: number;
  /** The price of the current draft; dropped as soon as anything it was calculated from changes. */
  readonly quote: Quote | null;
  readonly busy: CreateOrderBusy | null;
  readonly outcome: CreateOrderOutcome | null;
  readonly message: string | null;
  readonly stepUpHref: string | null;
}

export interface CreateOrderDependencies {
  readonly readSession: () => OperationsSession | null;
  readonly createApi: (session: OperationsSession) => OrdersApi;
  /** transitionOrder, shared with the order detail's "Siguiente paso". */
  readonly createActionsApi: (session: OperationsSession) => Pick<OrderActionsApi, "transitionOrder">;
  readonly loadRole: (session: OperationsSession, signal: AbortSignal) => Promise<string | null>;
  /** Owner-set terms/privacy versions from the server; `null` blocks confirmation. */
  readonly acceptanceVersions: AcceptanceVersions | null;
  readonly now?: () => Date;
}

const wizardStart = {
  step: "where",
  draft: initialOrderWizardDraft,
  fieldErrors: [],
  quote: null,
  busy: null,
  outcome: null,
  message: null,
  stepUpHref: null,
} as const satisfies Partial<CreateOrderState>;

const initialState: CreateOrderState = {
  phase: "no_session",
  role: null,
  canQuote: false,
  canOrder: false,
  canAuthorizeLowPrice: false,
  confirmationNeedsMfa: false,
  validationAttempt: 0,
  ...wizardStart,
};

/** createOrder 409: the quote was used or expired, or the acceptance fell outside the window. */
export const orderNotCreatedMessage =
  "No se creó la orden: la cotización pudo usarse o expirar, o la aceptación quedó fuera de límites. Calcula el precio de nuevo y vuelve a confirmar.";

export const priceChangedWhileQuotingMessage =
  "Cambiaste datos del envío mientras se calculaba el precio; calcúlalo de nuevo.";

/**
 * UI-PHASE3-ORDER-WIZARD-2026-10-10: Dónde → Qué se envía → Servicio y precio → Confirmar, then
 * createOrder followed by transitionOrder DRAFT → CONFIRMED.
 *
 * REST responses are the only authority: the screen shows the quote and the order exactly as
 * the API returned them and never reports a confirmation the server did not answer. Every write
 * keeps its Idempotency-Key until the server gives a definitive answer, so a retry after a
 * network or server failure resends the same request and never creates a second order or a
 * second confirmation. Switching tenant (or losing the session) drops the draft, the quote, the
 * outcome and the pending keys; nothing is stored in the browser or logged.
 */
export class CreateOrderController extends ExternalStore<CreateOrderState> {
  private session: OperationsSession | null = null;
  private api: OrdersApi | null = null;
  private actionsApi: Pick<OrderActionsApi, "transitionOrder"> | null = null;
  private generation = 0;
  private controller: AbortController | null = null;
  private readonly pending: PendingSubmissions;
  /** The createQuote body the shown quote was calculated from. */
  private quoteKey: string | null = null;

  public constructor(
    private readonly dependencies: CreateOrderDependencies,
    pending = new PendingSubmissions(),
  ) {
    super(initialState);
    this.pending = pending;
  }

  public async start(): Promise<void> {
    this.generation += 1;
    const generation = this.generation;
    this.controller?.abort();
    this.controller = new AbortController();
    this.pending.clear();
    this.quoteKey = null;
    const session = this.dependencies.readSession();
    this.session = session;
    this.api = session === null ? null : this.dependencies.createApi(session);
    this.actionsApi = session === null ? null : this.dependencies.createActionsApi(session);
    this.update({
      ...initialState,
      phase: session === null ? "no_session" : "loading",
    });
    if (session === null) return;
    let role: string | null;
    try {
      role = await this.dependencies.loadRole(session, this.controller.signal);
    } catch {
      role = null;
    }
    if (generation !== this.generation) return;
    const canQuote = canPerform(role, "createQuote");
    this.update({
      phase: canQuote ? "ready" : "access_unavailable",
      role,
      canQuote,
      canOrder: canPerform(role, "createOrder") && canPerform(role, "transitionOrder"),
      canAuthorizeLowPrice: canQuote && canAuthorizeLowPrice(role),
      confirmationNeedsMfa: requiresMfa(role, "transitionOrder"),
    });
  }

  public stop(): void {
    this.generation += 1;
    this.controller?.abort();
    this.pending.clear();
  }

  // -------------------------------------------------------------------------
  // Draft

  public updateDraft(patch: Partial<OrderWizardDraft>): void {
    if (!this.editable) return;
    this.applyDraft({ ...this.getSnapshot().draft, ...patch });
  }

  public updateAddress(side: "origin" | "destination", patch: Partial<AddressDraft>): void {
    if (!this.editable) return;
    const draft = this.getSnapshot().draft;
    this.applyDraft({ ...draft, [side]: { ...draft[side], ...patch } });
  }

  public updatePackage(index: number, patch: Partial<PackageDraft>): void {
    if (!this.editable) return;
    const draft = this.getSnapshot().draft;
    if (index < 0 || index >= draft.packages.length) return;
    this.applyDraft({
      ...draft,
      packages: draft.packages.map((item, position) => (position === index ? { ...item, ...patch } : item)),
    });
  }

  public addPackage(): void {
    const draft = this.getSnapshot().draft;
    if (!this.editable || draft.packages.length >= maximumPackages) return;
    this.applyDraft({ ...draft, packages: [...draft.packages, emptyPackageDraft] });
  }

  public removePackage(index: number): void {
    const draft = this.getSnapshot().draft;
    if (!this.editable || draft.packages.length <= 1 || index < 0 || index >= draft.packages.length) return;
    this.applyDraft({ ...draft, packages: draft.packages.filter((_, position) => position !== index) });
  }

  // -------------------------------------------------------------------------
  // Steps

  /** Completes the current step: shows what is missing, or moves to the next one. */
  public next(): void {
    const state = this.getSnapshot();
    if (!this.navigable) return;
    const following = nextWizardStep(state.step);
    if (following === null) return;
    const now = this.now();
    const errors = wizardStepErrors(state.step, state.draft, this.context(state.quote, now));
    if (errors.length > 0) {
      this.refuse(state.step, errors, now);
      return;
    }
    this.update({ step: following, fieldErrors: [], message: null, stepUpHref: null });
  }

  public back(): void {
    const previous = previousWizardStep(this.getSnapshot().step);
    if (!this.navigable || previous === null) return;
    this.update({ step: previous, fieldErrors: [], message: null, stepUpHref: null });
  }

  /** The stepper only goes back to a completed step; moving forward always validates. */
  public goTo(step: OrderWizardStep): void {
    const state = this.getSnapshot();
    if (!this.navigable || wizardStepIndex(step) >= wizardStepIndex(state.step)) return;
    this.update({ step, fieldErrors: [], message: null, stepUpHref: null });
  }

  // -------------------------------------------------------------------------
  // Requests

  /** Step 3: createQuote for the current shipment. */
  public async requestQuote(): Promise<void> {
    const api = this.api;
    const state = this.getSnapshot();
    if (api === null || !state.canQuote || state.busy !== null || state.outcome !== null) return;
    const quoteDraft = quoteDraftOf(state.draft, state.canAuthorizeLowPrice);
    const errors = quoteDraftErrors(quoteDraft);
    if (errors.length > 0) {
      // An address or a package fixed in an earlier step sends the person back to it.
      const step = errors.map((error) => stepOfField(error.field)).sort((a, b) => wizardStepIndex(a) - wizardStepIndex(b))[0];
      this.refuse(step, errors.filter((error) => stepOfField(error.field) === step), this.now());
      return;
    }
    const result = buildCreateQuoteBody(quoteDraft);
    if (!result.ok) return;
    const key = JSON.stringify(result.body);
    const generation = this.generation;
    const submission = this.pending.prepare("quote", key, () => result.body);
    this.quoteKey = null;
    this.update({ busy: "quote", quote: null, fieldErrors: [], message: null, stepUpHref: null });
    try {
      const quote = await api.createQuote(submission.payload, submission.key, this.controller?.signal);
      if (generation !== this.generation) return;
      this.pending.settle("quote");
      if (this.quoteBodyKey(this.getSnapshot().draft) !== key) {
        this.update({ busy: null, message: priceChangedWhileQuotingMessage });
        return;
      }
      this.quoteKey = key;
      this.update({ quote, busy: null });
    } catch (error) {
      if (generation !== this.generation) return;
      this.settleUnlessRetryable("quote", error);
      const view = describeFailure(
        error,
        createOrderPath,
        {},
        result.body.low_price_authorization === undefined
          ? "El servidor rechazó la cotización."
          : lowPriceAuthorizationNotNeededMessage,
      );
      const message =
        error instanceof TenantApiError && error.category === "invalid"
          ? "El servidor rechazó la cotización: revisa cobertura en Culiacán, direcciones, teléfonos y límites de paquetes."
          : view.message;
      this.update({ busy: null, message, stepUpHref: view.stepUpHref });
    }
  }

  /**
   * Step 4: createOrder, then transitionOrder DRAFT → CONFIRMED with the step 4 acknowledgement.
   * The owner decided the order leaves the wizard CONFIRMED (UI-PHASE3-ORDER-WIZARD-2026-10-10).
   */
  public async submit(): Promise<void> {
    const api = this.api;
    const state = this.getSnapshot();
    if (api === null || this.actionsApi === null || !state.canOrder || state.busy !== null || state.outcome !== null)
      return;
    const now = this.now();
    const invalid = firstInvalidStep(state.draft, this.context(state.quote, now));
    if (invalid !== null || state.quote === null) {
      if (invalid !== null) this.refuse(invalid.step, invalid.errors, now);
      return;
    }
    const result = buildCreateOrderBody(
      state.quote.id,
      acceptanceDraftOf(state.draft),
      this.dependencies.acceptanceVersions,
      now,
    );
    if (!result.ok) {
      this.refuse(state.step, result.errors.map((message) => ({ field: "acceptedAt", message })), now);
      return;
    }
    // A retry of the same acceptance resends the stored body, accepted_at included.
    const fingerprint = JSON.stringify({ ...result.body, acceptance: { ...result.body.acceptance, accepted_at: null } });
    const submission = this.pending.prepare("order", fingerprint, () => result.body);
    const generation = this.generation;
    this.update({ busy: "order", fieldErrors: [], message: null, stepUpHref: null });
    let order: CreatedOrder;
    try {
      order = await api.createOrder(submission.payload, submission.key, this.controller?.signal);
    } catch (error) {
      if (generation !== this.generation) return;
      this.settleUnlessRetryable("order", error);
      if (error instanceof TenantApiError && error.category === "conflict") {
        // The price has to be calculated again before another attempt.
        this.quoteKey = null;
        this.update({ busy: null, step: "service", quote: null, message: orderNotCreatedMessage, stepUpHref: null });
        return;
      }
      const view = describeFailure(error, createOrderPath, {}, orderNotCreatedMessage);
      this.update({ busy: null, message: view.message, stepUpHref: view.stepUpHref });
      return;
    }
    if (generation !== this.generation) return;
    this.pending.settle("order");
    this.quoteKey = null;
    await this.confirm(order, submission.payload.cod_expected_cents ?? 0);
  }

  /** Sends the same confirmation again (same key) after a network or server failure. */
  public async retryConfirmation(): Promise<void> {
    const state = this.getSnapshot();
    const outcome = state.outcome;
    if (state.busy !== null || outcome === null || outcome.kind !== "not_confirmed" || !outcome.retryable) return;
    await this.confirm(outcome.order, outcome.codExpectedCents);
  }

  /** Starts a new capture in the same tenant. */
  public reset(): void {
    if (this.getSnapshot().busy !== null) return;
    this.pending.clear();
    this.quoteKey = null;
    this.update({ ...wizardStart });
  }

  public get activeSession(): OperationsSession | null {
    return this.session;
  }

  // -------------------------------------------------------------------------

  private async confirm(order: CreatedOrder, codExpectedCents: number): Promise<void> {
    const actions = this.actionsApi;
    if (actions === null) return;
    const generation = this.generation;
    const submission = this.pending.prepare("confirm", JSON.stringify([order.id, order.version]), () => order.id);
    this.update({ busy: "confirm", quote: null, fieldErrors: [], message: null, stepUpHref: null });
    try {
      const confirmed = await actions.transitionOrder(
        order.id,
        wizardConfirmAction,
        wizardConfirmationReason,
        order.version,
        true,
        submission.key,
        this.controller?.signal,
      );
      // The API client already refuses any other answer; an order is never shown confirmed
      // unless the server said so.
      if (confirmed.id !== order.id || confirmed.status !== wizardConfirmAction.target)
        throw new TenantApiError("invalid");
      if (generation !== this.generation) return;
      this.pending.settle("confirm");
      this.update({
        busy: null,
        outcome: { kind: "confirmed", order, status: wizardConfirmAction.target, codExpectedCents },
      });
    } catch (error) {
      if (generation !== this.generation) return;
      const retryable = error instanceof TenantApiError && error.retryable;
      if (!retryable) this.pending.settle("confirm");
      const view = confirmationFailureView(error, order.id);
      this.update({
        busy: null,
        outcome: { kind: "not_confirmed", order, codExpectedCents, ...view, retryable },
      });
    }
  }

  private get editable(): boolean {
    const state = this.getSnapshot();
    return state.phase === "ready" && state.outcome === null && state.busy !== "order" && state.busy !== "confirm";
  }

  private get navigable(): boolean {
    const state = this.getSnapshot();
    return state.phase === "ready" && state.outcome === null && state.busy === null;
  }

  private now(): Date {
    return this.dependencies.now?.() ?? new Date();
  }

  private context(quote: Quote | null, now: Date): WizardValidationContext {
    return {
      now,
      quote,
      canAuthorizeLowPrice: this.getSnapshot().canAuthorizeLowPrice,
      acceptanceVersions: this.dependencies.acceptanceVersions,
    };
  }

  /** The createQuote body of `draft`, or null while it is not valid. */
  private quoteBodyKey(draft: OrderWizardDraft): string | null {
    const result = buildCreateQuoteBody(quoteDraftOf(draft, this.getSnapshot().canAuthorizeLowPrice));
    return result.ok ? JSON.stringify(result.body) : null;
  }

  /**
   * A changed shipment drops the price it no longer matches. Messages already shown follow the
   * fields as they are fixed: a fixed field loses its message, the others keep theirs.
   */
  private applyDraft(draft: OrderWizardDraft): void {
    const state = this.getSnapshot();
    let quote = state.quote;
    if (quote !== null && (this.quoteKey === null || this.quoteBodyKey(draft) !== this.quoteKey)) {
      quote = null;
      this.quoteKey = null;
    }
    const fieldErrors =
      state.fieldErrors.length === 0
        ? state.fieldErrors
        : wizardStepErrors(state.step, draft, this.context(quote, this.now())).filter((error) =>
            state.fieldErrors.some((shown) => shown.field === error.field),
          );
    this.update({ draft, quote, fieldErrors });
  }

  /** Shows the step that is not complete with its messages; an unusable price is dropped. */
  private refuse(step: OrderWizardStep, errors: readonly FieldError[], now: Date): void {
    const state = this.getSnapshot();
    const stale = state.quote !== null && quoteIsStale(state.quote, now);
    if (stale) this.quoteKey = null;
    this.update({
      step,
      fieldErrors: errors,
      validationAttempt: state.validationAttempt + 1,
      quote: stale ? null : state.quote,
      message: null,
      stepUpHref: null,
    });
  }

  private settleUnlessRetryable(scope: string, error: unknown): void {
    if (!(error instanceof TenantApiError) || !error.retryable) this.pending.settle(scope);
  }
}

/**
 * What a failed confirmation tells the person. A 409 names the unmet rule with the
 * ORD-002-GUARD-CODES-2026-10-05 messages; a missing MFA offers the step-up back to the order
 * detail, where "Siguiente paso" can confirm it. Only a refusal is certain to leave the order in
 * DRAFT; any other failure says nothing about the state it is in.
 */
export function confirmationFailureView(
  error: unknown,
  orderId: string,
): { readonly message: string; readonly stepUpHref: string | null; readonly certainty: "draft" | "unknown" } {
  if (error instanceof TenantApiError) {
    switch (error.category) {
      case "conflict":
        return { message: transitionRejectionMessage(error.code), stepUpHref: null, certainty: "draft" };
      case "forbidden":
      case "unauthorized":
        return { ...describeFailure(error, transitionReturnUrl(orderId)), certainty: "draft" };
      case "network":
      case "unavailable":
        return { ...describeFailure(error, transitionReturnUrl(orderId)), certainty: "unknown" };
      case "not_found":
      case "invalid":
        return { message: "No recibimos una respuesta válida de la confirmación.", stepUpHref: null, certainty: "unknown" };
    }
  }
  return { message: "No fue posible confirmar la orden.", stepUpHref: null, certainty: "unknown" };
}
