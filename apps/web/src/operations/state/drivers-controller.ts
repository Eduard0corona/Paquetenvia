import type { DriverDocumentsApi } from "../api/driver-documents-api";
import type { DriversApi } from "../api/drivers-api";
import { TenantApiError } from "../api/tenant-request";
import { canPerform } from "../contracts/capabilities";
import {
  driverDocumentConflictMessages,
  withDocument,
  type DriverDocument,
  type DriverDocumentPage,
  type DriverDocumentSlot,
  type RecordDriverDocumentInput,
} from "../contracts/driver-document";
import {
  sortByReference,
  type OrganizationDriver,
  type OrganizationDriverPage,
} from "../contracts/organization-driver";
import type { OperationsSession } from "../session/operations-session";
import {
  AuthoritativeRefreshCoordinator,
  RefreshScopeChangedError,
} from "./authoritative-refresh-coordinator";
import { ExternalStore } from "./external-store";
import { describeFailure } from "./tenant-error-messages";

/**
 * UI-PHASE3-DRIVERS-2026-10-11 ("Lista de consulta"): state of the dispatcher's read-only "Repartidores" screen.
 *
 * REST is the only source of truth: every read replaces the list with the first page listOrganizationDrivers
 * returns, "Cargar más" appends the next one, and the page becoming visible or focused again only asks for such a
 * read. Drivers are never added or removed here. Everything lives in memory and a session or organization change
 * starts from an empty state.
 *
 * DRV-DOC-001-IN-PERSON-2026-11-02 (AI-07 driver_roster documents): every read of the list also reads the same page
 * of listDriverDocuments (same drivers, same driver_id order, same page size), and "Cargar más" the next one of both.
 * While it answers documents_enabled false (the pilot until GATE-007, Y2) the screen shows no column, button or
 * dialog. Recording and revoking a document replace its slot with the API's own answer; a failed documents read
 * keeps the rows and the documents already shown.
 */
export const driversPath = "/ops/drivers";

export type DriversTrigger = "initial" | "manual" | "visibility" | "focus";

/** Background reads wait while the page is hidden and never repeat a read this recent. */
const backgroundTriggers: ReadonlySet<DriversTrigger> = new Set(["visibility", "focus"]);
export const backgroundRefreshIntervalMilliseconds = 5_000;

export const driversLoadFailedMessage = "No fue posible actualizar la lista de repartidores.";
export const driversPaginationFailedMessage = "No fue posible cargar más repartidores. Actualiza la lista.";
export const documentsLoadFailedMessage = "No fue posible leer los documentos de los repartidores. Actualiza la lista.";
export const documentNotFoundMessage = "Ese repartidor o documento ya no está disponible. Actualiza la lista.";

export interface DriversState {
  readonly phase: "no_session" | "loading" | "ready" | "access_unavailable";
  readonly role: string | null;
  /** The drivers the API returned in this session, by reference. */
  readonly drivers: readonly OrganizationDriver[];
  readonly nextCursor: string | null;
  /** The list was answered at least once (tells an empty fleet from a pending read). */
  readonly loaded: boolean;
  readonly loading: boolean;
  readonly loadingMore: boolean;
  /** The last read failed; the rows already shown stay. */
  readonly error: string | null;
  /** A refusal that only a second factor would lift, with its step-up link. */
  readonly message: string | null;
  readonly stepUpHref: string | null;
  /** When the last read was answered (device clock, shown in Mazatlán time). */
  readonly lastUpdated: string | null;
  /** listDriverDocuments answered documents_enabled true (Y2: false in the pilot until GATE-007). */
  readonly documentsEnabled: boolean;
  /** The required document slots of each listed driver, by driver_id. */
  readonly documents: Readonly<Record<string, readonly DriverDocumentSlot[]>>;
  readonly documentsNextCursor: string | null;
  /** The last documents read failed; what is shown stays. */
  readonly documentsError: string | null;
}

