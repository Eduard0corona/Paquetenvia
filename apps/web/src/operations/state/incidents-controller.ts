import type { IncidentsApi } from "../api/incidents-api";
import { TenantApiError } from "../api/tenant-request";
import { canPerform, requiresMfa } from "../contracts/capabilities";
import {
  buildOpenIncidentBody,
  buildResolveIncidentBody,
  incidentConflictMessages,
  type Incident,
  type OpenIncidentDraft,
} from "../contracts/incident";
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
  readonly mfaHint: string | null;
  /** Incidents the API returned in this tenant session, newest first. */
  readonly incidents: readonly Incident[];
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
  mfaHint: null,
  incidents: [],
  busy: false,
  errors: [],
  message: null,
  stepUpHref: null,
  formKey: 0,
};

/**
 * INC-001 open and resolve. Every incident shown is exactly what the API returned;
 * nothing is inferred locally. A tenant switch drops the incidents, typed text and
 * pending Idempotency-Keys.
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
    this.update({
      phase: canOpen || canResolve ? "ready" : "access_unavailable",
      role,
      canOpen,
      canResolve,
      mfaHint:
        requiresMfa(role, "openIncident") || requiresMfa(role, "resolveIncident")
          ? "Abrir y resolver incidencias con tu rol requiere verificar tu identidad (MFA)."
          : null,
    });
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
      "Incidencia abierta y registrada por el servidor.",
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
      "Resolución registrada por el servidor.",
    );
  }

  public get activeSession(): OperationsSession | null {
    return this.session;
  }

  private async write<T>(
    scope: string,
    fingerprint: string,
    payload: T,
    action: (api: IncidentsApi, key: string, payload: T, signal?: AbortSignal) => Promise<Incident>,
    matches: (incident: Incident) => boolean,
    success: string,
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
        incidents: [incident, ...this.getSnapshot().incidents.filter((item) => item.id !== incident.id)],
        message: success,
        formKey: this.getSnapshot().formKey + 1,
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
