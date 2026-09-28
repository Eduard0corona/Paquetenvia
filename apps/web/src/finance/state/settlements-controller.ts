import { TenantApiError } from "../../operations/api/tenant-request";
import {
  canPerform,
  requiresMfa,
  type CapabilityOperation,
} from "../../operations/contracts/capabilities";
import { parseMxnToCents } from "../../operations/contracts/money";
import type { OperationsSession } from "../../operations/session/operations-session";
import {
  AuthoritativeRefreshCoordinator,
  RefreshScopeChangedError,
} from "../../operations/state/authoritative-refresh-coordinator";
import { ExternalStore } from "../../operations/state/external-store";
import { PendingSubmissions } from "../../operations/state/pending-submissions";
import { describeFailure } from "../../operations/state/tenant-error-messages";
import type { SettlementCsv, SettlementsApi } from "../api/settlements-api";
import {
  actionsForStatus,
  buildSettlementSearch,
  isCanonicalUuid,
  isValidReason,
  settlementConflictMessages,
  validateCreateSettlement,
  type CreateSettlementBody,
  type Settlement,
  type SettlementAction,
  type SettlementFilters,
  type SettlementPage,
  type SettlementStatus,
} from "../contracts/settlement";

export const settlementsPath = "/finance/settlements";

const actionOperations: Readonly<Record<SettlementAction, CapabilityOperation>> = {
  adjust: "addSettlementAdjustment",
  approve: "approveSettlement",
  pay: "markSettlementPaid",
  void: "voidSettlement",
  export: "exportSettlementCsv",
};

/** D5-CAPABILITY-MATRIX ∩ the settlement state machine; the API still decides. */
export function visibleSettlementActions(
  role: string | null,
  status: SettlementStatus,
): readonly SettlementAction[] {
  return actionsForStatus(status).filter((action) => canPerform(role, actionOperations[action]));
}

/** D7-SETTLEMENT-MFA and the PLATFORM_ADMIN MFA rule, for the explanatory hint only. */
export function actionNeedsMfa(role: string | null, action: SettlementAction): boolean {
  return requiresMfa(role, actionOperations[action]);
}

export function settlementReturnUrl(settlementId: string | null): string {
  return settlementId !== null && isCanonicalUuid(settlementId)
    ? `${settlementsPath}?settlement=${settlementId}`
    : settlementsPath;
}

export interface SettlementsState {
  readonly phase: "no_session" | "loading" | "ready" | "access_unavailable";
  readonly role: string | null;
  readonly canCreate: boolean;
  readonly filters: SettlementFilters;
  readonly items: readonly Settlement[];
  readonly nextCursor: string | null;
  readonly selected: Settlement | null;
  readonly loading: boolean;
  readonly busy: boolean;
  readonly errors: readonly string[];
  readonly message: string | null;
  readonly stepUpHref: string | null;
  /** Changes on tenant switch so forms holding typed data remount empty. */
  readonly formKey: number;
}

export interface SettlementsDependencies {
  readonly readSession: () => OperationsSession | null;
  readonly createApi: (session: OperationsSession) => SettlementsApi;
  readonly loadRole: (session: OperationsSession, signal: AbortSignal) => Promise<string | null>;
  readonly download: (csv: SettlementCsv) => void;
  /** Settlement to reopen after returning from the MFA step-up. */
  readonly initialSelection?: () => string | null;
}

const initialState: SettlementsState = {
  phase: "no_session",
  role: null,
  canCreate: false,
  filters: {},
  items: [],
  nextCursor: null,
  selected: null,
  loading: false,
  busy: false,
  errors: [],
  message: null,
  stepUpHref: null,
  formKey: 0,
};

/**
 * /finance/settlements. REST is authoritative on load, refresh and after every write:
 * the list and the selected settlement are replaced by what the API returns, and a
 * conflict or missing settlement forces a refetch. A tenant switch clears all of it.
 */
export class SettlementsController extends ExternalStore<SettlementsState> {
  private api: SettlementsApi | null = null;
  private session: OperationsSession | null = null;
  private generation = 0;
  private controller: AbortController | null = null;
  private readonly refreshes = new AuthoritativeRefreshCoordinator<SettlementPage>();
  private readonly pending: PendingSubmissions;

