import { DriverDocumentsApiError, type DriverDocumentsApi } from "../api/driver-documents-api";
import type { DriverDocumentSlot } from "../../operations/contracts/driver-document";

/**
 * - `loading`: the first read of the driver's documents;
 * - `hidden`: documents are off (the pilot until GATE-007, Y2), or this account is not a driver here: nothing shown;
 * - `ready`: the list as last read;
 * - `error`: the list could not be read yet.
 */
export type DriverDocumentsPhase = "loading" | "hidden" | "ready" | "error";

export interface DriverDocumentsViewState {
  readonly phase: DriverDocumentsPhase;
  readonly documents: readonly DriverDocumentSlot[];
  /** When the list was read (device clock): the warnings («Vence en N días», «Vencido») are computed for it. */
  readonly readAt: number | null;
  readonly refreshing: boolean;
  readonly message: string | null;
}

export const driverDocumentsMessages = Object.freeze({
  offline: "Necesitas conexión a internet para consultar tus documentos.",
  loadFailed: "No pudimos consultar tus documentos. Intenta más tarde.",
});

const loading: DriverDocumentsViewState = Object.freeze({
  phase: "loading",
  documents: [],
  readAt: null,
  refreshing: false,
  message: null,
});
const hidden: DriverDocumentsViewState = Object.freeze({ ...loading, phase: "hidden" });

/**
 * DRV-DOC-001-IN-PERSON-2026-11-02 (Y8 "En la app"): "Cuenta > Documentos" reads the driver's own documents when it
 * opens and when the driver asks to refresh; nothing else reads them, no message is pushed, and nothing is kept
 * outside this memory. Read-only: there is no write. Online only; a failed refresh keeps the list already shown.
 */
export class DriverDocumentsController {
  private readonly listeners = new Set<(state: DriverDocumentsViewState) => void>();
  private current: DriverDocumentsViewState = loading;
  private controller: AbortController | null = null;
  private disposed = false;

  public constructor(
    private readonly api: DriverDocumentsApi,
    private readonly now: () => number = Date.now,
  ) {}

  public get state(): DriverDocumentsViewState {
    return this.current;
  }

  public subscribe(listener: (state: DriverDocumentsViewState) => void): () => void {
    this.listeners.add(listener);
    listener(this.current);
    return () => this.listeners.delete(listener);
  }

  /** Reads the list; while a read is in flight another one is not started. */
  public async load(online: boolean): Promise<void> {
    if (this.disposed || this.current.refreshing) return;
    if (!online) {
      this.setState({
        ...this.current,
        phase: this.current.phase === "ready" ? "ready" : "error",
        message: driverDocumentsMessages.offline,
      });
      return;
    }

    const controller = new AbortController();
    this.controller = controller;
    this.setState({ ...this.current, refreshing: true, message: null });
    try {
      const answer = await this.api.getMyDocuments(controller.signal);
      if (this.disposed || this.controller !== controller) return;
      this.setState(
        answer.documents_enabled
          ? { phase: "ready", documents: answer.documents, readAt: this.now(), refreshing: false, message: null }
          : hidden,
      );
    } catch (error) {
      if (this.disposed || this.controller !== controller) return;
      if (error instanceof DriverDocumentsApiError && error.category === "cancelled") return;
      if (error instanceof DriverDocumentsApiError && (error.category === "forbidden" || error.category === "unauthorized")) {
        this.setState(hidden);
        return;
      }
      this.setState({
        ...this.current,
        phase: this.current.phase === "ready" ? "ready" : "error",
        refreshing: false,
        message: driverDocumentsMessages.loadFailed,
      });
    }
  }

  public dispose(): void {
    this.disposed = true;
    this.controller?.abort();
    this.controller = null;
    this.listeners.clear();
  }

  private setState(next: DriverDocumentsViewState): void {
    this.current = next;
    for (const listener of this.listeners) listener(next);
  }
}
