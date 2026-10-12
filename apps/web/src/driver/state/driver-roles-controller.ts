import {
  DriverOrganizationRolesApiError,
  type DriverOrganizationRolesApi,
} from "../api/organization-roles-api";
import type { DriverOrganizationRolesCache } from "../cache/driver-roles-cache";
import {
  createDriverCachePartition,
  type DriverCachePartition,
} from "../cache/driver-stops-cache";
import type { DriverSession } from "../session/driver-session";

export interface DriverRolesState {
  /**
   * ORG-MY-ROLES-2026-10-12: the person's own roles in the session's
   * organization - the answer of getMyOrganizationRoles of this start, or else
   * the last one saved for the session's partition; null while none is known
   * (driverReportRole then says "unknown" and the report is not offered).
   */
  readonly roles: readonly string[] | null;
}

export interface DriverRolesControllerOptions {
  readonly session: DriverSession;
  readonly api: DriverOrganizationRolesApi;
  readonly cache: DriverOrganizationRolesCache;
}

const unknownRoles: DriverRolesState = Object.freeze({ roles: null });

/**
 * ORG-MY-ROLES-2026-10-12 (project owner, "Endpoint nuevo de roles
 * (Recommended)"): asks the API for the person's own roles in the active
 * organization once per start of the session (a new sign-in or organization
 * is a new session). The answer saved for the partition is used at once, so
 * "No se pudo entregar" keeps working offline for a driver, and a successful
 * answer replaces it. 401 or 403 removes it (the person is no longer a member
 * there; the stops screen clears the rest of the partition on its own
 * revocation). Any other failure - offline, a timeout, a 5xx, an older API
 * without the operation (404) or an answer that breaks the contract - keeps
 * the saved answer, or leaves the roles unknown when none is saved.
 */
export class DriverRolesController {
  private readonly listeners = new Set<(state: DriverRolesState) => void>();
  private state: DriverRolesState = unknownRoles;
  private abortController: AbortController | null = null;
  private disposed = false;

  public constructor(private readonly options: DriverRolesControllerOptions) {}

  public get current(): DriverRolesState {
    return this.state;
  }

  public subscribe(listener: (state: DriverRolesState) => void): () => void {
    this.listeners.add(listener);
    listener(this.state);
    return () => this.listeners.delete(listener);
  }

  public async start(): Promise<void> {
    let partition: DriverCachePartition;
    try {
      partition = await createDriverCachePartition(this.options.session);
    } catch {
      return;
    }
    let saved: readonly string[] | null = null;
    try {
      saved = await this.options.cache.readRoles(partition);
    } catch {
      saved = null;
    }
    if (this.disposed) return;
    if (saved !== null) this.setState(Object.freeze({ roles: saved }));

    const request = new AbortController();
    this.abortController = request;
    try {
      const roles = await this.options.api.getMyRoles(request.signal);
      if (this.disposed) return;
      this.setState(Object.freeze({ roles }));
      await this.options.cache.replaceRoles(partition, roles).catch(() => undefined);
    } catch (error) {
      if (this.disposed) return;
      if (
        error instanceof DriverOrganizationRolesApiError &&
        (error.category === "unauthorized" || error.category === "forbidden")
      ) {
        this.setState(unknownRoles);
        await this.options.cache.clearRoles(partition).catch(() => undefined);
      }
    } finally {
      if (this.abortController === request) this.abortController = null;
    }
  }

  public dispose(): void {
    this.disposed = true;
    this.abortController?.abort();
    this.listeners.clear();
  }

  private setState(next: DriverRolesState): void {
    this.state = next;
    for (const listener of this.listeners) listener(next);
  }
}
