import type { AssignmentApi } from "../api/assignment-api";
import { TenantApiError } from "../api/tenant-request";
import type { AssignableDriver } from "../contracts/assignable-driver";
import { describeFailure, isRateLimited } from "./tenant-error-messages";
import {
  rateLimitWaits,
  sleepUntilWaitEnds,
  type RateLimitedOperation,
  type RateLimitWaits,
} from "../../lib/rate-limit-wait";

/** SEC-004: listAssignableDrivers, 60 per minute per user (owner answer Q10 keeps it). */
export const assignableDriversRateLimit: readonly RateLimitedOperation[] = ["assignable_drivers"];

export interface DriverListWaitOptions {
  /** The tab's server-imposed waits (tests pass their own). */
  readonly waits?: RateLimitWaits;
  /** Waits the given milliseconds or until the signal aborts (tests pass a fake one). */
  readonly sleep?: (milliseconds: number, signal: AbortSignal) => Promise<void>;
}

/**
 * UI-PHASE2-DRIVER-PICKER-2026-10-05: the order detail's "Asignar repartidor" panel.
 *
 * The list comes from listAssignableDrivers and is only a guide: assignDriver decides again
 * on the server. One Idempotency-Key belongs to one confirmed payload (driver and cost) and is
 * reused only to retry that same payload after a network or server failure. After a
 * successful assignment, or a conflict, the order detail refetches REST (`onOrderChanged`);
 * the panel never shows an assignment the server has not confirmed. Nothing is stored in the
 * browser and nothing is logged.
 */
export interface DriverAssignmentState {
  readonly kind: "loading" | "ready" | "unavailable";
  readonly drivers: readonly AssignableDriver[];
  readonly nextCursor: string | null;
  readonly busy: "idle" | "loading-more" | "assigning";
  readonly message: string | null;
  /** The message reports a completed assignment rather than a failure. */
  readonly success: boolean;
  /** `/login?mfa=required&return_url=…` when the only missing requirement is MFA. */
  readonly stepUpHref: string | null;
}

export const initialDriverAssignmentState: DriverAssignmentState = {
  kind: "loading",
  drivers: [],
  nextCursor: null,
  busy: "idle",
  message: null,
  success: false,
  stepUpHref: null,
};

/** es-MX text for the AI-05 DispatchAssignmentConflictProblem codes. */
export const assignmentConflictMessages: Readonly<Record<string, string>> = {
  DRIVER_INELIGIBLE: "El repartidor ya no cumple los requisitos para esta orden. Revisa la lista actualizada.",
  DRIVER_DOCUMENT_EXPIRED: "El repartidor tiene un documento vencido. Elige a otro repartidor.",
  CONFLICT: "La orden cambió y ya no admite esta asignación. Se actualizó la información.",
  INVALID_REQUEST: "Revisa el repartidor y el costo e intenta de nuevo.",
};

interface PendingAssignment {
  readonly driverId: string;
  readonly costCents: number;
  readonly key: string;
}

export class DriverAssignmentController {
  private state: DriverAssignmentState = initialDriverAssignmentState;
  private disposed = false;
  private generation = 0;
  private pending: PendingAssignment | null = null;
  private readonly abort = new AbortController();

  public constructor(
    private readonly api: AssignmentApi,
    private readonly orderId: string,
    private readonly onChange: (state: DriverAssignmentState) => void,
    private readonly onOrderChanged: () => void,
    private readonly randomUuid: () => string = () => crypto.randomUUID(),
    private readonly limits: DriverListWaitOptions = {},
  ) {}

  public get current(): DriverAssignmentState {
    return this.state;
  }

  /** Loads the first page again; keeps any message so the outcome of an assignment stays visible. */
  public async load(): Promise<void> {
    if (this.disposed) return;
    const generation = ++this.generation;
    this.set({ ...this.state, kind: this.state.drivers.length === 0 ? "loading" : this.state.kind, busy: "idle" });
    // SEC-004 (owner answer Q6): during the server's wait nothing is sent; the panel shows the common message with
    // its countdown and reads the list by itself when the wait ends.
    for (;;) {
      await sleepUntilWaitEnds(this.waits(), assignableDriversRateLimit, this.abort.signal, this.limits.sleep);
      if (!this.isCurrent(generation)) return;
      try {
        const page = await this.api.listAssignableDrivers(this.orderId);
        if (!this.isCurrent(generation)) return;
        this.set({ ...this.state, kind: "ready", drivers: page.items, nextCursor: page.next_cursor });
        return;
      } catch (error: unknown) {
        if (!this.isCurrent(generation)) return;
        if (isRateLimited(error)) {
          this.waits().ensure("assignable_drivers", error.retryAfterSeconds);
          continue;
        }
        this.set({ ...initialDriverAssignmentState, kind: "unavailable", ...this.listFailure(error) });
        return;
      }
    }
  }

