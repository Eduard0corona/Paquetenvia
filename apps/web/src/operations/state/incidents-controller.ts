import type { IncidentsApi } from "../api/incidents-api";
import { TenantApiError } from "../api/tenant-request";
import { canPerform, requiresMfa } from "../contracts/capabilities";
import {
  buildOpenIncidentBody,
  buildResolveIncidentBody,
  incidentConflictMessages,
  incidentStatuses,
  type Incident,
  type IncidentListFilter,
  type IncidentStatus,
  type OpenIncidentDraft,
  type Proof,
} from "../contracts/incident";
import { isCanonicalUuid } from "../contracts/strict-json";
import type { OperationsSession } from "../session/operations-session";
import { ExternalStore } from "./external-store";
import { PendingSubmissions } from "./pending-submissions";
import { describeFailure } from "./tenant-error-messages";

export const incidentsPath = "/ops/incidents";

export interface IncidentsState {
  readonly phase: "no_session" | "loading" | "ready" | "access_unavailable";
  readonly role: string | null;
  readonly canOpen: boolean;
  readonly canResolve: boolean;
  /** listIncidents (API-INC-LIST-PROOFS-2026-09-29). */
  readonly canList: boolean;
  /** listOrderProofs (API-INC-LIST-PROOFS-2026-09-29). */
  readonly canListProofs: boolean;
  readonly mfaHint: string | null;
  /** Incidents the API returned in this tenant session (listed, opened or resolved), newest first. */
  readonly incidents: readonly Incident[];
  /** The listIncidents status filter in effect; null lists every status. */
  readonly statusFilter: IncidentStatus | null;
  /** The listIncidents order filter in effect (a canonical order id); null lists every order. */
  readonly orderFilter: string | null;
  /** next_cursor of the last listed page; null when every page was loaded. */
  readonly nextCursor: string | null;
  readonly listing: boolean;
  /** The order whose proofs are shown for evidence selection. */
  readonly proofsOrderId: string | null;
  readonly proofs: readonly Proof[];
  readonly proofsCursor: string | null;
  readonly busy: boolean;
  readonly errors: readonly string[];
  readonly message: string | null;
  readonly stepUpHref: string | null;
  /** Changes on tenant switch and after a confirmed write so typed text is dropped. */
  readonly formKey: number;
}

export interface IncidentsDependencies {
  readonly readSession: () => OperationsSession | null;
  readonly createApi: (session: OperationsSession) => IncidentsApi;
  readonly loadRole: (session: OperationsSession, signal: AbortSignal) => Promise<string | null>;
  readonly now?: () => Date;
}

const initialState: IncidentsState = {
  phase: "no_session",
  role: null,
  canOpen: false,
  canResolve: false,
  canList: false,
  canListProofs: false,
  mfaHint: null,
  incidents: [],
  statusFilter: null,
  orderFilter: null,
  nextCursor: null,
  listing: false,
  proofsOrderId: null,
  proofs: [],
  proofsCursor: null,
  busy: false,
  errors: [],
  message: null,
  stepUpHref: null,
  formKey: 0,
};

const invalidOrderMessage = "El ID de la orden no es válido.";

/** The listIncidents filters in effect; an absent one lists every value. */
function listFilter(status: IncidentStatus | null, orderId: string | null): IncidentListFilter {
  return { ...(status === null ? {} : { status }), ...(orderId === null ? {} : { orderId }) };
}

/**
 * INC-001 open and resolve, with the incidents and order proofs listed by the API
 * (API-INC-LIST-PROOFS-2026-09-29) so ids are picked, not typed. Every incident and
 * proof shown is exactly what the API returned; nothing is inferred locally. A tenant
 * switch drops the incidents, the list filters, proofs, typed text and pending
 * Idempotency-Keys.
 */
export class IncidentsController extends ExternalStore<IncidentsState> {
  private api: IncidentsApi | null = null;
  private session: OperationsSession | null = null;
  private generation = 0;
  private controller: AbortController | null = null;
  private readonly pending: PendingSubmissions;

