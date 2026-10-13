import { maximumRetryAfterSeconds, readRetryAfterSeconds } from "./retry-after";

/**
 * SEC-004: the panel operations the API limits per user (SEC-004-HEAVY-ENDPOINT-MAPPING-2026-10-12). CSV preview and
 * commit share one budget (owner answer Q1), so they share one wait; the dashboard and the queue counts have one each
 * (owner answer Q3). "Buscar guía", "Agregar entrega", "Órdenes" and the pending COD list are all listOrders.
 */
export type RateLimitedOperation =
  | "csv_import"
  | "settlement_export"
  | "quote"
  | "order_list"
  | "assignable_drivers"
  | "dashboard"
  | "queue_counts";

/**
 * The wait when a 429 says nothing usable (no Retry-After, or not a positive whole number of seconds): 60 seconds,
 * the window of every panel limit (the technical note shown to the owner with Q11 and Q12).
 */
export const rateLimitFallbackSeconds = 60;

/** The whole seconds a 429 asks to wait: its Retry-After (capped at one hour), or the 60-second fallback. */
export function rateLimitSecondsOf(headers: Headers): number {
  return readRetryAfterSeconds(headers) ?? rateLimitFallbackSeconds;
}

/**
 * Owner answers Q6 ("Mensaje común con espera") and Q12 ("1 segundo"): the panel's one text for a 429, with N the
 * whole seconds still to wait.
 */
export function rateLimitMessage(seconds: number): string {
  const left = Math.max(1, Math.ceil(seconds));
  return left === 1
    ? "Demasiadas solicitudes. Espera 1 segundo e intenta de nuevo."
    : `Demasiadas solicitudes. Espera ${left} segundos e intenta de nuevo.`;
}

export interface RateLimitWait {
  /** Epoch milliseconds when the wait ends. */
  readonly until: number;
  /** The whole seconds the 429 asked for, for the one announcement. */
  readonly seconds: number;
  /** Increases with every recorded wait of the tab: the newest one is announced. */
  readonly sequence: number;
}

/**
 * The waits the server asked for in this tab, one per limited operation. The API clients record a wait when a 429
 * arrives; every screen that reads or sends the same operation reads it, so it sends nothing during the wait, shows
 * the message and re-enables its buttons when it ends. A limit never holds an unrelated one: each operation has its
 * own wait. The limits are per user, not per organization, so an organization switch keeps the waits. Memory only:
 * another tab has its own waits.
 */
export class RateLimitWaits {
  private readonly waits = new Map<RateLimitedOperation, RateLimitWait>();
  private readonly listeners = new Set<() => void>();
  private version = 0;
  private sequence = 0;

  public constructor(private readonly clock: () => number = () => Date.now()) {}

  /** Records the server's wait (whole seconds, or null for the 60-second fallback). */
  public record(operation: RateLimitedOperation, retryAfterSeconds: number | null): void {
    const asked = Math.ceil(retryAfterSeconds ?? rateLimitFallbackSeconds);
    const seconds = Math.min(Math.max(1, asked), maximumRetryAfterSeconds);
    const until = this.clock() + seconds * 1_000;
    const current = this.waits.get(operation);
    if (current !== undefined && current.until >= until) return;
    this.sequence += 1;
    this.waits.set(operation, { until, seconds, sequence: this.sequence });
    this.notify();
  }

  /** Milliseconds still to wait before every one of the operations may be called again; 0 when none waits. */
  public millisecondsLeft(operations: readonly RateLimitedOperation[]): number {
    const now = this.clock();
    let longest = 0;
    for (const operation of operations) {
      const wait = this.waits.get(operation);
      if (wait !== undefined && wait.until > now) longest = Math.max(longest, wait.until - now);
    }
    return longest;
  }

  /** Whole seconds still to wait (rounded up); 0 when none of the operations waits. */
  public secondsLeft(operations: readonly RateLimitedOperation[]): number {
    return Math.ceil(this.millisecondsLeft(operations) / 1_000);
  }

  public isWaiting(operations: readonly RateLimitedOperation[]): boolean {
    return this.millisecondsLeft(operations) > 0;
  }

  /** The newest wait of the tab while it lasts (any operation), for the one announcement; null when none runs. */
  public newest(): RateLimitWait | null {
    const now = this.clock();
    let newest: RateLimitWait | null = null;
    for (const wait of this.waits.values()) {
      if (wait.until > now && (newest === null || wait.sequence > newest.sequence)) newest = wait;
    }
    return newest;
  }

  /** For tests and sign-out: forgets every wait. */
  public clear(): void {
    this.waits.clear();
    this.notify();
  }

  /** Called whenever a wait is recorded or cleared (not when one runs out: read {@link millisecondsLeft}). */
  public subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  /** Changes with every record and clear, so a subscriber knows to read again. */
  public getVersion(): number {
    return this.version;
  }

  public now(): number {
    return this.clock();
  }

  private notify(): void {
    this.version += 1;
    for (const listener of [...this.listeners]) listener();
  }
}

/** The tab's waits, shared by every API client and screen of the panel. */
export const rateLimitWaits = new RateLimitWaits();

/** Resolves after `milliseconds`, or at once when the signal aborts. Never rejects. */
export function sleepOrAbort(milliseconds: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    if (signal.aborted) {
      resolve();
      return;
    }
    const done = () => {
      clearTimeout(timer);
      signal.removeEventListener("abort", done);
      resolve();
    };
    const timer = setTimeout(done, milliseconds);
    signal.addEventListener("abort", done, { once: true });
  });
}

/** Resolves once the server's wait for the operations has passed (at once when there is none), or when aborted. */
export async function sleepUntilWaitEnds(
  waits: RateLimitWaits,
  operations: readonly RateLimitedOperation[],
  signal: AbortSignal,
  sleep: (milliseconds: number, signal: AbortSignal) => Promise<void> = sleepOrAbort,
): Promise<void> {
  // A wait recorded meanwhile (a 429 elsewhere in the tab) is waited too.
  let left = waits.millisecondsLeft(operations);
  while (left > 0 && !signal.aborted) {
    const before = waits.now();
    await sleep(left, signal);
    // A clock that did not move (a test's sleep) cannot end the wait: stop instead of spinning.
    if (waits.now() === before) break;
    left = waits.millisecondsLeft(operations);
  }
}

/**
 * Calls `listener` when a wait is recorded and, while `millisecondsLeft()` is above 0, once per second on the second
 * (`everySecond`, for a countdown) or once when it reaches 0. Returns the unsubscribe, which also clears the one
 * timer: nothing is left running after it, nor after the wait ends.
 */
export function watchWait(
  waits: RateLimitWaits,
  millisecondsLeft: () => number,
  everySecond: boolean,
  listener: () => void,
): () => void {
  let timer: ReturnType<typeof setTimeout> | null = null;
  const disarm = () => {
    if (timer !== null) clearTimeout(timer);
    timer = null;
  };
  const arm = () => {
    disarm();
    const left = millisecondsLeft();
    if (left <= 0) return;
    timer = setTimeout(
      () => {
        timer = null;
        listener();
        arm();
      },
      everySecond ? left % 1_000 || 1_000 : left,
    );
  };
  const unsubscribe = waits.subscribe(() => {
    listener();
    arm();
  });
  arm();
  return () => {
    unsubscribe();
    disarm();
  };
}
