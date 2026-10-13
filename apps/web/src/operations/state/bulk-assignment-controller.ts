import type { AssignmentApi } from "../api/assignment-api";
import { TenantApiError } from "../api/tenant-request";
import type { AssignableDriver } from "../contracts/assignable-driver";
import { bulkRunSummary, type BulkDriverChoice, type BulkOrder } from "../contracts/bulk-assignment";
import {
  assignableDriversRateLimit,
  assignmentConflictMessages,
  type DriverListWaitOptions,
} from "./driver-assignment-controller";
import { PendingSubmissions } from "./pending-submissions";
import { describeFailure } from "./tenant-error-messages";
import { rateLimitWaits, sleepUntilWaitEnds, type RateLimitWaits } from "../../lib/rate-limit-wait";

/**
 * UI-PHASE3-INBOX-PRICE-REVIEW-BULK-2026-10-11: state of one "Asignar seleccionadas…" batch.
 *
 * The batch is the selection taken when the panel opened. listAssignableDrivers is read for each
 * order (every page, one order after another), so the panel can offer only the drivers eligible
 * for every order. After the confirmation, assignDriver runs for one order at a time, each with its
 * own Idempotency-Key that is kept, with the same payload, until the server gives a definitive
 * answer; a retry after a network or server failure resends that key. Each order keeps its own
 * result, a stop is never hidden, and the inbox reads REST again afterwards. Nothing is stored in
 * the browser and nothing is logged.
 *
 * SEC-004, owner answer Q10 ("Si pasa, el panel pide esperar unos segundos."): a 429 on one order's
 * drivers leaves that order failed with its "Reintentar", which stays disabled while the server's
 * wait lasts and re-enables itself when it ends (it is not read again by itself). The orders not
 * read yet wait for the same wait before their first read instead of meeting the same 429. The
 * per-order read is unchanged.
 */
export type BulkAssignmentApi = Pick<AssignmentApi, "listAssignableDrivers" | "assignDriver">;

export type BulkDriversLoad =
  | { readonly kind: "loading" }
  | { readonly kind: "ready"; readonly drivers: readonly AssignableDriver[] }
  | { readonly kind: "failed"; readonly message: string; readonly retryable: boolean };

export type BulkRowResult =
  /** Part of the run, waiting for its turn. */
  | { readonly kind: "pending" }
  | { readonly kind: "sending" }
  | { readonly kind: "assigned" }
  | { readonly kind: "failed"; readonly message: string; readonly retryable: boolean }
  /** The run stopped before this order was sent. */
  | { readonly kind: "not_sent" };

/** What was confirmed: one driver and the integer cents of each order. */
export interface BulkRun {
  readonly driver: BulkDriverChoice;
  readonly costs: ReadonlyMap<string, number>;
}

export interface BulkAssignmentState {
  readonly phase: "loading" | "ready" | "running" | "done";
  readonly orders: readonly BulkOrder[];
  readonly drivers: Readonly<Record<string, BulkDriversLoad>>;
  readonly run: BulkRun | null;
  readonly results: Readonly<Record<string, BulkRowResult>>;
  /** The outcome of the last run, or why it stopped. */
  readonly message: string | null;
  /** `/login?mfa=required&return_url=…` when the run stopped only for MFA. */
  readonly stepUpHref: string | null;
  /** Some orders can be sent again: the run stopped on a network or server failure. */
  readonly canRetry: boolean;
}

export const initialBulkAssignmentState: BulkAssignmentState = {
  phase: "loading",
  orders: [],
  drivers: {},
  run: null,
  results: {},
  message: null,
  stepUpHref: null,
  canRetry: false,
};

/** listAssignableDrivers pages (100 drivers each) read per order before giving up on that order. */
export const maximumDriverPagesPerOrder = 10;

export const bulkDriversUnavailableMessage = "No fue posible cargar los repartidores de esta orden. Intenta de nuevo.";
export const bulkDriversTooManyMessage =
  "No fue posible revisar a todos los repartidores de esta orden; asígnala desde su detalle.";
export const bulkNotFoundMessage = "La orden o el repartidor ya no están disponibles.";
export const bulkStoppedRetryMessage =
  "El envío se detuvo porque el servidor no respondió. Puedes reintentar las órdenes pendientes: se reenvían sin duplicarse.";

class DriverListIncompleteError extends Error {
  public constructor() {
    super("The assignable driver list could not be read completely.");
    this.name = "DriverListIncompleteError";
  }
}

interface SendFailure {
  readonly message: string;
  /** Worth resending with the same key and payload. */
  readonly retryable: boolean;
  /** No later order is sent: the failure is not about this order alone. */
  readonly stop: boolean;
  readonly stepUpHref: string | null;
}

export class BulkAssignmentController {
  private state: BulkAssignmentState = initialBulkAssignmentState;
  private disposed = false;
  private readonly abort = new AbortController();
  private readonly keys: PendingSubmissions;

