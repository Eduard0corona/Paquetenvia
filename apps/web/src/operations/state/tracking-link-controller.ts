import {
  createTrackingLinkIdempotencyKey,
  publicTrackingUrl,
  type TrackingLinkApi,
} from "../api/tracking-link-api";
import { OperationsApiError } from "../api/operations-api";

/**
 * TRK-002-ISSUE-ENDPOINT: the order detail's tracking link actions.
 *
 * The link lives only in this controller's memory, for as long as it is shown:
 * hiding it, revoking it, issuing another one or disposing the controller drops
 * it. Nothing is written to browser storage and nothing is logged.
 */
export type TrackingLinkState =
  | { readonly kind: "idle"; readonly message: string | null }
  | { readonly kind: "busy"; readonly action: "issue" | "revoke" }
  | {
      readonly kind: "shown";
      readonly url: string;
      readonly expiresAt: string;
      readonly copied: boolean;
    };

export interface ClipboardWriter {
  writeText(text: string): Promise<void>;
}

export class TrackingLinkController {
  private state: TrackingLinkState = { kind: "idle", message: null };
  private disposed = false;
  private generation = 0;

  public constructor(
    private readonly api: TrackingLinkApi,
    private readonly orderId: string,
    private readonly origin: string,
    private readonly onChange: (state: TrackingLinkState) => void,
    private readonly randomUuid: () => string = () => crypto.randomUUID(),
  ) {}

  public get current(): TrackingLinkState {
    return this.state;
  }

  public async issue(): Promise<void> {
    if (this.disposed || this.state.kind === "busy") return;
    const generation = ++this.generation;
    this.set({ kind: "busy", action: "issue" });
    try {
      const link = await this.api.issue(
        this.orderId,
        createTrackingLinkIdempotencyKey(this.randomUuid),
      );
      if (!this.isCurrent(generation)) return;
      this.set({
        kind: "shown",
        url: publicTrackingUrl(this.origin, link.token),
        expiresAt: link.expiresAt,
        copied: false,
      });
    } catch (error: unknown) {
      if (!this.isCurrent(generation)) return;
      this.set({ kind: "idle", message: failureMessage(error, "issue") });
    }
  }

  public async revoke(): Promise<void> {
    if (this.disposed || this.state.kind === "busy") return;
    const generation = ++this.generation;
    this.set({ kind: "busy", action: "revoke" });
    try {
      await this.api.revoke(
        this.orderId,
        createTrackingLinkIdempotencyKey(this.randomUuid),
      );
      if (!this.isCurrent(generation)) return;
      this.set({
        kind: "idle",
        message: "Enlace revocado. El enlace anterior ya no muestra la orden.",
      });
    } catch (error: unknown) {
      if (!this.isCurrent(generation)) return;
      this.set({ kind: "idle", message: failureMessage(error, "revoke") });
    }
  }

  public async copy(clipboard: ClipboardWriter | undefined): Promise<void> {
    const state = this.state;
    if (this.disposed || state.kind !== "shown") return;
    const generation = this.generation;
    try {
      if (clipboard === undefined) throw new Error("Clipboard unavailable.");
      await clipboard.writeText(state.url);
      if (this.isCurrent(generation) && this.state === state)
        this.set({ ...state, copied: true });
    } catch {
      if (this.isCurrent(generation) && this.state === state)
        this.set({ ...state, copied: false });
    }
  }

  /** Drops the link from memory; it stays valid until it expires or is revoked. */
  public hide(): void {
    if (this.disposed || this.state.kind !== "shown") return;
    this.generation += 1;
    this.set({
      kind: "idle",
      message: "El enlace se ocultó y no se puede volver a mostrar. Genera uno nuevo si lo necesitas.",
    });
  }

  public dispose(): void {
    this.disposed = true;
    this.generation += 1;
    this.state = { kind: "idle", message: null };
  }

  private isCurrent(generation: number): boolean {
    return !this.disposed && generation === this.generation;
  }

  private set(state: TrackingLinkState): void {
    this.state = state;
    if (!this.disposed) this.onChange(state);
  }
}

function failureMessage(error: unknown, action: "issue" | "revoke"): string {
  const category =
    error instanceof OperationsApiError ? error.category : "unavailable";
  if (category === "forbidden" || category === "unauthorized")
    return "No tienes permiso para gestionar el enlace de seguimiento.";
  if (category === "not_found") return "La orden no está disponible.";
  return action === "issue"
    ? "No fue posible generar el enlace. Intenta de nuevo."
    : "No fue posible revocar el enlace. Intenta de nuevo.";
}
