import type { DriversApi } from "../api/drivers-api";
import { TenantApiError } from "../api/tenant-request";
import { canPerform } from "../contracts/capabilities";
import {
  sortByReference,
  type OrganizationDriver,
  type OrganizationDriverPage,
} from "../contracts/organization-driver";
import type { OperationsSession } from "../session/operations-session";
import {
  AuthoritativeRefreshCoordinator,
  RefreshScopeChangedError,
} from "./authoritative-refresh-coordinator";
import { ExternalStore } from "./external-store";
import { describeFailure } from "./tenant-error-messages";

/**
 * UI-PHASE3-DRIVERS-2026-10-11 ("Lista de consulta"): state of the dispatcher's read-only "Repartidores" screen.
 *
 * REST is the only source of truth: every read replaces the list with the first page listOrganizationDrivers
 * returns, "Cargar más" appends the next one, and the page becoming visible or focused again only asks for such a
 * read. There are no actions. Everything lives in memory and a session or organization change starts from an
 * empty state.
 */
export const driversPath = "/ops/drivers";

export type DriversTrigger = "initial" | "manual" | "visibility" | "focus";

/** Background reads wait while the page is hidden and never repeat a read this recent. */
const backgroundTriggers: ReadonlySet<DriversTrigger> = new Set(["visibility", "focus"]);
export const backgroundRefreshIntervalMilliseconds = 5_000;

export const driversLoadFailedMessage = "No fue posible actualizar la lista de repartidores.";
export const driversPaginationFailedMessage = "No fue posible cargar más repartidores. Actualiza la lista.";

export interface DriversState {
  readonly phase: "no_session" | "loading" | "ready" | "access_unavailable";
  readonly role: string | null;
  /** The drivers the API returned in this session, by reference. */
  readonly drivers: readonly OrganizationDriver[];
  readonly nextCursor: string | null;
  /** The list was answered at least once (tells an empty fleet from a pending read). */
  readonly loaded: boolean;
  readonly loading: boolean;
  readonly loadingMore: boolean;
  /** The last read failed; the rows already shown stay. */
  readonly error: string | null;
  /** A refusal that only a second factor would lift, with its step-up link. */
  readonly message: string | null;
  readonly stepUpHref: string | null;
  /** When the last read was answered (device clock, shown in Mazatlán time). */
  readonly lastUpdated: string | null;
}

export interface DriversDependencies {
  readonly readSession: () => OperationsSession | null;
  readonly createApi: (session: OperationsSession) => DriversApi;
  readonly loadRole: (session: OperationsSession, signal: AbortSignal) => Promise<string | null>;
  readonly isHidden?: () => boolean;
  readonly now?: () => number;
}

const initialState: DriversState = {
  phase: "no_session",
  role: null,
  drivers: [],
  nextCursor: null,
  loaded: false,
  loading: false,
  loadingMore: false,
  error: null,
  message: null,
  stepUpHref: null,
  lastUpdated: null,
};

export class DriversController extends ExternalStore<DriversState> {
  private api: DriversApi | null = null;
  private generation = 0;
  private controller: AbortController | null = null;
  private coordinator = new AuthoritativeRefreshCoordinator<OrganizationDriverPage>();
  private seenCursors = new Set<string>();
  private lastReadAt = Number.NEGATIVE_INFINITY;

  public constructor(private readonly dependencies: DriversDependencies) {
    super(initialState);
  }

  /** Starts (again) for the installed session: drops everything of the previous one, then reads the list. */
  public async start(): Promise<void> {
    this.reset();
    const generation = this.generation;
    const session = this.dependencies.readSession();
    this.api = session === null ? null : this.dependencies.createApi(session);
    this.update({ ...initialState, phase: session === null ? "no_session" : "loading" });
    if (session === null) return;
    let role: string | null;
    try {
      role = await this.dependencies.loadRole(session, this.controller!.signal);
    } catch {
      role = null;
    }
    if (generation !== this.generation) return;
    if (!canPerform(role, "listOrganizationDrivers")) {
      this.update({ phase: "access_unavailable", role });
      return;
    }
    this.update({ phase: "ready", role });
    await this.refresh("initial");
  }