/** The outcome of recording or revoking a document: the API's document, or a message (never server text). */
export type DocumentWriteResult =
  | { readonly ok: true; readonly document: DriverDocument }
  | { readonly ok: false; readonly message: string; readonly retryable: boolean };

export interface DriversDependencies {
  readonly readSession: () => OperationsSession | null;
  readonly createApi: (session: OperationsSession) => DriversApi;
  /** DRV-DOC-001: absent, the screen never reads documents. */
  readonly createDocumentsApi?: (session: OperationsSession) => DriverDocumentsApi;
  readonly loadRole: (session: OperationsSession, signal: AbortSignal) => Promise<string | null>;
  readonly isHidden?: () => boolean;
  readonly now?: () => number;
}

const initialState: DriversState = {
  phase: "no_session",
  role: null,
  drivers: [],
  nextCursor: null,
  loaded: false,
  loading: false,
  loadingMore: false,
  error: null,
  message: null,
  stepUpHref: null,
  lastUpdated: null,
  documentsEnabled: false,
  documents: {},
  documentsNextCursor: null,
  documentsError: null,
};

type DocumentsRead =
  | { readonly kind: "none" }
  | { readonly kind: "page"; readonly page: DriverDocumentPage }
  | { readonly kind: "failed"; readonly error: unknown };

interface ListRead {
  readonly page: OrganizationDriverPage;
  readonly documents: DocumentsRead;
}

function isAccessFailure(error: unknown): boolean {
  return error instanceof TenantApiError && (error.category === "unauthorized" || error.category === "forbidden");
}

export class DriversController extends ExternalStore<DriversState> {
  private api: DriversApi | null = null;
  private documentsApi: DriverDocumentsApi | null = null;
  private generation = 0;
  private controller: AbortController | null = null;
  private coordinator = new AuthoritativeRefreshCoordinator<ListRead>();
  private seenCursors = new Set<string>();
  private seenDocumentCursors = new Set<string>();
  private lastReadAt = Number.NEGATIVE_INFINITY;

  public constructor(private readonly dependencies: DriversDependencies) {
    super(initialState);
  }

  /** Starts (again) for the installed session: drops everything of the previous one, then reads the list. */
  public async start(): Promise<void> {
    this.reset();
    const generation = this.generation;
    const session = this.dependencies.readSession();
    this.api = session === null ? null : this.dependencies.createApi(session);
    this.documentsApi = session === null ? null : (this.dependencies.createDocumentsApi?.(session) ?? null);
    this.update({ ...initialState, phase: session === null ? "no_session" : "loading" });
    if (session === null) return;
    let role: string | null;
    try {
      role = await this.dependencies.loadRole(session, this.controller!.signal);
    } catch {
      role = null;
    }
    if (generation !== this.generation) return;
    if (!canPerform(role, "listOrganizationDrivers")) {
      this.update({ phase: "access_unavailable", role });
      return;
    }
    // Y4: the documents column is read only by the roles that may read it.
    if (!canPerform(role, "listDriverDocuments")) this.documentsApi = null;
    this.update({ phase: "ready", role });
    await this.refresh("initial");
  }

  /** Leaves the screen: cancels every request and forgets the session's data. */
  public stop(): void {
    this.reset();
    this.api = null;
    this.documentsApi = null;
    this.update(initialState);
  }