  public constructor(
    private readonly dependencies: SettlementsDependencies,
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
    this.refreshes.cancel();
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
    if (!canPerform(role, "listSettlements")) {
      this.update({ phase: "access_unavailable", role });
      return;
    }
    this.update({ phase: "ready", role, canCreate: canPerform(role, "createSettlement") });
    await this.refresh();
    const initial = this.dependencies.initialSelection?.() ?? null;
    if (generation === this.generation && initial !== null && isCanonicalUuid(initial)) {
      await this.select(initial);
    }
  }

  public stop(): void {
    this.generation += 1;
    this.controller?.abort();
    this.refreshes.cancel();
    this.pending.clear();
  }

  public async applyFilters(filters: SettlementFilters): Promise<void> {
    if (buildSettlementSearch(filters) === null) {
      this.update({
        errors: ["Revisa los filtros: repartidor UUID, estado válido y un periodo con fin posterior al inicio."],
      });
      return;
    }
    this.update({ filters, errors: [] });
    await this.refresh();
  }

  /** Fetches the first page again (load, refresh and after every write). */
  public async refresh(): Promise<void> {
    const api = this.api;
    if (api === null || this.getSnapshot().phase !== "ready") return;
    const generation = this.generation;
    const filters = this.getSnapshot().filters;
    this.update({ loading: true });
    try {
      await this.refreshes.request({
        requirement: "normal",
        isCurrent: () => generation === this.generation && this.api === api,
        execute: (signal) => api.list(filters, signal),
        apply: (page) => this.update({ items: page.items, nextCursor: page.next_cursor }),
      });
    } catch (error) {
      if (error instanceof RefreshScopeChangedError || generation !== this.generation) return;
      this.fail(error);
    } finally {
      if (generation === this.generation) this.update({ loading: false });
    }
  }

  public async loadMore(): Promise<void> {
    const api = this.api;
    const state = this.getSnapshot();
    if (api === null || state.nextCursor === null || state.loading) return;
    const generation = this.generation;
    this.update({ loading: true });
    try {
      const page = await api.list({ ...state.filters, cursor: state.nextCursor }, this.controller?.signal);
      if (generation !== this.generation) return;
      const known = new Set(this.getSnapshot().items.map((item) => item.id));
      this.update({
        items: [...this.getSnapshot().items, ...page.items.filter((item) => !known.has(item.id))],
        nextCursor: page.next_cursor,
      });
    } catch (error) {
      if (generation === this.generation) this.fail(error);
    } finally {
      if (generation === this.generation) this.update({ loading: false });
    }
  }

  public async select(settlementId: string): Promise<void> {
    const api = this.api;
    if (api === null) return;
    const generation = this.generation;
    this.update({ loading: true, errors: [], message: null, stepUpHref: null });
    try {
      const settlement = await api.get(settlementId, this.controller?.signal);
      if (generation !== this.generation) return;
      this.update({ selected: settlement });
    } catch (error) {
      if (generation !== this.generation) return;
      if (error instanceof TenantApiError && error.category === "not_found") this.update({ selected: null });
      this.fail(error, settlementId);
    } finally {
      if (generation === this.generation) this.update({ loading: false });
    }
  }

  public clearSelection(): void {
    this.update({ selected: null, errors: [], message: null, stepUpHref: null });
  }

  public async create(body: CreateSettlementBody): Promise<void> {
    if (!this.getSnapshot().canCreate) return;
    const errors = validateCreateSettlement(body);
    if (errors.length > 0) {
      this.update({ errors, message: null });
      return;
    }
    await this.write("create", JSON.stringify(body), body, (api, key, payload, signal) =>
      api.create(payload, key, signal), null);
  }

  public async addAdjustment(amountText: string, reason: string): Promise<void> {
    const selected = this.getSnapshot().selected;
    if (selected === null) return;
    const errors: string[] = [];
    const amount = parseMxnToCents(amountText, { allowNegative: true, allowZero: false });
    if (amount === null)
      errors.push("El ajuste debe ser un monto en MXN distinto de cero, con signo opcional y hasta 2 decimales.");
    if (!isValidReason(reason))
      errors.push("El motivo es obligatorio (máximo 500 caracteres) y no puede iniciar ni terminar con espacios.");
    if (errors.length > 0) {
      this.update({ errors, message: null });
      return;
    }
    const body = { amount_cents: amount!, reason };
    await this.write(`adjust:${selected.id}`, JSON.stringify(body), body, (api, key, payload, signal) =>
      api.addAdjustment(selected.id, payload, key, signal), selected.id);
  }

