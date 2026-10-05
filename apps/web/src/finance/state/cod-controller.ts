import { TenantApiError } from "../../operations/api/tenant-request";
import { canListPendingCod, canPerform, requiresMfa } from "../../operations/contracts/capabilities";
import type { OperationsSession } from "../../operations/session/operations-session";
import { ExternalStore } from "../../operations/state/external-store";
import { PendingSubmissions } from "../../operations/state/pending-submissions";
import { describeFailure } from "../../operations/state/tenant-error-messages";
import type { CodApi } from "../api/cod-api";
import {
  buildRecordCodBody,
  canRecordCollection,
  financeConflictMessages,
  isCanonicalUuid,
  reconcilableRecord,
  type CodTransaction,
  type OrderFinancials,
  type PendingCodOrder,
} from "../contracts/cod";

export const codPath = "/finance/cod";

export function codReturnUrl(orderId: string | null): string {
  return orderId !== null && isCanonicalUuid(orderId) ? `${codPath}?order=${orderId}` : codPath;
}

export interface CodState {
  readonly phase: "no_session" | "loading" | "ready" | "access_unavailable";
  readonly role: string | null;
  readonly canRecord: boolean;
  readonly canReconcile: boolean;
  /**
   * listOrders with cod_pending_reconciliation (DISPATCHER; PLATFORM_ADMIN and FINANCE
   * with MFA, FIN-PENDING-COD-LIST-FINANCE-2026-10-02).
   */
  readonly canListPending: boolean;
  /** Orders whose collection is RECORDED and not RECONCILED; null until the API answered. */
  readonly pending: readonly PendingCodOrder[] | null;
  readonly pendingCursor: string | null;
  readonly pendingLoading: boolean;
  readonly mfaHint: string | null;
  readonly financials: OrderFinancials | null;
  /** Order whose financials GET is in flight; actions stay disabled until it is loaded. */
  readonly loadingOrder: string | null;
  /** Latest COD record the API returned in this tenant session. */
  readonly transaction: CodTransaction | null;
  readonly busy: boolean;
  readonly errors: readonly string[];
  readonly message: string | null;
  readonly stepUpHref: string | null;
  /** Changes on tenant switch and after a confirmed write so typed text is dropped. */
  readonly formKey: number;
}

export interface CodDependencies {
  readonly readSession: () => OperationsSession | null;
  readonly createApi: (session: OperationsSession) => CodApi;
  readonly loadRole: (session: OperationsSession, signal: AbortSignal) => Promise<string | null>;
  /** Order to reopen after returning from the MFA step-up. */
  readonly initialOrder?: () => string | null;
}

const initialState: CodState = {
  phase: "no_session",
  role: null,
  canRecord: false,
  canReconcile: false,
  canListPending: false,
  pending: null,
  pendingCursor: null,
  pendingLoading: false,
  mfaHint: null,
  financials: null,
  loadingOrder: null,
  transaction: null,
  busy: false,
  errors: [],
  message: null,
  stepUpHref: null,
  formKey: 0,
};

/**
 * FIN-001 COD control. getOrderFinancials is the authority on load, refresh and
 * after every write (success or conflict); a recorded or reconciled collection is
 * never assumed locally. A tenant switch drops the order, COD records and keys.
 */
export class CodController extends ExternalStore<CodState> {
  private api: CodApi | null = null;
  private session: OperationsSession | null = null;
  private generation = 0;
  /** Monotonic load token: only the latest load() may write `financials`. */
  private loadToken = 0;
  /** Monotonic token for the pending list: only the latest loadPending() may write it. */
  private pendingToken = 0;
  private controller: AbortController | null = null;
  private readonly pending: PendingSubmissions;