  public constructor(
    private readonly dependencies: IncidentsDependencies,
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
    const canOpen = canPerform(role, "openIncident");
    const canResolve = canPerform(role, "resolveIncident");
    const canList = canPerform(role, "listIncidents");
    this.update({
      phase: canOpen || canResolve ? "ready" : "access_unavailable",
      role,
      canOpen,
      canResolve,
      canList,
      canListProofs: canPerform(role, "listOrderProofs"),
      mfaHint:
        requiresMfa(role, "openIncident") || requiresMfa(role, "resolveIncident")
          ? "Abrir y resolver incidencias con tu rol requiere verificar tu identidad (MFA)."
          : null,
    });
    if (canList) await this.refresh(null, null);
  }

  /**
   * Loads the first listIncidents page for `status` (null: every status) and `orderId` (null: every order), replacing
   * the list (AI-07 incident_desk: "filterable by status and order"). An order id that is not a canonical UUID is
   * refused before any request.
   */
  public async refresh(
    status: IncidentStatus | null = this.getSnapshot().statusFilter,
    orderId: string | null = this.getSnapshot().orderFilter,
  ): Promise<void> {
    if (!this.getSnapshot().canList) return;
    if (status !== null && !(incidentStatuses as readonly string[]).includes(status)) return;
    if (orderId !== null && !isCanonicalUuid(orderId)) {
      this.update({ errors: [invalidOrderMessage], message: null, stepUpHref: null });
      return;
    }
    await this.read(
      () => this.api!.list(listFilter(status, orderId), null, this.controller?.signal),
      (page) => ({ incidents: page.items, nextCursor: page.next_cursor, statusFilter: status, orderFilter: orderId }),
      { statusFilter: status, orderFilter: orderId },
    );
  }

  /** Appends the next listIncidents page for the filters in effect, keeping any incident already shown. */
  public async loadMore(): Promise<void> {
    const { canList, nextCursor, statusFilter, orderFilter } = this.getSnapshot();
    if (!canList || nextCursor === null) return;
    await this.read(
      () => this.api!.list(listFilter(statusFilter, orderFilter), nextCursor, this.controller?.signal),
      (page) => {
        const known = new Set(this.getSnapshot().incidents.map((incident) => incident.id));
        return {
          incidents: [...this.getSnapshot().incidents, ...page.items.filter((item) => !known.has(item.id))],
          nextCursor: page.next_cursor,
        };
      },
    );
  }

  /** Lists the proofs of `orderId` so the evidence is picked from what the API returned. */
  public async loadProofs(orderId: string): Promise<void> {
    if (!this.getSnapshot().canListProofs) return;
    if (!isCanonicalUuid(orderId)) {
      this.update({ errors: [invalidOrderMessage], message: null, stepUpHref: null });
      return;
    }
    await this.read(
      () => this.api!.listOrderProofs(orderId, null, this.controller?.signal),
      (page) => ({ proofsOrderId: orderId, proofs: page.items, proofsCursor: page.next_cursor }),
      { proofsOrderId: null, proofs: [], proofsCursor: null },
    );
  }

  public async loadMoreProofs(): Promise<void> {
    const { canListProofs, proofsOrderId, proofsCursor } = this.getSnapshot();
    if (!canListProofs || proofsOrderId === null || proofsCursor === null) return;
    await this.read(
      () => this.api!.listOrderProofs(proofsOrderId, proofsCursor, this.controller?.signal),
      (page) => {
        if (this.getSnapshot().proofsOrderId !== proofsOrderId) return {};
        const known = new Set(this.getSnapshot().proofs.map((proof) => proof.id));
        return {
          proofs: [...this.getSnapshot().proofs, ...page.items.filter((item) => !known.has(item.id))],
          proofsCursor: page.next_cursor,
        };
      },
    );
  }

  public stop(): void {
    this.generation += 1;
    this.controller?.abort();
    this.pending.clear();
  }