  /** Reads the first page again and replaces the list with it. */
  public async refresh(trigger: DriversTrigger = "manual"): Promise<void> {
    const api = this.api;
    const state = this.getSnapshot();
    if (api === null || state.phase !== "ready") return;
    if (backgroundTriggers.has(trigger)) {
      if (this.dependencies.isHidden?.() ?? false) return;
      if (state.loading || this.now() - this.lastReadAt < backgroundRefreshIntervalMilliseconds) return;
    }
    const generation = this.generation;
    const coordinator = this.coordinator;
    const documentsApi = this.documentsApi;
    this.update({ loading: true });
    try {
      await coordinator.request({
        requirement: "normal",
        isCurrent: () => generation === this.generation && this.api === api,
        execute: async (signal) => {
          const documents = readDocuments(documentsApi, null, signal);
          const [page, read] = await Promise.all([api.list(null, signal), documents]);
          return { page, documents: read };
        },
        apply: ({ page, documents }) => {
          this.seenCursors = new Set(page.next_cursor === null ? [] : [page.next_cursor]);
          this.update({
            drivers: sortByReference(page.items),
            nextCursor: page.next_cursor,
            loaded: true,
            error: null,
            message: null,
            stepUpHref: null,
            lastUpdated: new Date(this.now()).toISOString(),
          });
          this.applyDocuments(documents, "replace");
        },
      });
    } catch (error: unknown) {
      if (error instanceof RefreshScopeChangedError || generation !== this.generation) return;
      this.fail(error, driversLoadFailedMessage);
    } finally {
      if (generation === this.generation) {
        this.lastReadAt = this.now();
        this.update({ loading: false });
      }
    }
  }

  /** Appends the next page; a cursor the list already followed stops the paging with a message. */
  public async loadMore(): Promise<void> {
    const api = this.api;
    const state = this.getSnapshot();
    if (api === null || state.phase !== "ready" || state.nextCursor === null || state.loadingMore) return;
    const generation = this.generation;
    this.update({ loadingMore: true });
    try {
      const documentsCursor = state.documentsEnabled ? state.documentsNextCursor : null;
      const documents =
        documentsCursor === null
          ? Promise.resolve<DocumentsRead>({ kind: "none" })
          : readDocuments(this.documentsApi, documentsCursor, this.controller?.signal);
      const [page, read] = await Promise.all([api.list(state.nextCursor, this.controller?.signal), documents]);
      if (generation !== this.generation) return;
      this.applyDocuments(read, "append");
      if (generation !== this.generation) return;
      if (page.next_cursor !== null && this.seenCursors.has(page.next_cursor)) {
        this.update({ nextCursor: null, error: driversPaginationFailedMessage });
        return;
      }
      if (page.next_cursor !== null) this.seenCursors.add(page.next_cursor);
      const known = new Set(this.getSnapshot().drivers.map((driver) => driver.driver_id));
      this.update({
        drivers: sortByReference([
          ...this.getSnapshot().drivers,
          ...page.items.filter((driver) => !known.has(driver.driver_id)),
        ]),
        nextCursor: page.next_cursor,
        error: null,
      });
    } catch (error: unknown) {
      if (generation === this.generation) this.fail(error, driversPaginationFailedMessage);
    } finally {
      if (generation === this.generation) this.update({ loadingMore: false });
    }
  }

  /**
   * Records what dispatch reviewed in person (Y1, Y3). One Idempotency-Key per submission; the caller reuses it only to
   * retry the same submission after an answer that applied nothing for sure (network, 503).
   */
  public async recordDocument(
    driverId: string,
    input: RecordDriverDocumentInput,
    idempotencyKey: string,
  ): Promise<DocumentWriteResult> {
    return this.write(driverId, (api, signal) => api.record(driverId, input, idempotencyKey, signal));
  }

  /** Y10: revokes the driver's current VALID document of its type; current stops continue. */
  public async revokeDocument(driverId: string, documentId: string): Promise<DocumentWriteResult> {
    return this.write(driverId, (api, signal) => api.revoke(driverId, documentId, signal));
  }

