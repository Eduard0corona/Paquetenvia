import {
  createTrackingLinkIdempotencyKey,
  trackingLinkOrderFinishedCode,
  type TrackingLinkApi,
} from "../api/tracking-link-api";
import { TenantApiError } from "../api/tenant-request";
import { describeFailure } from "./tenant-error-messages";

/**
 * TRK-002-AUTO-LINK: the order detail's tracking link panel.
 *
 * Every order already has its link; "show" reads it through get-or-create, which
 * returns the same link every time and never rotates it. Revoking retires it; the
 * next "show" gets a new generation, unless the order is finished (409
 * TRACKING_LINK_ORDER_FINISHED). The link lives only in this controller's memory
 * while it is shown: hiding it, revoking it or disposing the controller drops it.
 * Nothing is written to browser storage and nothing is logged. A 403 MFA_REQUIRED
 * (PLATFORM_ADMIN without a second factor) offers the step-up
 * `/login?mfa=required&return_url=/ops/orders/{orderId}`.
 */
export type TrackingLinkState =
  | {
      readonly kind: "idle";
      readonly message: string | null;
      /** `/login?mfa=required&return_url=…` when the only missing requirement is MFA. */
      readonly stepUpHref: string | null;
    }
  | { readonly kind: "busy"; readonly action: "show" | "revoke" }
  | {
      readonly kind: "shown";
      readonly url: string;
      readonly generation: number;
      /** Null while the order is in progress. */
      readonly validUntil: string | null;
      readonly copied: boolean;
    };

export interface ClipboardWriter {
  writeText(text: string): Promise<void>;
}

export class TrackingLinkController {
  private state: TrackingLinkState = idle(null);
  private disposed = false;
  private generation = 0;

  public constructor(
    private readonly api: TrackingLinkApi,
    private readonly orderId: string,
    private readonly onChange: (state: TrackingLinkState) => void,
    private readonly randomUuid: () => string = () => crypto.randomUUID(),
  ) {}

  public get current(): TrackingLinkState {
    return this.state;
  }

  public async show(): Promise<void> {
    if (this.disposed || this.state.kind === "busy") return;
    const generation = ++this.generation;
    this.set({ kind: "busy", action: "show" });
    try {
      const link = await this.api.getOrCreate(
        this.orderId,
        createTrackingLinkIdempotencyKey(this.randomUuid),
      );
      if (!this.isCurrent(generation)) return;
      this.set({
        kind: "shown",
        url: link.url,
        generation: link.generation,
        validUntil: link.validUntil,
        copied: false,
      });
    } catch (error: unknown) {
      if (!this.isCurrent(generation)) return;
      this.set(this.failure(error, "show"));
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
      this.set(
        idle(
          "Enlace revocado: ya no muestra la orden. Si la orden sigue en curso, al volver a verlo se genera uno nuevo.",
        ),
      );
    } catch (error: unknown) {
      if (!this.isCurrent(generation)) return;
      this.set(this.failure(error, "revoke"));
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

  /** Drops the link from memory; it stays the order's link and can be shown again. */
  public hide(): void {
    if (this.disposed || this.state.kind !== "shown") return;
    this.generation += 1;
    this.set(idle(null));
  }

  public dispose(): void {
    this.disposed = true;
    this.generation += 1;
    this.state = idle(null);
  }

  private failure(error: unknown, action: "show" | "revoke"): TrackingLinkState {
    if (error instanceof TenantApiError && error.mfaRequired) {
      const view = describeFailure(error, trackingLinkReturnUrl(this.orderId));
      return { kind: "idle", message: view.message, stepUpHref: view.stepUpHref };
    }
    return idle(failureMessage(error, action));
  }

  private isCurrent(generation: number): boolean {
    return !this.disposed && generation === this.generation;
  }

  private set(state: TrackingLinkState): void {
    this.state = state;
    if (!this.disposed) this.onChange(state);
  }
}

/** The order detail page the step-up returns to. */
export function trackingLinkReturnUrl(orderId: string): string {
  return `/ops/orders/${encodeURIComponent(orderId)}`;
}

function idle(message: string | null): TrackingLinkState {
  return { kind: "idle", message, stepUpHref: null };
}

function failureMessage(error: unknown, action: "show" | "revoke"): string {
  const category =
    error instanceof TenantApiError ? error.category : "unavailable";
  if (category === "forbidden" || category === "unauthorized")
    return "No tienes permiso para gestionar el enlace de seguimiento.";
  if (category === "not_found") return "La orden no está disponible.";
  if (
    category === "conflict" &&
    error instanceof TenantApiError &&
    error.code === trackingLinkOrderFinishedCode
  )
    return "La orden ya terminó y su enlace ya no está vigente; no se generan enlaces nuevos.";
  return action === "show"
    ? "No fue posible obtener el enlace. Intenta de nuevo."
    : "No fue posible revocar el enlace. Intenta de nuevo.";
}