  public async open(draft: OpenIncidentDraft): Promise<void> {
    if (!this.getSnapshot().canOpen) return;
    const result = buildOpenIncidentBody(draft, this.dependencies.now?.() ?? new Date());
    if (!result.ok) {
      this.update({ errors: result.errors, message: null, stepUpHref: null });
      return;
    }
    const orderId = draft.orderId;
    await this.write(
      `open:${orderId}`,
      JSON.stringify(result.body),
      result.body,
      (api, key, body, signal) => api.open(orderId, body, key, signal),
      (incident) => incident.order_id === orderId,
      "Incidencia abierta y registrada.",
      true,
    );
  }

  public async resolve(incidentId: string, outcome: string, reason: string): Promise<void> {
    if (!this.getSnapshot().canResolve) return;
    const result = buildResolveIncidentBody(incidentId, outcome, reason);
    if (!result.ok) {
      this.update({ errors: result.errors, message: null, stepUpHref: null });
      return;
    }
    await this.write(
      `resolve:${incidentId}`,
      JSON.stringify(result.body),
      result.body,
      (api, key, body, signal) => api.resolve(incidentId, body, key, signal),
      (incident) => incident.id === incidentId && (incident.status === "RESOLVED" || incident.status === "REJECTED"),
      "Resolución registrada.",
      false,
    );
  }

  public get activeSession(): OperationsSession | null {
    return this.session;
  }

  /** One read at a time; a result that arrives after a tenant switch is dropped. */
  private async read<T>(
    request: () => Promise<T>,
    apply: (result: T) => Partial<IncidentsState>,
    before: Partial<IncidentsState> = {},
  ): Promise<void> {
    if (this.api === null || this.getSnapshot().listing) return;
    const generation = this.generation;
    this.update({ ...before, listing: true, errors: [], message: null, stepUpHref: null });
    try {
      const result = await request();
      if (generation !== this.generation) return;
      this.update(apply(result));
    } catch (error) {
      if (generation !== this.generation) return;
      const view = describeFailure(error, incidentsPath, incidentConflictMessages);
      this.update({ message: view.message, stepUpHref: view.stepUpHref });
    } finally {
      if (generation === this.generation) this.update({ listing: false });
    }
  }

  private async write<T>(
    scope: string,
    fingerprint: string,
    payload: T,
    action: (api: IncidentsApi, key: string, payload: T, signal?: AbortSignal) => Promise<Incident>,
    matches: (incident: Incident) => boolean,
    success: string,
    /** openIncident: an incident not listed yet is the newest, so it goes first. */
    opened: boolean,
  ): Promise<void> {
    const api = this.api;
    if (api === null || this.getSnapshot().busy) return;
    const generation = this.generation;
    const submission = this.pending.prepare(scope, fingerprint, () => payload);
    this.update({ busy: true, errors: [], message: null, stepUpHref: null });
    try {
      const incident = await action(api, submission.key, submission.payload, this.controller?.signal);
      if (generation !== this.generation) return;
      this.pending.settle(scope);
      // A response for another order or incident fails closed.
      if (!matches(incident)) throw new TenantApiError("invalid");
      this.update({
        incidents: placeReturned(this.getSnapshot().incidents, incident, opened),
        message: success,
        formKey: this.getSnapshot().formKey + 1,
        // The evidence picked for this opening is not offered again for the next one.
        proofsOrderId: null,
        proofs: [],
        proofsCursor: null,
      });
    } catch (error) {
      if (generation !== this.generation) return;
      if (!(error instanceof TenantApiError) || !error.retryable) this.pending.settle(scope);
      const view = describeFailure(error, incidentsPath, incidentConflictMessages);
      this.update({ message: view.message, stepUpHref: view.stepUpHref });
    } finally {
      if (generation === this.generation) this.update({ busy: false });
    }
  }
}

/**
 * AI-07 incident_desk: an Incident returned by openIncident or resolveIncident replaces the listed one with the same
 * id where it is, so the list stays newest first; only a newly opened incident, not listed yet, goes first.
 */
function placeReturned(incidents: readonly Incident[], incident: Incident, opened: boolean): readonly Incident[] {
  if (incidents.some((item) => item.id === incident.id)) {
    return incidents.map((item) => (item.id === incident.id ? incident : item));
  }
  return opened ? [incident, ...incidents] : incidents;
}
