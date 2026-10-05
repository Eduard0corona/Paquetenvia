import type { OperationsOrganizationContext } from "../operations/contracts/operations-dashboard";
import { parseOrganizationContexts } from "../operations/contracts/operations-parsers";
import type { DriverSession } from "../driver/session/driver-session";
import { driverSessionChangedEvent } from "../driver/session/driver-session";
import type { OperationsSession } from "../operations/session/operations-session";
import { operationsSessionChangedEvent } from "../operations/session/operations-session";
import { fetchBffSession, type BffSessionState } from "./bff-session";
import type { SessionAccount } from "./session-account";

export type BffSessionInstallation =
  | {
      readonly kind: "operations" | "driver";
      readonly organizationId: string;
      readonly displayName: string;
      /** Where "Continuar" sends the person after login; see {@link landingPathForRole}. */
      readonly landingPath: string;
    }
  | { readonly kind: "none" };

/**
 * First screen for a membership role after login. FINANCE works from the COD and settlement
 * screens, DRIVER from its stops; every other role starts on the operations dashboard. This
 * only picks a page: each screen still asks the API what the role may do.
 */
export function landingPathForRole(role: string): string {
  if (role === "DRIVER") return "/driver/stops";
  if (role === "FINANCE") return "/finance/cod";
  return "/ops/dashboard";
}

export interface SessionHost {
  __paquetenviaOperationsSession?: OperationsSession;
  __paquetenviaDriverSession?: DriverSession;
  __paquetenviaAccount?: SessionAccount;
  dispatchEvent(event: Event): boolean;
}

/**
 * Chooses the tenant context to activate after a BFF login. The API still
 * validates `X-Organization-Id` against the memberships it resolved from the
 * database; this only picks a sensible default for the UI.
 */
export function selectSessionInstallation(
  contexts: readonly OperationsOrganizationContext[],
  preferredOrganizationId?: string | null,
): BffSessionInstallation {
  const selected =
    contexts.find((context) => context.organization_id === preferredOrganizationId) ??
    contexts.find((context) => context.is_default) ??
    contexts[0];
  if (selected === undefined) {
    return { kind: "none" };
  }
  return {
    kind: selected.role === "DRIVER" ? "driver" : "operations",
    organizationId: selected.organization_id,
    displayName: selected.display_name,
    landingPath: landingPathForRole(selected.role),
  };
}

export interface InstallOptions {
  /** Shown by the app shell and the driver account area; kept in memory only. */
  readonly account?: SessionAccount;
  /**
   * Switches the active organization (app shell selector). It re-runs the bootstrap with
   * the chosen organization, so every screen clears its tenant state on the
   * session-changed events exactly as on a new sign-in.
   */
  readonly onOrganizationChange?: (organizationId: string) => Promise<void>;
}

export function installBffSession(
  host: SessionHost,
  installation: BffSessionInstallation,
  csrfToken: string,
  sessionNamespace: string,
  options: InstallOptions = {},
): void {
  clearInstalledSessions(host);
  if (installation.kind === "none") {
    return;
  }
  host.__paquetenviaAccount = options.account ?? { displayName: null };
  const credentials = {
    credentialMode: "cookie" as const,
    getCsrfToken: () => csrfToken,
  };
  if (installation.kind === "driver") {
    host.__paquetenviaDriverSession = {
      organizationId: installation.organizationId,
      cacheNamespace: sessionNamespace,
      ...credentials,
    };
    host.dispatchEvent(new Event(driverSessionChangedEvent));
    return;
  }
  host.__paquetenviaOperationsSession = {
    organizationId: installation.organizationId,
    sessionNamespace,
    ...credentials,
    ...(options.onOrganizationChange === undefined
      ? {}
      : { requestOrganizationChange: options.onOrganizationChange }),
  };
  host.dispatchEvent(new Event(operationsSessionChangedEvent));
}

export function clearInstalledSessions(host: SessionHost): void {
  const hadSession =
    host.__paquetenviaOperationsSession !== undefined ||
    host.__paquetenviaDriverSession !== undefined;
  delete host.__paquetenviaOperationsSession;
  delete host.__paquetenviaDriverSession;
  delete host.__paquetenviaAccount;
  if (hadSession) {
    host.dispatchEvent(new Event(operationsSessionChangedEvent));
    host.dispatchEvent(new Event(driverSessionChangedEvent));
  }
}

export async function fetchOrganizationContexts(
  fetcher: typeof fetch = fetch,
): Promise<readonly OperationsOrganizationContext[] | null> {
  try {
    const response = await fetcher("/api/v1/me/organization-contexts", {
      method: "GET",
      credentials: "include",
      cache: "no-store",
      headers: { Accept: "application/json" },
    });
    if (!response.ok) {
      return null;
    }
    return parseOrganizationContexts(await response.json());
  } catch {
    return null;
  }
}

export interface BffBootstrapResult {
  readonly session: BffSessionState;
  readonly installation: BffSessionInstallation;
}

/** Runs once per page load in BFF mode: session → organization contexts → in-memory session objects. */
export async function bootstrapBffSession(
  host: SessionHost,
  fetcher: typeof fetch = fetch,
  preferredOrganizationId?: string | null,
): Promise<BffBootstrapResult> {
  const session = await fetchBffSession(fetcher);
  if (session.status !== "authenticated" || !session.authorized) {
    clearInstalledSessions(host);
    return { session, installation: { kind: "none" } };
  }
  const contexts = await fetchOrganizationContexts(fetcher);
  const installation =
    contexts === null
      ? ({ kind: "none" } as const)
      : selectSessionInstallation(contexts, preferredOrganizationId);
  installBffSession(host, installation, session.csrfToken, session.sessionNamespace, {
    account: { displayName: session.user.name },
    onOrganizationChange: async (organizationId) => {
      await bootstrapBffSession(host, fetcher, organizationId);
    },
  });
  return { session, installation };
}