  public async loadMore(): Promise<void> {
    const cursor = this.state.nextCursor;
    if (this.disposed || cursor === null || this.state.busy !== "idle") return;
    // SEC-004 (owner answer Q6): "Cargar más repartidores" re-enables itself when the server's wait ends.
    if (this.waits().isWaiting(assignableDriversRateLimit)) return;
    const generation = ++this.generation;
    this.set({ ...this.state, busy: "loading-more" });
    try {
      const page = await this.api.listAssignableDrivers(this.orderId, cursor);
      if (!this.isCurrent(generation)) return;
      const known = new Set(this.state.drivers.map((driver) => driver.driver_id));
      this.set({
        ...this.state,
        busy: "idle",
        drivers: [...this.state.drivers, ...page.items.filter((driver) => !known.has(driver.driver_id))],
        nextCursor: page.next_cursor,
      });
    } catch (error: unknown) {
      if (!this.isCurrent(generation)) return;
      // SEC-004: the common message with its countdown says it all; the drivers already shown stay.
      if (isRateLimited(error)) {
        this.waits().ensure("assignable_drivers", error.retryAfterSeconds);
        this.set({ ...this.state, busy: "idle" });
        return;
      }
      this.set({ ...this.state, busy: "idle", success: false, ...this.listFailure(error) });
    }
  }

  /** Sends the confirmed assignment; only an eligible listed driver and integer cents are accepted. */
  public async assign(driver: AssignableDriver, costCents: number): Promise<void> {
    if (this.disposed || this.state.busy !== "idle") return;
    if (!driver.eligible || !Number.isSafeInteger(costCents) || costCents < 0) {
      this.set({ ...this.state, message: "Elige un repartidor disponible y un costo válido.", success: false, stepUpHref: null });
      return;
    }
    const pending =
      this.pending !== null && this.pending.driverId === driver.driver_id && this.pending.costCents === costCents
        ? this.pending
        : { driverId: driver.driver_id, costCents, key: `assign-driver-${this.randomUuid()}` };
    this.pending = pending;
    const generation = ++this.generation;
    this.set({ ...this.state, busy: "assigning", message: null, success: false, stepUpHref: null });
    try {
      await this.api.assignDriver(this.orderId, pending.driverId, pending.costCents, pending.key);
      if (this.disposed) return;
      this.pending = null;
      this.set({
        ...this.state,
        busy: "idle",
        message: `Repartidor ${driver.driver_reference} asignado.`,
        success: true,
        stepUpHref: null,
      });
      this.onOrderChanged();
    } catch (error: unknown) {
      if (this.disposed) return;
      const retryable = error instanceof TenantApiError && error.retryable;
      if (!retryable) this.pending = null;
      const view = describeFailure(error, assignmentReturnUrl(this.orderId), assignmentConflictMessages);
      const message =
        error instanceof TenantApiError && error.category === "not_found"
          ? "La orden o el repartidor ya no están disponibles. Se actualizó la información."
          : view.message;
      if (this.isCurrent(generation))
        this.set({ ...this.state, busy: "idle", message, success: false, stepUpHref: view.stepUpHref });
      if (!retryable && !(error instanceof TenantApiError && error.category === "forbidden")) {
        this.onOrderChanged();
        void this.load();
      }
    }
  }

  public dispose(): void {
    this.disposed = true;
    this.generation += 1;
    this.pending = null;
    this.abort.abort();
  }

  private waits(): RateLimitWaits {
    return this.limits.waits ?? rateLimitWaits;
  }

  private listFailure(error: unknown): Pick<DriverAssignmentState, "message" | "stepUpHref"> {
    if (error instanceof TenantApiError && error.category === "conflict")
      return { message: "La orden ya no admite asignación. Se actualizó la información.", stepUpHref: null };
    if (error instanceof TenantApiError && error.category === "not_found")
      return { message: "La orden ya no está disponible.", stepUpHref: null };
    const view = describeFailure(error, assignmentReturnUrl(this.orderId));
    if (error instanceof TenantApiError && (error.retryable || error.category === "invalid"))
      return { message: "No fue posible cargar los repartidores. Intenta de nuevo.", stepUpHref: null };
    return { message: view.message, stepUpHref: view.stepUpHref };
  }

  private isCurrent(generation: number): boolean {
    return !this.disposed && generation === this.generation;
  }

  private set(state: DriverAssignmentState): void {
    this.state = state;
    if (!this.disposed) this.onChange(state);
  }
}

/** The order detail page the MFA step-up returns to. */
export function assignmentReturnUrl(orderId: string): string {
  return `/ops/orders/${encodeURIComponent(orderId)}`;
}