  public constructor(
    private readonly dependencies: CodDependencies,
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
    if (!canPerform(role, "getOrderFinancials")) {
      this.update({ phase: "access_unavailable", role });
      return;
    }
    this.update({
      phase: "ready",
      role,
      canRecord: canPerform(role, "recordCodCollection"),
      canReconcile: canPerform(role, "reconcileCod"),
      canListPending: canListPendingCod(role),
      mfaHint: requiresMfa(role, "getOrderFinancials")
        ? "Consultar y conciliar cobros con tu rol requiere verificar tu identidad (MFA)."
        : null,
    });
    await this.loadPending();
    if (generation !== this.generation) return;
    const initial = this.dependencies.initialOrder?.() ?? null;
    if (initial !== null && isCanonicalUuid(initial)) await this.load(initial);
  }

  /**
   * API-FIN-COD-VISIBILITY-2026-09-29: the list of collections awaiting
   * reconciliation, straight from listOrders. `more` appends the next page; any
   * other call replaces the list. Roles outside the filter never request it.
   */
  public async loadPending(more = false): Promise<void> {
    const api = this.api;
    const state = this.getSnapshot();
    if (api === null || state.phase !== "ready" || !state.canListPending) return;
    const cursor = more ? state.pendingCursor : null;
    if (more && cursor === null) return;
    const generation = this.generation;
    const token = ++this.pendingToken;
    const isLatest = () => generation === this.generation && token === this.pendingToken;
    this.update({ pendingLoading: true });
    try {
      const page = await api.pendingReconciliation(cursor, this.controller?.signal);
      if (!isLatest()) return;
      const previous = more ? (this.getSnapshot().pending ?? []) : [];
      const seen = new Set(previous.map((order) => order.id));
      this.update({
        pending: [...previous, ...page.items.filter((order) => !seen.has(order.id))],
        pendingCursor: page.next_cursor,
      });
    } catch (error) {
      if (!isLatest()) return;
      if (!more) this.update({ pending: null, pendingCursor: null });
      this.fail(error, null);
    } finally {
      if (isLatest()) this.update({ pendingLoading: false });
    }
  }

  /**
   * Opens a pending order and reconciles the collection its financials name, so
   * nobody types a COD record id. The record is taken only from the REST read.
   */
  public async reconcileFromList(orderId: string): Promise<void> {
    const state = this.getSnapshot();
    if (!state.canReconcile || state.busy || !isCanonicalUuid(orderId)) return;
    const generation = this.generation;
    await this.load(orderId);
    if (generation !== this.generation) return;
    const financials = this.getSnapshot().financials;
    const record = financials?.order_id === orderId ? reconcilableRecord(financials) : null;
    if (record === null) {
      if (financials?.order_id === orderId) {
        this.update({ errors: ["El cobro de esta orden ya no está pendiente de conciliar; se actualizó la lista."] });
        await this.loadPending();
      }
      return;
    }
    await this.reconcile(record.id);
  }

  public stop(): void {
    this.generation += 1;
    this.controller?.abort();
    this.pending.clear();
  }

  public async load(orderId: string): Promise<void> {
    const api = this.api;
    if (api === null || this.getSnapshot().phase !== "ready") return;
    if (!isCanonicalUuid(orderId)) {
      this.update({ errors: ["El ID de la orden no es válido."], message: null, stepUpHref: null });
      return;
    }
    const generation = this.generation;
    const token = ++this.loadToken;
    const isLatest = () => generation === this.generation && token === this.loadToken;
    const switching = this.getSnapshot().financials?.order_id !== orderId;
    this.update({
      loadingOrder: orderId,
      errors: [],
      message: null,
      stepUpHref: null,
      ...(switching ? { financials: null, transaction: null } : {}),
    });
    try {
      const financials = await api.financials(orderId, this.controller?.signal);
      if (!isLatest()) return;
      if (financials.order_id !== orderId) throw new TenantApiError("invalid");
      // The COD record comes from REST (API-FIN-COD-VISIBILITY-2026-09-29), so it can be reconciled as shown.
      this.update({ financials, transaction: financials.cod_record ?? this.getSnapshot().transaction });
    } catch (error) {
      if (!isLatest()) return;
      this.update({ financials: null });
      this.fail(error, orderId);
    } finally {
      if (isLatest()) this.update({ loadingOrder: null });
    }
  }

