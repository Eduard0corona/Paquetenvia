/**
 * SEC-004-RELOADS-AND-BULK-2026-10-13, owner answer Q9 "Espaciar recargas (Recommended)": "El panel junta los avisos
 * y recarga como máximo cada 3 segundos por pestaña. Los límites de 60/min no cambian. Los cambios se ven hasta 3
 * segundos después."
 *
 * One spacer per screen, and a tab shows one screen at a time. Every realtime notice asks for a reload; the notices
 * are gathered into one reload, which runs once its own short wait has passed (the screen's existing debounce), no
 * sooner than 3 seconds after the screen's previous reload of any kind, and not while the server's wait (SEC-004,
 * a 429's Retry-After) for the operations it reads lasts. A reload that a background trigger skipped during such a
 * wait asks the spacer too, so the screen catches up once, by itself, when the wait ends.
 */
export const realtimeReloadSpacingMilliseconds = 3_000;

export interface ReloadSpacerOptions<Kind extends string> {
  /** Starts one reload of the screen (a React hook sets it later, from an effect, with `setReload`). */
  readonly reload?: (kind: Kind) => void;
  /** Milliseconds left of the server's wait for the screen's operations; 0 when none. */
  readonly waitLeft?: () => number;
  /** Of two pending kinds, the one the gathered reload uses (for example the one that also reads the counts). */
  readonly stronger?: (left: Kind, right: Kind) => Kind;
  readonly now?: () => number;
  readonly setTimer?: (callback: () => void, delay: number) => unknown;
  readonly clearTimer?: (timer: unknown) => void;
  readonly spacing?: number;
}

export class ReloadSpacer<Kind extends string> {
  private lastReload = Number.NEGATIVE_INFINITY;
  private pending: Kind | null = null;
  private dueAt = 0;
  private timer: unknown = null;
  private reload: (kind: Kind) => void;

  public constructor(private readonly options: ReloadSpacerOptions<Kind>) {
    this.reload = options.reload ?? (() => undefined);
  }

  /** The screen's current reload; what is pending runs it. */
  public setReload(reload: (kind: Kind) => void): void {
    this.reload = reload;
  }

  /**
   * A notice (or a background reload skipped during the server's wait) asks for a reload after at least `delay`
   * milliseconds. A reload already pending takes it in and keeps its time, so a steady stream of notices still
   * reloads every 3 seconds instead of postponing the reload.
   */
  public request(kind: Kind, delay = 0): void {
    if (this.pending !== null) {
      this.pending = this.options.stronger?.(this.pending, kind) ?? kind;
      return;
    }
    this.pending = kind;
    this.dueAt = Math.max(this.now() + Math.max(0, delay), this.lastReload + this.spacing());
    this.arm();
  }

  /** Every reload the screen starts, whatever asked for it, counts for the spacing. */
  public reloaded(): void {
    this.lastReload = this.now();
  }

  /** Session change, lost access or leaving the screen: nothing pending runs. */
  public cancel(): void {
    this.pending = null;
    this.disarm();
  }

  public get hasPending(): boolean {
    return this.pending !== null;
  }

  private arm(): void {
    this.disarm();
    const due = Math.max(this.dueAt, this.now() + (this.options.waitLeft?.() ?? 0));
    const set = this.options.setTimer ?? ((callback, delay) => setTimeout(callback, delay));
    this.timer = set(() => this.fire(), Math.max(0, due - this.now()));
  }

  private fire(): void {
    this.timer = null;
    const kind = this.pending;
    if (kind === null) return;
    // The server's wait may have grown since the timer was set: wait for it again.
    if ((this.options.waitLeft?.() ?? 0) > 0 || this.now() < this.lastReload + this.spacing()) {
      this.dueAt = Math.max(this.dueAt, this.lastReload + this.spacing());
      this.arm();
      return;
    }
    this.pending = null;
    this.reload(kind);
  }

  private disarm(): void {
    if (this.timer === null) return;
    const clear = this.options.clearTimer ?? ((timer) => clearTimeout(timer as ReturnType<typeof setTimeout>));
    clear(this.timer);
    this.timer = null;
  }

  private spacing(): number {
    return this.options.spacing ?? realtimeReloadSpacingMilliseconds;
  }

  private now(): number {
    return this.options.now?.() ?? Date.now();
  }
}
