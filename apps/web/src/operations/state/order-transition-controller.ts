import type { OrderActionsApi } from "../api/order-actions-api";
import { TenantApiError } from "../api/tenant-request";
import {
  transitionConflictMessage,
  transitionRejectionMessage,
  type NextStepAction,
} from "../contracts/order-transitions";
import { describeFailure } from "./tenant-error-messages";

/**
 * UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05: the order detail's "Siguiente paso".
 *
 * The actions come from the server's allowed_transitions; transitionOrder still decides. One
 * Idempotency-Key belongs to one confirmed payload (action, reason, version, acknowledgement)
 * and is reused only to retry that same payload after a network or server failure. After any
 * answer other than a retryable failure the order detail refetches REST (`onOrderChanged`);
 * the screen never shows a status the server has not confirmed. Nothing is stored or logged.
 */
export interface OrderTransitionState {
  readonly busy: boolean;
  readonly message: string | null;
  /** The message reports a completed change rather than a failure. */
  readonly success: boolean;
  /** `/login?mfa=required&return_url=…` when the only missing requirement is MFA. */
  readonly stepUpHref: string | null;
}

export const initialOrderTransitionState: OrderTransitionState = {
  busy: false,
  message: null,
  success: false,
  stepUpHref: null,
};

interface PendingTransition {
  readonly signature: string;
  readonly key: string;
}

export class OrderTransitionController {
  private state: OrderTransitionState = initialOrderTransitionState;
  private disposed = false;
  private pending: PendingTransition | null = null;

  public constructor(
    private readonly api: OrderActionsApi,
    private readonly orderId: string,
    private readonly onChange: (state: OrderTransitionState) => void,
    private readonly onOrderChanged: () => void,
    private readonly randomUuid: () => string = () => crypto.randomUUID(),
  ) {}

  public get current(): OrderTransitionState {
    return this.state;
  }

  /** Sends one confirmed transition with the version the screen read. */
  public async submit(
    action: NextStepAction,
    reason: string,
    expectedVersion: number,
    restrictedGoodsAcknowledged: boolean,
  ): Promise<void> {
    if (this.disposed || this.state.busy) return;
    const signature = JSON.stringify([action.target, reason.trim(), expectedVersion, restrictedGoodsAcknowledged]);
    const pending =
      this.pending !== null && this.pending.signature === signature
        ? this.pending
        : { signature, key: `order-transition-${this.randomUuid()}` };
    this.pending = pending;
    this.set({ busy: true, message: null, success: false, stepUpHref: null });
    try {
      await this.api.transitionOrder(
        this.orderId,
        action,
        reason,
        expectedVersion,
        restrictedGoodsAcknowledged,
        pending.key,
      );
      if (this.disposed) return;
      this.pending = null;
      this.set({ busy: false, message: `Listo: ${action.label}. Se actualizó la información.`, success: true, stepUpHref: null });
      this.onOrderChanged();
    } catch (error: unknown) {
      if (this.disposed) return;
      const retryable = error instanceof TenantApiError && error.retryable;
      if (!retryable) this.pending = null;
      const view = describeFailure(
        error,
        transitionReturnUrl(this.orderId),
        {},
        transitionConflictMessage,
      );
      // ORD-002-GUARD-CODES-2026-10-05: a 409 names the unmet rule when the server sent its code.
      const message =
        error instanceof TenantApiError && error.category === "not_found"
          ? "La orden ya no está disponible. Se actualizó la información."
          : error instanceof TenantApiError && error.category === "invalid"
            ? "Revisa el motivo e intenta de nuevo."
            : error instanceof TenantApiError && error.category === "conflict"
              ? transitionRejectionMessage(error.code)
              : view.message;
      this.set({ busy: false, message, success: false, stepUpHref: view.stepUpHref });
      if (!retryable && !(error instanceof TenantApiError && error.category === "forbidden")) this.onOrderChanged();
    }
  }

  public dispose(): void {
    this.disposed = true;
    this.pending = null;
  }

  private set(state: OrderTransitionState): void {
    this.state = state;
    if (!this.disposed) this.onChange(state);
  }
}

/** The order detail page the MFA step-up returns to. */
export function transitionReturnUrl(orderId: string): string {
  return `/ops/orders/${encodeURIComponent(orderId)}`;
}
