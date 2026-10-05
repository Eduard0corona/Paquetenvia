import type { CsvImportApi } from "../api/csv-import-api";
import { TenantApiError } from "../api/tenant-request";
import { canPerform } from "../contracts/capabilities";
import {
  checkCsvFile,
  csvConflictMessages,
  csvFileProblemLabels,
  csvRestrictedGoodsRequiredMessage,
  isCommittable,
  type CsvImportCommit,
  type CsvImportPreview,
} from "../contracts/csv-import";
import type { OperationsSession } from "../session/operations-session";
import { ExternalStore } from "./external-store";
import { PendingSubmissions } from "./pending-submissions";
import { describeFailure } from "./tenant-error-messages";

export const csvImportPath = "/ops/orders/import";

export interface CsvImportState {
  readonly phase: "no_session" | "loading" | "ready" | "access_unavailable";
  readonly role: string | null;
  readonly canCommit: boolean;
  /** Size in bytes of the file held in memory; its name is never kept. */
  readonly fileBytes: number | null;
  readonly preview: CsvImportPreview | null;
  readonly commit: CsvImportCommit | null;
  readonly busy: boolean;
  readonly errors: readonly string[];
  readonly message: string | null;
  readonly stepUpHref: string | null;
  /** Changes on tenant switch and on reset so the file input remounts empty. */
  readonly formKey: number;
}

export interface CsvImportDependencies {
  readonly readSession: () => OperationsSession | null;
  readonly createApi: (session: OperationsSession) => CsvImportApi;
  readonly loadRole: (session: OperationsSession, signal: AbortSignal) => Promise<string | null>;
}

const initialState: CsvImportState = {
  phase: "no_session",
  role: null,
  canCommit: false,
  fileBytes: null,
  preview: null,
  commit: null,
  busy: false,
  errors: [],
  message: null,
  stepUpHref: null,
  formKey: 0,
};

const commitScope = "csv-commit";

/**
 * CSV-001 upload → server prevalidation → confirmation. The selected bytes are copied
 * into memory once, so the commit sends exactly what was previewed. The report and
 * outcomes are shown only as the API returned them. A tenant switch drops the file,
 * the report and the batch Idempotency-Key.
 */
export class CsvImportController extends ExternalStore<CsvImportState> {
  private api: CsvImportApi | null = null;
  private session: OperationsSession | null = null;
  private file: Blob | null = null;
  private generation = 0;
  private controller: AbortController | null = null;
  private readonly pending: PendingSubmissions;

  public constructor(
    private readonly dependencies: CsvImportDependencies,
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
    this.file = null;
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
    const canPreview = canPerform(role, "previewOrderCsv");
    this.update({
      phase: canPreview ? "ready" : "access_unavailable",
      role,
      canCommit: canPerform(role, "commitOrderCsv"),
    });
  }

  public stop(): void {
    this.generation += 1;
    this.controller?.abort();
    this.pending.clear();
    this.file = null;
  }

  /** Copies the chosen file into memory; a new file discards the previous report and key. */
  public async selectFile(file: Blob | null): Promise<void> {
    if (this.getSnapshot().phase !== "ready" || this.getSnapshot().busy) return;
    const generation = this.generation;
    this.pending.settle(commitScope);
    this.file = null;
    this.update({ fileBytes: null, preview: null, commit: null, errors: [], message: null, stepUpHref: null });
    if (file === null) return;
    const problem = checkCsvFile(file.size);
    if (problem !== null) {
      this.update({ errors: [csvFileProblemLabels[problem]] });
      return;
    }
    let copy: Blob;
    try {
      copy = new Blob([await file.arrayBuffer()]);
    } catch {
      if (generation === this.generation) this.update({ errors: ["No fue posible leer el archivo seleccionado."] });
      return;
    }
    if (generation !== this.generation) return;
    this.file = copy;
    this.update({ fileBytes: copy.size });
  }

  public async preview(): Promise<void> {
    const api = this.api;
    const file = this.file;
    if (api === null || file === null || this.getSnapshot().busy) return;
    const generation = this.generation;
    this.pending.settle(commitScope);
    this.update({ busy: true, preview: null, commit: null, errors: [], message: null, stepUpHref: null });
    try {
      const preview = await api.preview(file, this.controller?.signal);
      if (generation !== this.generation) return;
      this.update({
        preview,
        message: isCommittable(preview)
          ? "Prevalidación completa: ninguna orden se ha creado todavía."
          : "El archivo tiene errores; corrígelo y vuelve a previsualizar. Ninguna orden se creó.",
      });
    } catch (error) {
      if (generation !== this.generation) return;
      this.fail(error);
    } finally {
      if (generation === this.generation) this.update({ busy: false });
    }
  }

  /**
   * @param restrictedGoodsAcknowledged ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02: the dispatcher ticked that no
   * shipment in the file contains prohibited goods; without it nothing is sent.
   */
  public async commit(restrictedGoodsAcknowledged: boolean): Promise<void> {
    const api = this.api;
    const file = this.file;
    const state = this.getSnapshot();
    const preview = state.preview;
    if (api === null || file === null || preview === null || !state.canCommit || state.busy) return;
    if (!isCommittable(preview)) return;
    if (!restrictedGoodsAcknowledged) {
      this.update({ errors: [csvRestrictedGoodsRequiredMessage], message: null, stepUpHref: null });
      return;
    }
    const generation = this.generation;
    // The batch key follows the previewed digest: a retry of the same batch reuses it.
    const submission = this.pending.prepare(commitScope, preview.content_digest, () => preview.content_digest);
    this.update({ busy: true, errors: [], message: null, stepUpHref: null });
    try {
      const result = await api.commit(file, submission.payload, submission.key, this.controller?.signal);
      if (generation !== this.generation) return;
      this.pending.settle(commitScope);
      if (result.kind === "committed") {
        this.update({
          commit: result.commit,
          message: `Lote confirmado: ${result.commit.created_rows} creada(s), ${result.commit.failed_rows} con error.`,
        });
      } else {
        this.update({
          preview: result.preview,
          message: "El servidor rechazó el lote porque alguna fila no pasó la prevalidación. Ninguna orden se creó.",
        });
      }
    } catch (error) {
      if (generation !== this.generation) return;
      if (!(error instanceof TenantApiError) || !error.retryable) this.pending.settle(commitScope);
      this.fail(error);
    } finally {
      if (generation === this.generation) this.update({ busy: false });
    }
  }

  /** Starts a new import in the same tenant. */
  public reset(): void {
    this.pending.clear();
    this.file = null;
    this.update({
      fileBytes: null,
      preview: null,
      commit: null,
      errors: [],
      message: null,
      stepUpHref: null,
      formKey: this.getSnapshot().formKey + 1,
    });
  }

  public get activeSession(): OperationsSession | null {
    return this.session;
  }

  private fail(error: unknown): void {
    const view = describeFailure(error, csvImportPath, csvConflictMessages);
    this.update({ message: view.message, stepUpHref: view.stepUpHref });
  }
}
