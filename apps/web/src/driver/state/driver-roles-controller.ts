import {
  DriverOrganizationRolesApiError,
  type DriverOrganizationRolesApi,
} from "../api/organization-roles-api";
import type { DriverOrganizationRolesCache } from "../cache/driver-roles-cache";
import {
  createDriverCachePartition,
  type DriverCachePartition,
} from "../cache/driver-stops-cache";
import type { DriverOrganizationRolesState } from "../contracts/organization-roles";
import type { DriverSession } from "../session/driver-session";

/**
 * ORG-MY-ROLES-2026-10-12: the person's own roles in the session's
 * organization - the answer of getMyOrganizationRoles of this page, or else
 * the last one saved for the session's partition; null while none is known.
 * `loading` is true while a request for them is on its way (driverReportRoleOf
 * then says "loading" and the stop says nothing about the role).
 */
export type DriverRolesState = DriverOrganizationRolesState;

/** The most extra requests one session start makes after transient failures. */
export const DriverRolesMaximumRetries = 5;

export interface DriverRolesControllerOptions {
  readonly session: DriverSession;
  readonly api: DriverOrganizationRolesApi;
  readonly cache: DriverOrganizationRolesCache;
  /** Defaults to {@link DriverRolesMaximumRetries}. */
  readonly maximumRetries?: number;
  /** The page whose `online` and `visibilitychange` events retry; defaults to `window`. */
  readonly window?: Pick<
    Window,
    "addEventListener" | "removeEventListener" | "navigator" | "document"
  >;
}

const loadingRoles: DriverRolesState = Object.freeze({
  roles: null,
  loading: true,
});

const unknownRoles: DriverRolesState = Object.freeze({
  roles: null,
  loading: false,
});

/**
 * ORG-MY-ROLES-2026-10-12 (project owner, "Endpoint nuevo de roles
 * (Recommended)"): asks the API for the person's own roles in the active
 * organization when the session starts (a new sign-in or organization is a new
 * session). The answer saved for the partition is used at once, so
 * "No se pudo entregar" keeps working offline for a driver, and a successful
 * answer replaces it. 401 or 403 removes it (the person is no longer a member
 * there; the stops screen clears the rest of the partition on its own
 * revocation) and nothing is asked again. Any other failure - offline, a
 * timeout, a 5xx, an older API without the operation (404) or an answer that
 * breaks the contract - keeps the saved answer, or leaves the roles unknown
 * when none is saved, and the request is made again when the connection
 * returns (`online`) or the page becomes visible again, at most
 * {@link DriverRolesMaximumRetries} times and never after dispose.
 */
export class DriverRolesController {
  private readonly listeners = new Set<(state: DriverRolesState) => void>();
  private readonly browser: DriverRolesControllerOptions["window"];
  private readonly maximumRetries: number;
  private state: DriverRolesState = loadingRoles;
  private partition: DriverCachePartition | null = null;
  private abortController: AbortController | null = null;
  private request: Promise<void> | null = null;
  /** Whether a later request may still change the answer: only after a transient failure. */
  private retryable = false;
  private retries = 0;
  private started = false;
  private disposed = false;

  public constructor(private readonly options: DriverRolesControllerOptions) {
    this.maximumRetries = options.maximumRetries ?? DriverRolesMaximumRetries;
    this.browser =
      options.window ?? (typeof window === "undefined" ? undefined : window);
  }

  public get current(): DriverRolesState {
    return this.state;
  }

  public subscribe(listener: (state: DriverRolesState) => void): () => void {
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
      if (!this.disposed) this.setState(unknownRoles);
      return;
    }
    let saved: readonly string[] | null = null;
    try {
      saved = await this.options.cache.readRoles(partition);
    } catch {
      saved = null;
    }
    if (this.disposed) return;
    this.partition = partition;
    if (saved !== null) this.setState(Object.freeze({ roles: saved, loading: true }));
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
    if (!this.state.loading) {
      this.setState(Object.freeze({ roles: this.state.roles, loading: true }));
    }
    const request = new AbortController();
    this.abortController = request;
    try {
      const roles = await this.options.api.getMyRoles(request.signal);
      if (this.disposed) return;
      this.setState(Object.freeze({ roles, loading: false }));
      await this.options.cache.replaceRoles(partition, roles).catch(() => undefined);
    } catch (error) {
      if (this.disposed) return;
      if (
        error instanceof DriverOrganizationRolesApiError &&
        (error.category === "unauthorized" || error.category === "forbidden")
      ) {
        this.setState(unknownRoles);
        await this.options.cache.clearRoles(partition).catch(() => undefined);
        return;
      }
      this.retryable = true;
      this.setState(Object.freeze({ roles: this.state.roles, loading: false }));
    } finally {
      if (this.abortController === request) this.abortController = null;
    }
  }

  private setState(next: DriverRolesState): void {
    this.state = next;
    for (const listener of this.listeners) listener(next);
  }
}