  public constructor(
    private readonly api: BulkAssignmentApi,
    private readonly onChange: (state: BulkAssignmentState) => void,
    /** The inbox reads the queue and the counts again. */
    private readonly onChanged: () => void,
    /** Where the MFA step-up returns (the inbox view). */
    private readonly returnUrl: string,
    randomUuid: () => string = () => crypto.randomUUID(),
    private readonly limits: DriverListWaitOptions = {},
  ) {
    this.keys = new PendingSubmissions(() => `assign-driver-${randomUuid()}`);
  }

  /** The server's wait for listAssignableDrivers is running: "Reintentar" stays disabled. */
  public get rateLimited(): boolean {
    return this.waits().isWaiting(assignableDriversRateLimit);
  }

  public get current(): BulkAssignmentState {
    return this.state;
  }

  /** Reads the drivers of every order of the batch, one order after another. */
  public async load(orders: readonly BulkOrder[]): Promise<void> {
    if (this.disposed) return;
    this.set({
      ...initialBulkAssignmentState,
      orders,
      drivers: Object.fromEntries(orders.map((order) => [order.orderId, { kind: "loading" } as const])),
    });
    for (const order of orders) {
      if (this.disposed) return;
      // SEC-004: the next order is read only once the server's wait (if any) has passed.
      await sleepUntilWaitEnds(this.waits(), assignableDriversRateLimit, this.abort.signal);
      if (this.disposed) return;
      if (this.has(order.orderId)) await this.loadOrder(order.orderId);
    }
    if (!this.disposed && this.state.phase === "loading") this.set({ ...this.state, phase: "ready" });
  }

  /** Reads one order's drivers again after a failure. */
  public async retryLoad(orderId: string): Promise<void> {
    if (this.disposed || this.state.phase !== "ready" || this.state.drivers[orderId]?.kind !== "failed") return;
    if (this.rateLimited) return;
    this.setDrivers(orderId, { kind: "loading" });
    await this.loadOrder(orderId);
  }

  /** Takes an order out of the batch before anything is sent. */
  public remove(orderId: string): void {
    if (this.disposed || (this.state.phase !== "loading" && this.state.phase !== "ready")) return;
    const drivers = { ...this.state.drivers };
    delete drivers[orderId];
    this.set({ ...this.state, orders: this.state.orders.filter((order) => order.orderId !== orderId), drivers });
  }

  /**
   * Sends the confirmed batch: every order with its own cost, to the one driver, one after another.
   * Only a run that covers every order with safe non-negative integer cents is accepted.
   */
  public async execute(run: BulkRun): Promise<void> {
    if (this.disposed || this.state.phase !== "ready" || this.state.orders.length === 0) return;
    const complete = this.state.orders.every((order) => {
      const cents = run.costs.get(order.orderId);
      return cents !== undefined && Number.isSafeInteger(cents) && cents >= 0 && this.state.drivers[order.orderId]?.kind === "ready";
    });
    if (!complete || run.driver.blockedBy.length > 0) return;
    this.set({
      ...this.state,
      phase: "running",
      run,
      results: Object.fromEntries(this.state.orders.map((order) => [order.orderId, { kind: "pending" } as const])),
      message: null,
      stepUpHref: null,
      canRetry: false,
    });
    await this.send(this.state.orders.map((order) => order.orderId));
  }

  /** Sends again, with the same keys and payloads, the orders a stopped run left without an answer. */
  public async retry(): Promise<void> {
    if (this.disposed || this.state.phase !== "done" || !this.state.canRetry) return;
    const ids = this.state.orders
      .map((order) => order.orderId)
      .filter((orderId) => isResendable(this.state.results[orderId]));
    if (ids.length === 0) return;
    const results = { ...this.state.results };
    for (const orderId of ids) results[orderId] = { kind: "pending" };
    this.set({ ...this.state, phase: "running", results, message: null, stepUpHref: null, canRetry: false });
    await this.send(ids);
  }

  public dispose(): void {
    this.disposed = true;
    this.abort.abort();
    this.keys.clear();
  }