  private async write(
    driverId: string,
    send: (api: DriverDocumentsApi, signal: AbortSignal | undefined) => Promise<DriverDocument>,
  ): Promise<DocumentWriteResult> {
    const api = this.documentsApi;
    const state = this.getSnapshot();
    const unavailable = { ok: false, message: documentNotFoundMessage, retryable: false } as const;
    if (api === null || state.phase !== "ready" || !state.documentsEnabled) return unavailable;
    const generation = this.generation;
    try {
      const document = await send(api, this.controller?.signal);
      if (generation !== this.generation) return unavailable;
      const slots = this.getSnapshot().documents[driverId];
      if (slots === undefined) return unavailable;
      this.update({ documents: { ...this.getSnapshot().documents, [driverId]: withDocument(slots, document) } });
      return { ok: true, document };
    } catch (error: unknown) {
      if (generation !== this.generation) return unavailable;
      if (isAccessFailure(error)) {
        const view = describeFailure(error, driversPath);
        this.fail(error, driversLoadFailedMessage);
        return { ok: false, message: view.message, retryable: false };
      }
      if (error instanceof TenantApiError && error.category === "not_found") return unavailable;
      const view = describeFailure(error, driversPath, driverDocumentConflictMessages);
      return {
        ok: false,
        message: view.message,
        retryable: error instanceof TenantApiError && error.retryable,
      };
    }
  }

  /** Applies a documents read; a refused one is handled like the list's, any other failure keeps what is shown. */
  private applyDocuments(read: DocumentsRead, mode: "replace" | "append"): void {
    if (read.kind === "none") return;
    if (read.kind === "failed") {
      if (isAccessFailure(read.error)) {
        this.fail(read.error, driversLoadFailedMessage);
        return;
      }
      this.update({ documentsError: documentsLoadFailedMessage });
      return;
    }
    const page = read.page;
    if (!page.documents_enabled) {
      this.seenDocumentCursors = new Set();
      this.update({ documentsEnabled: false, documents: {}, documentsNextCursor: null, documentsError: null });
      return;
    }
    if (mode === "append" && page.next_cursor !== null && this.seenDocumentCursors.has(page.next_cursor)) {
      this.update({ documentsNextCursor: null, documentsError: documentsLoadFailedMessage });
      return;
    }
    if (mode === "replace") this.seenDocumentCursors = new Set();
    if (page.next_cursor !== null) this.seenDocumentCursors.add(page.next_cursor);
    const documents: Record<string, readonly DriverDocumentSlot[]> =
      mode === "replace" ? {} : { ...this.getSnapshot().documents };
    for (const item of page.items) documents[item.driver_id] = item.documents;
    this.update({
      documentsEnabled: true,
      documents,
      documentsNextCursor: page.next_cursor,
      documentsError: null,
    });
  }

  private fail(error: unknown, readFailed: string): void {
    if (error instanceof TenantApiError && (error.category === "unauthorized" || error.category === "forbidden")) {
      if (error.mfaRequired) {
        // Only the second factor is missing: nothing is shown until it is verified.
        const view = describeFailure(error, driversPath);
        this.update({ drivers: [], nextCursor: null, error: null, message: view.message, stepUpHref: view.stepUpHref });
        return;
      }
      // Lost access clears everything, as on the other operations lists.
      this.reset();
      this.api = null;
      this.documentsApi = null;
      this.update({ ...initialState, phase: "access_unavailable", role: this.getSnapshot().role });
      return;
    }
    this.update({ error: readFailed });
  }

  private reset(): void {
    this.generation += 1;
    this.controller?.abort();
    this.controller = new AbortController();
    this.coordinator.cancel();
    this.coordinator = new AuthoritativeRefreshCoordinator<ListRead>();
    this.seenCursors = new Set();
    this.seenDocumentCursors = new Set();
    this.lastReadAt = Number.NEGATIVE_INFINITY;
  }

  private now(): number {
    return this.dependencies.now?.() ?? Date.now();
  }
}

/** One documents read that never rejects: the list's own read decides whether the whole refresh failed. */
async function readDocuments(
  api: DriverDocumentsApi | null,
  cursor: string | null,
  signal: AbortSignal | undefined,
): Promise<DocumentsRead> {
  if (api === null) return { kind: "none" };
  try {
    return { kind: "page", page: await api.list(cursor, signal) };
  } catch (error: unknown) {
    return { kind: "failed", error };
  }
}
