import type { OrdersApi } from "../api/orders-api";
import type { AcceptanceVersions } from "../contracts/acceptance-versions";
import { TenantApiError } from "../api/tenant-request";
import { canAuthorizeLowPrice, canPerform } from "../contracts/capabilities";
import {
  buildCreateOrderBody,
  buildCreateQuoteBody,
  confirmationBlockerLabels,
  evaluateConfirmation,
  lowPriceAuthorizationNotNeededMessage,
  type AcceptanceDraft,
  type CreatedOrder,
  type Quote,
  type QuoteDraft,
} from "../contracts/create-order";
import type { OperationsSession } from "../session/operations-session";
import { ExternalStore } from "./external-store";
import { PendingSubmissions } from "./pending-submissions";
import { describeFailure } from "./tenant-error-messages";

export const createOrderPath = "/ops/orders/new";

export interface CreateOrderState {
  readonly phase: "no_session" | "loading" | "ready" | "access_unavailable";
  readonly role: string | null;
  readonly canQuote: boolean;
  readonly canOrder: boolean;
  /** LOW-PRICE-MANUAL-AUTH-2026-10-02: shows "Autorizar envío de bajo monto" (DISPATCHER, PLATFORM_ADMIN). */
  readonly canAuthorizeLowPrice: boolean;
  readonly quote: Quote | null;
  readonly order: CreatedOrder | null;
  /**
   * D6-COD-EXPECTED: the COD (integer cents, 0 = none) sent with the order the server
   * created. Order responses do not carry it (VIEWER reads orders), so the screen shows
   * the amount it submitted and FIN-001 remains the authority afterwards.
   */
  readonly orderCodExpectedCents: number | null;
  readonly busy: boolean;
  readonly errors: readonly string[];
  readonly message: string | null;
  readonly stepUpHref: string | null;
  /** Changes on tenant switch and on reset so forms holding typed data remount empty. */
  readonly formKey: number;
}

export interface CreateOrderDependencies {
  readonly readSession: () => OperationsSession | null;
  readonly createApi: (session: OperationsSession) => OrdersApi;
  readonly loadRole: (session: OperationsSession, signal: AbortSignal) => Promise<string | null>;
  /** Owner-set terms/privacy versions from the server; `null` blocks confirmation. */
  readonly acceptanceVersions: AcceptanceVersions | null;
  readonly now?: () => Date;
}

const initialState: CreateOrderState = {
  phase: "no_session",
  role: null,
  canQuote: false,
  canOrder: false,
  canAuthorizeLowPrice: false,
  quote: null,
  order: null,
  orderCodExpectedCents: null,
  busy: false,
  errors: [],
  message: null,
  stepUpHref: null,
  formKey: 0,
};

/**
 * Quote → acceptance → order. REST responses are the only authority: the screen shows
 * the quote and the order exactly as the API returned them. Switching tenant (or
 * losing the session) drops the quote, the order, typed data and pending keys.
 */
export class CreateOrderController extends ExternalStore<CreateOrderState> {
  private session: OperationsSession | null = null;
  private api: OrdersApi | null = null;
  private generation = 0;
  private controller: AbortController | null = null;
  private readonly pending: PendingSubmissions;

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
    const session = this.dependencies.readSession();
    this.session = session;
    this.api = session === null ? null : this.dependencies.createApi(session);
    this.update({
      ...initialState,
      phase: session === null ? "no_session" : "loading",
      formKey: this.getSnapshot().formKey + 1,
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
      canOrder: canPerform(role, "createOrder"),
      canAuthorizeLowPrice: canQuote && canAuthorizeLowPrice(role),
    });
  }

  public stop(): void {
    this.generation += 1;
    this.controller?.abort();
    this.pending.clear();
  }

  public async requestQuote(draft: QuoteDraft): Promise<void> {
    const api = this.api;
    if (api === null || !this.getSnapshot().canQuote || this.getSnapshot().busy) return;
    // A role that cannot authorize never sends the field, whatever the form held.
    const result = buildCreateQuoteBody(
      this.getSnapshot().canAuthorizeLowPrice ? draft : { ...draft, authorizeLowPrice: false },
    );
    if (!result.ok) {
      this.update({ errors: result.errors, message: null, stepUpHref: null });
      return;
    }
    const generation = this.generation;
    const submission = this.pending.prepare("quote", JSON.stringify(result.body), () => result.body);
    this.update({ busy: true, errors: [], message: null, stepUpHref: null, order: null, orderCodExpectedCents: null });
    try {
      const quote = await api.createQuote(submission.payload, submission.key, this.controller?.signal);
      if (generation !== this.generation) return;
      this.pending.settle("quote");
      this.update({ quote, busy: false, message: "Cotización calculada por el servidor." });
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
      this.update({ busy: false, message, stepUpHref: view.stepUpHref });
    }
  }

  public async confirmOrder(draft: AcceptanceDraft): Promise<void> {
    const api = this.api;
    const state = this.getSnapshot();
    const quote = state.quote;
    if (api === null || quote === null || !state.canOrder || state.busy) return;
    const now = this.dependencies.now?.() ?? new Date();
    const blockers = evaluateConfirmation(quote, now);
    if (blockers.length > 0) {
      this.update({
        errors: blockers.map((blocker) => confirmationBlockerLabels[blocker]),
        message: null,
        stepUpHref: null,
      });
      return;
    }
    const result = buildCreateOrderBody(quote.id, draft, this.dependencies.acceptanceVersions, now);
    if (!result.ok) {
      this.update({ errors: result.errors, message: null, stepUpHref: null });
      return;
    }
    // A retry of the same acceptance resends the stored body, accepted_at included.
    const fingerprint = JSON.stringify({ ...result.body, acceptance: { ...result.body.acceptance, accepted_at: null } });
    const submission = this.pending.prepare("order", fingerprint, () => result.body);
    const generation = this.generation;
    this.update({ busy: true, errors: [], message: null, stepUpHref: null });
    try {
      const order = await api.createOrder(submission.payload, submission.key, this.controller?.signal);
      if (generation !== this.generation) return;
      this.pending.settle("order");
      this.update({
        order,
        orderCodExpectedCents: submission.payload.cod_expected_cents ?? 0,
        quote: null,
        busy: false,
        message: "Orden creada y confirmada por el servidor.",
      });
    } catch (error) {
      if (generation !== this.generation) return;
      this.settleUnlessRetryable("order", error);
      const view = describeFailure(
        error,
        createOrderPath,
        {},
        "El servidor no creó la orden: la cotización pudo usarse o expirar, o la aceptación quedó fuera de límites. Cotiza de nuevo.",
      );
      this.update({ busy: false, message: view.message, stepUpHref: view.stepUpHref });
    }
  }

  /** Starts a new capture in the same tenant. */
  public reset(): void {
    this.pending.clear();
    this.update({
      quote: null,
      order: null,
      orderCodExpectedCents: null,
      errors: [],
      message: null,
      stepUpHref: null,
      formKey: this.getSnapshot().formKey + 1,
    });
  }

  public get activeSession(): OperationsSession | null {
    return this.session;
  }

  private settleUnlessRetryable(scope: string, error: unknown): void {
    if (!(error instanceof TenantApiError) || !error.retryable) this.pending.settle(scope);
  }
}
