import { maximumRetryAfterSeconds } from "./retry-after";

/**
 * SEC-004: the panel operations the API limits per user
 * (SEC-004-HEAVY-ENDPOINT-MAPPING-2026-10-12). CSV preview and commit share one
 * budget, so they share one wait; the dashboard and the queue counts have one
 * each (owner answer Q3).
 */
export type RateLimitedOperation =
  | "csv_import"
  | "settlement_export"
  | "quote"
  | "order_list"
  | "assignable_drivers"
  | "dashboard"
  | "queue_counts";

/** The wait when a 429 says nothing usable: the API's default window. */
export const rateLimitFallbackSeconds = 60;

/**
 * Owner answer Q6, "Mensaje común con espera": the panel's one text for a 429,
 * with N the whole seconds still to wait ("1 segundo" when one is left).
 */
export function rateLimitMessage(seconds: number): string {
  const left = Math.max(1, Math.ceil(seconds));
  return `Demasiadas solicitudes. Espera ${left} ${left === 1 ? "segundo" : "segundos"} e intenta de nuevo.`;
}

/**
 * The waits the server asked for in this tab, one per limited operation, until
 * an instant in epoch milliseconds. The API clients record them when a 429
 * arrives; the screens read them to show the message and to keep the buttons
 * that would call the operation disabled until it ends. A limit is per user,
 * not per organization, so an organization switch keeps the wait. Memory only.
 */
export class RateLimitWaits {
  private readonly waits = new Map<RateLimitedOperation, number>();
  private readonly listeners = new Set<() => void>();
  private version = 0;

  public constructor(private readonly now: () => number = () => Date.now()) {}

  /** Records the server's wait (whole seconds, or null for the fallback); returns when it ends. */
  public record(operation: RateLimitedOperation, retryAfterSeconds: number | null): number {
    const seconds = Math.min(
      Math.max(1, retryAfterSeconds ?? rateLimitFallbackSeconds),
      maximumRetryAfterSeconds,
    );
    const until = this.now() + seconds * 1_000;
    this.waits.set(operation, Math.max(this.waits.get(operation) ?? 0, until));
    this.notify();
    return until;
  }

  /**
   * Records the wait of a 429 unless one already runs for the operation (the API client records it first; this
   * keeps a screen that met a 429 from sending again at once when it did not).
   */
  public ensure(operation: RateLimitedOperation, retryAfterSeconds: number | null): void {
    if (!this.isWaiting([operation])) this.record(operation, retryAfterSeconds);
  }

  /** Milliseconds still to wait before every one of the operations may be called again; 0 when none waits. */
  public millisecondsLeft(operations: readonly RateLimitedOperation[]): number {
    const now = this.now();
    let longest = 0;
    for (const operation of operations) {
      const until = this.waits.get(operation);
      if (until !== undefined && until > now) longest = Math.max(longest, until - now);
    }
    return longest;
  }

  /** Whole seconds still to wait before any of the operations may be called again; 0 when none waits. */
  public secondsLeft(operations: readonly RateLimitedOperation[]): number {
    return Math.ceil(this.millisecondsLeft(operations) / 1_000);
  }

  public isWaiting(operations: readonly RateLimitedOperation[]): boolean {
    return this.secondsLeft(operations) > 0;
  }

  /** For tests: forgets every wait. */
  public clear(): void {
    this.waits.clear();
    this.notify();
  }

  public subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  /** Changes on every record and clear, so a subscriber knows to read again. */
  public getVersion(): number {
    return this.version;
  }

  private notify(): void {
    this.version += 1;
    for (const listener of [...this.listeners]) listener();
  }
}

/** The tab's waits, shared by every API client and screen. */
export const rateLimitWaits = new RateLimitWaits();

/** Resolves after `milliseconds`, or at once when the signal aborts. */
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

/** Waits until the server's wait for the operations has passed (at once when there is none). */
export async function sleepUntilWaitEnds(
  waits: RateLimitWaits,
  operations: readonly RateLimitedOperation[],
  signal: AbortSignal,
  sleep: (milliseconds: number, signal: AbortSignal) => Promise<void> = sleepOrAbort,
): Promise<void> {
  const milliseconds = waits.millisecondsLeft(operations);
  if (milliseconds > 0 && !signal.aborted) await sleep(milliseconds, signal);
}