  private async send(orderIds: readonly string[]): Promise<void> {
    const run = this.state.run;
    if (run === null) return;
    let stopped: SendFailure | null = null;
    for (const orderId of orderIds) {
      if (this.disposed) return;
      if (stopped !== null) {
        this.setResult(orderId, { kind: "not_sent" });
        continue;
      }
      const costCents = run.costs.get(orderId) ?? -1;
      const prepared = this.keys.prepare(orderId, `${run.driver.driverId}|${costCents}`, () => ({
        driverId: run.driver.driverId,
        costCents,
      }));
      this.setResult(orderId, { kind: "sending" });
      try {
        await this.api.assignDriver(
          orderId,
          prepared.payload.driverId,
          prepared.payload.costCents,
          prepared.key,
          this.abort.signal,
        );
        if (this.disposed) return;
        this.keys.settle(orderId);
        this.setResult(orderId, { kind: "assigned" });
      } catch (error: unknown) {
        if (this.disposed) return;
        const failure = this.sendFailure(error);
        if (!failure.retryable) this.keys.settle(orderId);
        this.setResult(orderId, { kind: "failed", message: failure.message, retryable: failure.retryable });
        if (failure.stop) stopped = failure;
      }
    }
    if (this.disposed) return;
    const results = this.state.results;
    const total = this.state.orders.length;
    const assigned = this.state.orders.filter((order) => results[order.orderId]?.kind === "assigned").length;
    const canRetry =
      (stopped === null || stopped.retryable) &&
      this.state.orders.some((order) => isResendable(results[order.orderId]));
    const summary = bulkRunSummary(assigned, total);
    const stop = stopped === null ? null : stopped.retryable ? bulkStoppedRetryMessage : `El envío se detuvo: ${stopped.message}`;
    this.set({
      ...this.state,
      phase: "done",
      message: stop === null ? summary : `${summary} ${stop}`,
      stepUpHref: stopped?.stepUpHref ?? null,
      canRetry,
    });
    // REST is the authority: the queue and the counts are read again after every run.
    this.onChanged();
  }

  private async loadOrder(orderId: string): Promise<void> {
    try {
      const drivers = await this.readAllDrivers(orderId);
      if (this.disposed || !this.has(orderId)) return;
      this.setDrivers(orderId, { kind: "ready", drivers });
    } catch (error: unknown) {
      if (this.disposed || !this.has(orderId)) return;
      this.setDrivers(orderId, { kind: "failed", ...loadFailure(error, this.returnUrl) });
    }
  }

  /** Every page of one order's list; a repeated cursor or too many pages leave it incomplete. */
  private async readAllDrivers(orderId: string): Promise<readonly AssignableDriver[]> {
    const drivers: AssignableDriver[] = [];
    const known = new Set<string>();
    const cursors = new Set<string>();
    let cursor: string | null = null;
    for (let page = 0; page < maximumDriverPagesPerOrder; page += 1) {
      const result = await this.api.listAssignableDrivers(orderId, cursor, this.abort.signal);
      for (const driver of result.items) {
        if (known.has(driver.driver_id)) continue;
        known.add(driver.driver_id);
        drivers.push(driver);
      }
      if (result.next_cursor === null) return drivers;
      if (cursors.has(result.next_cursor)) throw new DriverListIncompleteError();
      cursors.add(result.next_cursor);
      cursor = result.next_cursor;
    }
    throw new DriverListIncompleteError();
  }

  private sendFailure(error: unknown): SendFailure {
    if (error instanceof TenantApiError && error.category === "not_found")
      return { message: bulkNotFoundMessage, retryable: false, stop: false, stepUpHref: null };
    const view = describeFailure(error, this.returnUrl, assignmentConflictMessages);
    return {
      message: view.message,
      retryable: error instanceof TenantApiError && error.retryable,
      // A 409 answers for this order only; anything else (session, role, MFA, network, server or an
      // unexpected answer) would repeat for every order, so the run stops there.
      stop: !(error instanceof TenantApiError && error.category === "conflict"),
      stepUpHref: view.stepUpHref,
    };
  }

  private waits(): RateLimitWaits {
    return this.limits.waits ?? rateLimitWaits;
  }

  private has(orderId: string): boolean {
    return this.state.orders.some((order) => order.orderId === orderId);
  }

  private setDrivers(orderId: string, load: BulkDriversLoad): void {
    this.set({ ...this.state, drivers: { ...this.state.drivers, [orderId]: load } });
  }

  private setResult(orderId: string, result: BulkRowResult): void {
    this.set({ ...this.state, results: { ...this.state.results, [orderId]: result } });
  }

  private set(state: BulkAssignmentState): void {
    this.state = state;
    if (!this.disposed) this.onChange(state);
  }
}

function isResendable(result: BulkRowResult | undefined): boolean {
  return result !== undefined && (result.kind === "not_sent" || (result.kind === "failed" && result.retryable));
}

function loadFailure(error: unknown, returnUrl: string): { readonly message: string; readonly retryable: boolean } {
  if (error instanceof DriverListIncompleteError) return { message: bulkDriversTooManyMessage, retryable: false };
  if (error instanceof TenantApiError) {
    if (error.category === "conflict") return { message: "Esta orden ya no admite asignación.", retryable: false };
    if (error.category === "not_found") return { message: "Esta orden ya no está disponible.", retryable: false };
    if (error.retryable || error.category === "invalid") return { message: bulkDriversUnavailableMessage, retryable: true };
  }
  return { message: describeFailure(error, returnUrl).message, retryable: false };
}