  /** Leaves the screen: cancels every request and forgets the session's data. */
  public stop(): void {
    this.reset();
    this.api = null;
    this.update(initialState);
  }

  /** Reads the first page again and replaces the list with it. */
  public async refresh(trigger: DriversTrigger = "manual"): Promise<void> {
    const api = this.api;
    const state = this.getSnapshot();
    if (api === null || state.phase !== "ready") return;
    if (backgroundTriggers.has(trigger)) {
      if (this.dependencies.isHidden?.() ?? false) return;
      if (state.loading || this.now() - this.lastReadAt < backgroundRefreshIntervalMilliseconds) return;
    }
    const generation = this.generation;
    const coordinator = this.coordinator;
    this.update({ loading: true });
    try {
      await coordinator.request({
        requirement: "normal",
        isCurrent: () => generation === this.generation && this.api === api,
        execute: (signal) => api.list(null, signal),
        apply: (page) => {
          this.seenCursors = new Set(page.next_cursor === null ? [] : [page.next_cursor]);
          this.update({
            drivers: sortByReference(page.items),
            nextCursor: page.next_cursor,
            loaded: true,
            error: null,
            message: null,
            stepUpHref: null,
            lastUpdated: new Date(this.now()).toISOString(),
          });
        },
      });
    } catch (error: unknown) {
      if (error instanceof RefreshScopeChangedError || generation !== this.generation) return;
      this.fail(error, driversLoadFailedMessage);
    } finally {
      if (generation === this.generation) {
        this.lastReadAt = this.now();
        this.update({ loading: false });
      }
    }
  }

  /** Appends the next page; a cursor the list already followed stops the paging with a message. */
  public async loadMore(): Promise<void> {
    const api = this.api;
    const state = this.getSnapshot();
    if (api === null || state.phase !== "ready" || state.nextCursor === null || state.loadingMore) return;
    const generation = this.generation;
    this.update({ loadingMore: true });
    try {
      const page = await api.list(state.nextCursor, this.controller?.signal);
      if (generation !== this.generation) return;
      if (page.next_cursor !== null && this.seenCursors.has(page.next_cursor)) {
        this.update({ nextCursor: null, error: driversPaginationFailedMessage });
        return;
      }
      if (page.next_cursor !== null) this.seenCursors.add(page.next_cursor);
      const known = new Set(this.getSnapshot().drivers.map((driver) => driver.driver_id));
      this.update({
        drivers: sortByReference([
          ...this.getSnapshot().drivers,
          ...page.items.filter((driver) => !known.has(driver.driver_id)),
        ]),
        nextCursor: page.next_cursor,
        error: null,
      });
    } catch (error: unknown) {
      if (generation === this.generation) this.fail(error, driversPaginationFailedMessage);
    } finally {
      if (generation === this.generation) this.update({ loadingMore: false });
    }
  }

  private fail(error: unknown, readFailed: string): void {
    if (error instanceof TenantApiError && (error.category === "unauthorized" || error.category === "forbidden")) {
      if (error.mfaRequired) {
        // Only the second factor is missing: nothing is shown until it is verified.
        const view = describeFailure(error, driversPath);
        this.update({ drivers: [], nextCursor: null, error: null, message: view.message, stepUpHref: view.stepUpHref });
        return;
      }
      // Lost access clears everything, as on the other operations lists.
      this.reset();
      this.api = null;
      this.update({ ...initialState, phase: "access_unavailable", role: this.getSnapshot().role });
      return;
    }
    this.update({ error: readFailed });
  }

  private reset(): void {
    this.generation += 1;
    this.controller?.abort();
    this.controller = new AbortController();
    this.coordinator.cancel();
    this.coordinator = new AuthoritativeRefreshCoordinator<OrganizationDriverPage>();
    this.seenCursors = new Set();
    this.lastReadAt = Number.NEGATIVE_INFINITY;
  }

  private now(): number {
    return this.dependencies.now?.() ?? Date.now();
  }
}
