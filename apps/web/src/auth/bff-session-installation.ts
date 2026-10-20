import { canOpenOrderList, canOpenWorkInbox } from "../operations/contracts/capabilities";
import type { OperationsOrganizationContext } from "../operations/contracts/operations-dashboard";
import { parseOrganizationContexts } from "../operations/contracts/operations-parsers";
import type { DriverSession } from "../driver/session/driver-session";
import { driverSessionChangedEvent } from "../driver/session/driver-session";
import {
  clearDriverSessionMarker,
  readDriverSessionMarker,
  writeDriverSessionMarker,
  type DriverSessionMarkerHost,
} from "../driver/session/driver-session-marker";
import type { OperationsSession } from "../operations/session/operations-session";
import { operationsSessionChangedEvent } from "../operations/session/operations-session";
import { fetchBffSession, type BffSessionState } from "./bff-session";
import { MissingCredentialError } from "./request-credentials";
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
 * First screen for a membership role after login. DISPATCHER and PLATFORM_ADMIN land on the
 * work inbox (UI-PHASE3-INBOX-2026-10-10), FINANCE works from the COD and settlement screens,
 * DRIVER from its stops, and VIEWER, which lists orders but has no inbox, on "Órdenes"
 * (UI-PHASE3-ORDERS-LIST-2026-10-12); every other role starts on the operations dashboard.
 * This only picks a page: each screen still asks the API what the role may do.
 */
export function landingPathForRole(role: string): string {
  if (role === "DRIVER") return "/driver/stops";
  if (role === "FINANCE") return "/finance/cod";
  if (canOpenWorkInbox(role)) return "/ops/inbox";
  if (canOpenOrderList(role)) return "/ops/orders";
  return "/ops/dashboard";
}

export interface SessionHost extends DriverSessionMarkerHost {
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
    // Signed in, with no organization to open: no driver is remembered.
    clearDriverSessionMarker(host);
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
    // DRV-SESSION-EXPIRY-OFFLINE-2026-10-19 (E3): the phone remembers who opened
    // this driver session; another driver or organization replaces it.
    writeDriverSessionMarker(host, {
      sessionNamespace,
      organizationId: installation.organizationId,
    });
    host.dispatchEvent(new Event(driverSessionChangedEvent));
    return;
  }
  // A session that is not a driver's (an operations role here) is not the
  // remembered driver: the phone forgets it, and that partition stays unseen.
  clearDriverSessionMarker(host);
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
  /**
   * DRV-SESSION-EXPIRY-OFFLINE-2026-10-19 (E3): the server could not confirm a
   * session and the remembered driver's partition was opened instead
   * (installRememberedDriverSession).
   */
  readonly remembered?: true;
}

export interface BootstrapOptions {
  /**
   * The driver PWA's pages (`/driver/...`): when the server cannot confirm a
   * session (no network, a 5xx, an answer that cannot be read), the driver
   * this phone remembers is opened locally (installRememberedDriverSession).
   */
  readonly rememberDriver?: boolean;
}

/**
 * Runs once per page load in BFF mode: session → organization contexts → in-memory session objects.
 *
 * DRV-SESSION-EXPIRY-OFFLINE-2026-10-19 (owner answers E2 and E3): what the phone remembers
 * (driver-session-marker) follows the server's answer. A confirmed driver session writes it; any
 * other person, organization or role replaces or removes it, so the remembered partition is never
 * shown or sent to them; a person the server does not authorize removes it. A 401 (the session
 * ended) keeps it, and the stops screen asks the same driver to sign in again. Only when the server
 * cannot answer, or cannot list the organizations of the same person, is the remembered driver
 * opened, locally and without any request.
 */
export async function bootstrapBffSession(
  host: SessionHost,
  fetcher: typeof fetch = fetch,
  preferredOrganizationId?: string | null,
  options: BootstrapOptions = {},
): Promise<BffBootstrapResult> {
  const session = await fetchBffSession(fetcher);
  if (session.status === "unavailable") {
    if (options.rememberDriver && installRememberedDriverSession(host)) {
      return { session, installation: { kind: "none" }, remembered: true };
    }
    clearInstalledSessions(host);
    return { session, installation: { kind: "none" } };
  }
  if (session.status === "anonymous") {
    clearInstalledSessions(host);
    return { session, installation: { kind: "none" } };
  }
  if (!session.authorized) {
    clearDriverSessionMarker(host);
    clearInstalledSessions(host);
    return { session, installation: { kind: "none" } };
  }
  const contexts = await fetchOrganizationContexts(fetcher);
  if (contexts === null) {
    // The person is confirmed but not their organizations: the remembered
    // driver opens locally only when it is this same person.
    if (
      options.rememberDriver &&
      readDriverSessionMarker(host)?.sessionNamespace === session.sessionNamespace &&
      installRememberedDriverSession(host)
    ) {
      return { session, installation: { kind: "none" }, remembered: true };
    }
    if (readDriverSessionMarker(host)?.sessionNamespace !== session.sessionNamespace) {
      clearDriverSessionMarker(host);
    }
    clearInstalledSessions(host);
    return { session, installation: { kind: "none" } };
  }
  const installation = selectSessionInstallation(contexts, preferredOrganizationId);
  installBffSession(host, installation, session.csrfToken, session.sessionNamespace, {
    account: { displayName: session.user.name },
    onOrganizationChange: async (organizationId) => {
      await bootstrapBffSession(host, fetcher, organizationId, options);
    },
  });
  return { session, installation };
}

/**
 * DRV-SESSION-EXPIRY-OFFLINE-2026-10-19 (owner answer E3 "Sí, y envía al validar (Recommended)"):
 * installs the driver this phone remembers as a `remembered` driver session, so the stops screen
 * opens that driver's saved stops and queue from IndexedDB and lets them queue actions. It has no
 * CSRF token (a write fails before any request) and the stops screen makes no request with it; it
 * is replaced by the confirmed session once `GET /auth/session` answers for the same driver and
 * organization. Returns false when nobody is remembered. An already installed remembered session
 * of the same driver is kept as it is, so a check that still cannot reach the server changes nothing.
 */
export function installRememberedDriverSession(host: SessionHost): boolean {
  const marker = readDriverSessionMarker(host);
  if (marker === null) return false;
  const current = host.__paquetenviaDriverSession;
  if (
    current?.remembered === true &&
    current.cacheNamespace === marker.sessionNamespace &&
    current.organizationId === marker.organizationId
  ) {
    return true;
  }
  clearInstalledSessions(host);
  host.__paquetenviaAccount = { displayName: null };
  host.__paquetenviaDriverSession = {
    organizationId: marker.organizationId,
    cacheNamespace: marker.sessionNamespace,
    credentialMode: "cookie",
    getCsrfToken: () => {
      throw new MissingCredentialError();
    },
    remembered: true,
  };
  host.dispatchEvent(new Event(driverSessionChangedEvent));
  return true;
}