  public async refresh(): Promise<void> {
    const orderId = this.getSnapshot().financials?.order_id;
    if (orderId !== undefined) await this.load(orderId);
  }

  public async record(amountText: string, reference: string): Promise<void> {
    const state = this.getSnapshot();
    const financials = state.loadingOrder === null ? state.financials : null;
    if (financials === null || !state.canRecord) return;
    if (!canRecordCollection(financials)) {
      this.update({ errors: ["Esta orden no tiene un cobro contra entrega pendiente de registrar."], message: null });
      return;
    }
    const result = buildRecordCodBody(financials, amountText, reference);
    if (!result.ok) {
      this.update({ errors: result.errors, message: null, stepUpHref: null });
      return;
    }
    const orderId = financials.order_id;
    await this.write(
      `record:${orderId}`,
      JSON.stringify(result.body),
      result.body,
      (api, key, body, signal) => api.record(orderId, body, key, signal),
      (transaction) => transaction.order_id === orderId,
      "Cobro registrado.",
      orderId,
    );
  }

  /** Reconciles the given COD record, or the one the screen received from the API. */
  public async reconcile(codIdText: string | null = null): Promise<void> {
    const state = this.getSnapshot();
    if (!state.canReconcile) return;
    const codId = codIdText ?? state.transaction?.id ?? reconcilableRecord(state.financials)?.id ?? null;
    if (codId === null || !isCanonicalUuid(codId)) {
      this.update({ errors: ["El ID del registro de cobro no es válido."], message: null, stepUpHref: null });
      return;
    }
    await this.write(
      `reconcile:${codId}`,
      "",
      undefined,
      (api, key, _body, signal) => api.reconcile(codId, key, signal),
      (transaction) => transaction.id === codId,
      "Cobro conciliado.",
      state.financials?.order_id ?? null,
    );
  }

  public get activeSession(): OperationsSession | null {
    return this.session;
  }

  private async write<T>(
    scope: string,
    fingerprint: string,
    payload: T,
    action: (api: CodApi, key: string, payload: T, signal?: AbortSignal) => Promise<CodTransaction>,
    matches: (transaction: CodTransaction) => boolean,
    success: string,
    orderId: string | null,
  ): Promise<void> {
    const api = this.api;
    if (api === null || this.getSnapshot().busy) return;
    const generation = this.generation;
    const submission = this.pending.prepare(scope, fingerprint, () => payload);
    this.update({ busy: true, errors: [], message: null, stepUpHref: null });
    let reload = false;
    try {
      const transaction = await action(api, submission.key, submission.payload, this.controller?.signal);
      if (generation !== this.generation) return;
      this.pending.settle(scope);
      if (!matches(transaction)) throw new TenantApiError("invalid");
      this.update({ transaction, message: success, formKey: this.getSnapshot().formKey + 1 });
      reload = true;
    } catch (error) {
      if (generation !== this.generation) return;
      if (!(error instanceof TenantApiError) || !error.retryable) this.pending.settle(scope);
      this.fail(error, orderId);
      reload = error instanceof TenantApiError && error.category === "conflict";
    } finally {
      if (generation === this.generation) this.update({ busy: false });
    }
    // REST stays authoritative: the COD position and the pending list are read again, never inferred.
    const loaded = this.getSnapshot().financials?.order_id;
    if (reload && generation === this.generation) {
      const message = this.getSnapshot().message;
      const stepUpHref = this.getSnapshot().stepUpHref;
      if (loaded !== undefined) await this.load(loaded);
      await this.loadPending();
      if (generation === this.generation && this.getSnapshot().message === null)
        this.update({ message, stepUpHref });
    }
  }

  private fail(error: unknown, orderId: string | null): void {
    const view = describeFailure(error, codReturnUrl(orderId), financeConflictMessages);
    this.update({ message: view.message, stepUpHref: view.stepUpHref });
  }
}
