import { rateLimitWaits, type RateLimitedOperation, type RateLimitWaits } from "../../lib/rate-limit-wait";

/**
 * SEC-004-RELOADS-AND-BULK-2026-10-13, owner answer Q9 "Espaciar recargas (Recommended)": "El panel junta los avisos
 * y recarga como máximo cada 3 segundos por pestaña."
 */
export const realtimeReloadSpacingMilliseconds = 3_000;

export interface ReloadTimers {
  readonly now: () => number;
  readonly setTimer: (callback: () => void, delay: number) => unknown;
  readonly clearTimer: (timer: unknown) => void;
}

const defaultTimers: ReloadTimers = {
  now: () => Date.now(),
  setTimer: (callback, delay) => setTimeout(callback, delay),
  clearTimer: (timer) => clearTimeout(timer as ReturnType<typeof setTimeout>),
};

export interface ScreenReloadsOptions<Kind extends string> {
  /**
   * Every limited operation the screen reads. A wait for any of them pauses the whole screen (owner answer Q16,
   * "Toda la pantalla": one message, every automatic reload paused, everything read once when it ends).
   */
  readonly operations: readonly RateLimitedOperation[];
  /** Starts one reload of the screen; a React hook may set it later with {@link ScreenReloads.setReload}. */
  readonly reload?: (kind: Kind) => void;
  /** Of two pending kinds, the one the gathered reload uses (for example the one that also reads the counts). */
  readonly stronger?: (left: Kind, right: Kind) => Kind;
  /** Milliseconds between two reloads asked by realtime notices: 3 seconds (Q9); 0 for a screen without them. */
  readonly spacing?: number;
  readonly waits?: RateLimitWaits;
  readonly timers?: Partial<ReloadTimers>;
}

/**
 * The automatic reloads of one panel screen; a tab shows one screen at a time, so this is per tab.
 *
 * - **Realtime notices (Q9).** {@link notice} gathers them into one reload. It runs after the screen's short
 *   debounce, and no sooner than 3 seconds after the screen's previous reload of any kind. A reload already pending
 *   absorbs further notices and keeps its time, so a steady stream reloads every 3 seconds and no notice is lost: one
 *   that arrives during the spacing is read by the reload that ends it.
 * - **The server's wait (Q11 "Reintenta sola", Q14 "Se pausan", Q16 "Toda la pantalla").** While a 429's wait for
 *   any of the screen's operations lasts, {@link admit} refuses every read (polling, visibility, realtime, buttons,
 *   filter changes) and keeps one reload instead; a read that met the 429 keeps it too ({@link rateLimited}). When
 *   the wait ends the screen reloads once, by itself, with what is on screen then (its filters included).
 *
 * Nothing is stored outside memory; {@link cancel} drops what is pending and its timer.
 */
export class ScreenReloads<Kind extends string> {
  private lastReload = Number.NEGATIVE_INFINITY;
  private pending: Kind | null = null;
  /** Only realtime notices are spaced; the reload at the end of the server's wait runs when it ends. */
  private pendingSpaced = false;
  private dueAt = 0;
  private timer: unknown = null;
  private reload: (kind: Kind) => void;
  private readonly timers: ReloadTimers;

  public constructor(private readonly options: ScreenReloadsOptions<Kind>) {
    this.reload = options.reload ?? (() => undefined);
    this.timers = { ...defaultTimers, ...options.timers };
  }

  public setReload(reload: (kind: Kind) => void): void {
    this.reload = reload;
  }

  /** The server's wait for one of the screen's operations is running. */
  public get waiting(): boolean {
    return this.waitLeft() > 0;
  }

  /** One reload is kept for later (after the spacing or the server's wait). */
  public get hasPending(): boolean {
    return this.pending !== null;
  }

  /** A realtime notice asks for a reload after at least `debounce` milliseconds (Q9). */
  public notice(kind: Kind, debounce = 0): void {
    this.request(kind, debounce, true);
  }

  /**
   * May the screen read now? Not during the server's wait: then one reload is kept for when it ends, and the read is
   * not sent (Q11, Q14).
   */
  public admit(kind: Kind): boolean {
    if (!this.waiting) return true;
    this.request(kind, 0, false);
    return false;
  }

  /**
   * A read of the screen met a 429 (the API client recorded its wait): the screen reloads once when it ends. Without a
   * running wait for the screen's operations nothing is kept, so a 429 that is not this screen's limit can never make
   * it reload in a loop.
   */
  public rateLimited(kind: Kind): void {
    if (!this.waiting) return;
    this.request(kind, 0, false);
  }

  /** Every reload the screen starts, whatever asked for it, counts for the spacing. */
  public reloaded(): void {
    this.lastReload = this.timers.now();
  }

  /** Session change, lost access or leaving the screen: nothing pending runs and no timer is left. */
  public cancel(): void {
    this.pending = null;
    this.disarm();
  }

  private request(kind: Kind, delay: number, spaced: boolean): void {
    const now = this.timers.now();
    if (this.pending !== null) {
      this.pending = this.options.stronger?.(this.pending, kind) ?? kind;
      if (spaced || !this.pendingSpaced) return;
      // A read the server's wait held joins a pending notice: the one reload runs when the wait ends (Q11).
      this.pendingSpaced = false;
      this.dueAt = Math.min(this.dueAt, now);
      this.arm();
      return;
    }
    this.pending = kind;
    this.pendingSpaced = spaced;
    this.dueAt = Math.max(now + Math.max(0, delay), spaced ? this.lastReload + this.spacing() : now);
    this.arm();
  }

  private arm(): void {
    this.disarm();
    const now = this.timers.now();
    const due = Math.max(this.dueAt, now + this.waitLeft());
    this.timer = this.timers.setTimer(() => this.fire(), Math.max(0, due - now));
  }

  private fire(): void {
    this.timer = null;
    const kind = this.pending;
    if (kind === null) return;
    // The wait may have grown (another 429), or a reload may have started meanwhile: wait for both again.
    const spacedUntil = this.pendingSpaced ? this.lastReload + this.spacing() : 0;
    if (this.waiting || this.timers.now() < spacedUntil) {
      this.dueAt = Math.max(this.dueAt, spacedUntil);
      this.arm();
      return;
    }
    this.pending = null;
    this.reload(kind);
  }

  private disarm(): void {
    if (this.timer === null) return;
    this.timers.clearTimer(this.timer);
    this.timer = null;
  }

  private waitLeft(): number {
    return (this.options.waits ?? rateLimitWaits).millisecondsLeft(this.options.operations);
  }

  private spacing(): number {
    return this.options.spacing ?? realtimeReloadSpacingMilliseconds;
  }
}
