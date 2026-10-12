import {
  DriverDispatchContactApiError,
  type DriverDispatchContactApi,
} from "../api/dispatch-contact-api";
import type { DriverDispatchContactCache } from "../cache/driver-dispatch-contact-cache";
import {
  createDriverCachePartition,
  type DriverCachePartition,
} from "../cache/driver-stops-cache";
import type { DriverSession } from "../session/driver-session";

/**
 * DRV-DISPATCH-CONTACT-2026-10-12: the dispatch number the phone knows for the
 * session's organization - the answer of getMyDispatchContact of this page, or
 * else the one saved for the session's partition. null while none is known or
 * when the organization has none: then no "Llamar a despacho" is shown.
 */
export interface DriverDispatchContactState {
  readonly phoneE164: string | null;
}

/** The most extra requests one session start makes after transient failures (as for the roles). */
export const DriverDispatchContactMaximumRetries = 5;

export interface DriverDispatchContactControllerOptions {
  readonly session: DriverSession;
  readonly api: DriverDispatchContactApi;
  readonly cache: DriverDispatchContactCache;
  /** Defaults to {@link DriverDispatchContactMaximumRetries}. */
  readonly maximumRetries?: number;
  /** The page whose `online` and `visibilitychange` events retry; defaults to `window`. */
  readonly window?: Pick<
    Window,
    "addEventListener" | "removeEventListener" | "navigator" | "document"
  >;
}

const noNumber: DriverDispatchContactState = Object.freeze({ phoneE164: null });

/**
 * DRV-DISPATCH-CONTACT-2026-10-12 (project owner, "Sí, configurable
 * (Recommended)"): asks the API for the dispatch number of the active
 * organization when the driver session starts (a new sign-in or organization
 * is a new session and a new partition). The answer saved for the partition
 * is used at once, so "Llamar a despacho" also works offline, and a successful
 * answer (a number or null) replaces it. 401 or 403 removes it (the person is
 * no longer a driver there; the stops screen clears the rest of the partition
 * on its own revocation) and nothing is asked again. Any other failure -
 * offline, a timeout, a 5xx, an older API without the operation (404) or an
 * answer that breaks the contract - keeps what is saved, or no number when
 * nothing is, and the request is made again when the connection returns
 * (`online`) or the page becomes visible again, at most
 * {@link DriverDispatchContactMaximumRetries} times and never after dispose.
 * Without a known number nothing is shown, so a failure is never an error
 * message.
 */
export class DriverDispatchContactController {
  private readonly listeners = new Set<(state: DriverDispatchContactState) => void>();
  private readonly browser: DriverDispatchContactControllerOptions["window"];
  private readonly maximumRetries: number;
  private state: DriverDispatchContactState = noNumber;
  private partition: DriverCachePartition | null = null;
  private abortController: AbortController | null = null;
  private request: Promise<void> | null = null;
  /** Whether a later request may still change the answer: only after a transient failure. */
  private retryable = false;
  private retries = 0;
  private started = false;
  private disposed = false;

  public constructor(private readonly options: DriverDispatchContactControllerOptions) {
    this.maximumRetries = options.maximumRetries ?? DriverDispatchContactMaximumRetries;
    this.browser =
      options.window ?? (typeof window === "undefined" ? undefined : window);
  }

  public get current(): DriverDispatchContactState {
    return this.state;
  }

  public subscribe(listener: (state: DriverDispatchContactState) => void): () => void {
    this.listeners.add(listener);
    listener(this.state);
    return () => this.listeners.delete(listener);
  }

  /** Runs once per controller; resolves when the first request has ended. */
  public async start(): Promise<void> {
    if (this.started || this.disposed) return;
    this.started = true;
    this.browser?.addEventListener("online", this.handleOnline);
    this.browser?.document.addEventListener(
      "visibilitychange",
      this.handleVisibility,
    );
    let partition: DriverCachePartition;
    try {
      partition = await createDriverCachePartition(this.options.session);
    } catch {
      return;
    }
    let saved: string | null = null;
    try {
      saved = (await this.options.cache.readContact(partition))?.phoneE164 ?? null;
    } catch {
      saved = null;
    }
    if (this.disposed) return;
    this.partition = partition;
    if (saved !== null) this.setState(Object.freeze({ phoneE164: saved }));
    await this.ask();
  }

  public dispose(): void {
    this.disposed = true;
    this.retryable = false;
    this.abortController?.abort();
    this.browser?.removeEventListener("online", this.handleOnline);
    this.browser?.document.removeEventListener(
      "visibilitychange",
      this.handleVisibility,
    );
    this.listeners.clear();
  }

  private readonly handleOnline = () => {
    void this.retry();
  };

  private readonly handleVisibility = () => {
    if (this.browser?.document.visibilityState !== "hidden") void this.retry();
  };

  private retry(): Promise<void> {
    if (
      this.disposed ||
      !this.retryable ||
      this.request !== null ||
      this.retries >= this.maximumRetries ||
      this.browser?.navigator.onLine === false ||
      this.browser?.document.visibilityState === "hidden"
    ) {
      return Promise.resolve();
    }
    this.retries += 1;
    return this.ask();
  }

  /** One request at a time; a retry while one is on its way does nothing. */
  private ask(): Promise<void> {
    if (this.request !== null) return this.request;
    const pending = this.askOnce().finally(() => {
      if (this.request === pending) this.request = null;
    });
    this.request = pending;
    return pending;
  }

  private async askOnce(): Promise<void> {
    const partition = this.partition;
    if (partition === null || this.disposed) return;
    this.retryable = false;
    const request = new AbortController();
    this.abortController = request;
    try {
      const phoneE164 = await this.options.api.getMyDispatchContact(request.signal);
      if (this.disposed) return;
      this.setState(phoneE164 === null ? noNumber : Object.freeze({ phoneE164 }));
      await this.options.cache
        .replaceContact(partition, phoneE164)
        .catch(() => undefined);
    } catch (error) {
      if (this.disposed) return;
      if (
        error instanceof DriverDispatchContactApiError &&
        (error.category === "unauthorized" || error.category === "forbidden")
      ) {
        this.setState(noNumber);
        await this.options.cache.clearContact(partition).catch(() => undefined);
        return;
      }
      this.retryable = true;
    } finally {
      if (this.abortController === request) this.abortController = null;
    }
  }

  private setState(next: DriverDispatchContactState): void {
    this.state = next;
    for (const listener of this.listeners) listener(next);
  }
}