  public async approve(): Promise<void> {
    const selected = this.getSnapshot().selected;
    if (selected === null) return;
    await this.write(`approve:${selected.id}`, "", undefined, (api, key, _payload, signal) =>
      api.approve(selected.id, key, signal), selected.id);
  }

  public async markPaid(): Promise<void> {
    const selected = this.getSnapshot().selected;
    if (selected === null) return;
    await this.write(`pay:${selected.id}`, "", undefined, (api, key, _payload, signal) =>
      api.markPaid(selected.id, key, signal), selected.id);
  }

  public async voidSettlement(reason: string): Promise<void> {
    const selected = this.getSnapshot().selected;
    if (selected === null) return;
    if (!isValidReason(reason)) {
      this.update({
        errors: ["El motivo es obligatorio (máximo 500 caracteres) y no puede iniciar ni terminar con espacios."],
        message: null,
      });
      return;
    }
    const body = { reason };
    await this.write(`void:${selected.id}`, JSON.stringify(body), body, (api, key, payload, signal) =>
      api.void(selected.id, payload, key, signal), selected.id);
  }

  public async exportCsv(): Promise<void> {
    const api = this.api;
    const selected = this.getSnapshot().selected;
    if (api === null || selected === null || this.getSnapshot().busy) return;
    const generation = this.generation;
    this.update({ busy: true, errors: [], message: null, stepUpHref: null });
    try {
      const csv = await api.exportCsv(selected.id, this.controller?.signal);
      if (generation !== this.generation) return;
      this.dependencies.download(csv);
      this.update({ message: "Exportación generada; el servidor la registra en auditoría." });
    } catch (error) {
      if (generation === this.generation) this.fail(error, selected.id);
    } finally {
      if (generation === this.generation) this.update({ busy: false });
    }
  }

  public get activeSession(): OperationsSession | null {
    return this.session;
  }

  private async write<T>(
    scope: string,
    fingerprint: string,
    payload: T,
    action: (api: SettlementsApi, key: string, payload: T, signal?: AbortSignal) => Promise<Settlement>,
    settlementId: string | null,
  ): Promise<void> {
    const api = this.api;
    if (api === null || this.getSnapshot().busy) return;
    const generation = this.generation;
    const submission = this.pending.prepare(scope, fingerprint, () => payload);
    this.update({ busy: true, errors: [], message: null, stepUpHref: null });
    try {
      const settlement = await action(api, submission.key, submission.payload, this.controller?.signal);
      if (generation !== this.generation) return;
      this.pending.settle(scope);
      this.update({ selected: settlement, message: "Cambio confirmado por el servidor.", formKey: this.getSnapshot().formKey + 1 });
    } catch (error) {
      if (generation !== this.generation) return;
      if (!(error instanceof TenantApiError) || !error.retryable) this.pending.settle(scope);
      this.fail(error, settlementId);
      // A conflict or a missing settlement discards local assumptions: reload from REST.
      if (error instanceof TenantApiError && settlementId !== null) {
        if (error.category === "conflict") await this.reloadSelected(settlementId, generation);
        if (error.category === "not_found") this.update({ selected: null });
      }
    } finally {
      if (generation === this.generation) this.update({ busy: false });
    }
    await this.refresh();
  }

  private async reloadSelected(settlementId: string, generation: number): Promise<void> {
    const api = this.api;
    if (api === null) return;
    try {
      const settlement = await api.get(settlementId, this.controller?.signal);
      if (generation === this.generation) this.update({ selected: settlement });
    } catch (error) {
      if (generation === this.generation && error instanceof TenantApiError && error.category === "not_found")
        this.update({ selected: null });
    }
  }

  private fail(error: unknown, settlementId: string | null = null): void {
    const view = describeFailure(
      error,
      settlementReturnUrl(settlementId),
      settlementConflictMessages,
    );
    this.update({ message: view.message, stepUpHref: view.stepUpHref });
  }
}
