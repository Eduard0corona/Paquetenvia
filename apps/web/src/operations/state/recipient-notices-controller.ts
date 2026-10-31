import type { RecipientNoticesApi } from "../api/recipient-notices-api";
import { TenantApiError } from "../api/tenant-request";
import {
  recipientNoticeTexts,
  type RecipientNoticeChannel,
  type RecipientNoticeList,
} from "../contracts/recipient-notices";
import { PendingSubmissions } from "./pending-submissions";
import { describeFailure } from "./tenant-error-messages";

/**
 * NTF-RECIPIENT-NOTICE-PANEL-2026-10-30 (AI-08 NTF-007; AI-07 /ops/orders/:id recipient_notices): the order detail's
 * panel "Avisos al destinatario".
 *
 * - The list is read when the screen asks for it (each read of the order, a NotificationStatusChanged v2 notice of the
 *   order, after "Detener avisos"); a failed read shows «No fue posible leer los avisos.» until the next one.
 * - "Detener avisos" sends one Idempotency-Key per press and reuses it for a retry of that press after an answer that
 *   may not have reached the server (network or 503); any definitive answer drops it, so the next press is a new one
 *   (T3: the button stays, and pressing again changes nothing for the recipient). The list is read again after every
 *   answer. The confirmation lives only in memory (T3).
 * - A 403 MFA_REQUIRED (PLATFORM_ADMIN without a second factor) offers the step-up back to this order.
 *
 * Nothing is written to browser storage and nothing is logged.
 */
export type RecipientNoticeListState =
  | { readonly kind: "loading" }
  | { readonly kind: "loaded"; readonly list: RecipientNoticeList }
  | { readonly kind: "failed"; readonly message: string; readonly stepUpHref: string | null };

export type RecipientNoticeStopState =
  | { readonly kind: "idle" }
  | { readonly kind: "busy" }
  | { readonly kind: "stopped"; readonly channels: readonly RecipientNoticeChannel[] }
  | { readonly kind: "failed"; readonly message: string; readonly stepUpHref: string | null };

export interface RecipientNoticesState {
  readonly list: RecipientNoticeListState;
  readonly stop: RecipientNoticeStopState;
}

const stopScope = "stopOrderRecipientNotices";

export class RecipientNoticesController {
  private state: RecipientNoticesState = { list: { kind: "loading" }, stop: { kind: "idle" } };
  private readonly pending: PendingSubmissions;
  private disposed = false;
  private listGeneration = 0;
  private abort = new AbortController();

  public constructor(
    private readonly api: RecipientNoticesApi,
    private readonly orderId: string,
    private readonly onChange: (state: RecipientNoticesState) => void,
    /** Asks the screen to read the list again after a stop's answer (gathered with the screen's reloads). */
    private readonly afterStop: () => void,
    randomUuid: () => string = () => crypto.randomUUID(),
  ) {
    this.pending = new PendingSubmissions(() => `recipient-notices-stop-${randomUuid()}`);
  }

  public get current(): RecipientNoticesState {
    return this.state;
  }

  /** Reads the list; a later read wins over an earlier one still in flight. */
  public async load(): Promise<void> {
    if (this.disposed) return;
    const generation = ++this.listGeneration;
    try {
      const list = await this.api.list(this.orderId, this.abort.signal);
      if (!this.isCurrentList(generation)) return;
      this.set({ ...this.state, list: { kind: "loaded", list } });
    } catch (error: unknown) {
      if (!this.isCurrentList(generation)) return;
      const view = this.failure(error, recipientNoticeTexts.readFailure);
      this.set({ ...this.state, list: { kind: "failed", ...view } });
    }
  }

  /** "Detener avisos", after the person confirmed the dialog. */
  public async stop(): Promise<void> {
    if (this.disposed || this.state.stop.kind === "busy") return;
    const prepared = this.pending.prepare(stopScope, this.orderId, () => null);
    this.set({ ...this.state, stop: { kind: "busy" } });
    try {
      const result = await this.api.stop(this.orderId, prepared.key, this.abort.signal);
      if (this.disposed) return;
      this.pending.settle(stopScope);
      this.set({ ...this.state, stop: { kind: "stopped", channels: result.channels } });
    } catch (error: unknown) {
      if (this.disposed) return;
      // Only an answer that may not have reached the server keeps the key for a retry of the same press.
      if (!(error instanceof TenantApiError && (error.category === "network" || error.category === "unavailable")))
        this.pending.settle(stopScope);
      const view = this.failure(error, recipientNoticeTexts.stopFailure);
      this.set({ ...this.state, stop: { kind: "failed", ...view } });
    }
    this.afterStop();
  }

  public dispose(): void {
    this.disposed = true;
    this.listGeneration += 1;
    this.abort.abort();
    this.abort = new AbortController();
    this.pending.clear();
  }

  private failure(error: unknown, message: string): { readonly message: string; readonly stepUpHref: string | null } {
    if (error instanceof TenantApiError && error.mfaRequired) {
      const view = describeFailure(error, `/ops/orders/${encodeURIComponent(this.orderId)}`);
      return { message: view.message, stepUpHref: view.stepUpHref };
    }
    return { message, stepUpHref: null };
  }

  private isCurrentList(generation: number): boolean {
    return !this.disposed && generation === this.listGeneration;
  }

  private set(state: RecipientNoticesState): void {
    this.state = state;
    if (!this.disposed) this.onChange(state);
  }
}
