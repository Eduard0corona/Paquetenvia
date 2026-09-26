import type { OperationsOrganizationContext } from "../operations/contracts/operations-dashboard";
import { parseOrganizationContexts } from "../operations/contracts/operations-parsers";
import type { DriverSession } from "../driver/session/driver-session";
import { driverSessionChangedEvent } from "../driver/session/driver-session";
import type { OperationsSession } from "../operations/session/operations-session";
import { operationsSessionChangedEvent } from "../operations/session/operations-session";
import { fetchBffSession, type BffSessionState } from "./bff-session";

export type BffSessionInstallation =
  | {
      readonly kind: "operations" | "driver";
      readonly organizationId: string;
      readonly displayName: string;
    }
  | { readonly kind: "none" };

export interface SessionHost {
  __paquetenviaOperationsSession?: OperationsSession;
  __paquetenviaDriverSession?: DriverSession;
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
  };
}

export function installBffSession(
  host: SessionHost,
  installation: BffSessionInstallation,
  csrfToken: string,
  sessionNamespace: string,
): void {
  clearInstalledSessions(host);
  if (installation.kind === "none") {
    return;
  }
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
  };
  host.dispatchEvent(new Event(operationsSessionChangedEvent));
}

export function clearInstalledSessions(host: SessionHost): void {
  const hadSession =
    host.__paquetenviaOperationsSession !== undefined ||
    host.__paquetenviaDriverSession !== undefined;
  delete host.__paquetenviaOperationsSession;
  delete host.__paquetenviaDriverSession;
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
  installBffSession(host, installation, session.csrfToken, session.sessionNamespace);
  return { session, installation };
}
