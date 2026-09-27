import { csrfHeaderName } from "./request-credentials";

/** BFF contract served by Paqueteria.Api through the same origin (Next rewrites or ingress). */
export const bffPaths = {
  login: "/auth/login",
  session: "/auth/session",
  logout: "/auth/logout",
} as const;

export interface BffUser {
  readonly name: string | null;
  readonly email: string | null;
}

export type BffSessionState =
  | {
      readonly status: "authenticated";
      readonly authorized: boolean;
      readonly mfa: boolean;
      readonly csrfToken: string;
      readonly sessionNamespace: string;
      readonly user: BffUser;
    }
  | { readonly status: "anonymous" }
  | { readonly status: "unavailable" };

const csrfPattern = /^[A-Za-z0-9_-]{32,256}$/;
const namespacePattern = /^[A-Za-z0-9_-]{16,128}$/;

export function isLocalReturnUrl(value: string | null | undefined): value is string {
  return (
    typeof value === "string" &&
    value.length > 0 &&
    value.length <= 2048 &&
    value.startsWith("/") &&
    !value.startsWith("//") &&
    !value.includes("\\") &&
    !/[\u0000-\u001f\u007f]/.test(value)
  );
}

export function buildLoginHref(returnUrl?: string | null): string {
  const destination = isLocalReturnUrl(returnUrl) ? returnUrl : "/login";
  return `${bffPaths.login}?return_url=${encodeURIComponent(destination)}`;
}

/**
 * "Verificar identidad" (AUTH-001-MFA-STEP-UP): a new AuthCenter sign-in that requires a
 * second factor and replaces the current session. Same local-only return_url rule as login.
 */
export function buildStepUpHref(returnUrl?: string | null): string {
  const destination = isLocalReturnUrl(returnUrl) ? returnUrl : "/login";
  return `${bffPaths.login}?mfa=required&return_url=${encodeURIComponent(destination)}`;
}

export function parseBffSession(value: unknown): BffSessionState {
  if (typeof value !== "object" || value === null) {
    return { status: "unavailable" };
  }
  const body = value as Record<string, unknown>;
  const user = body.user as Record<string, unknown> | undefined;
  if (
    body.authenticated !== true ||
    typeof body.authorized !== "boolean" ||
    typeof body.mfa !== "boolean" ||
    typeof body.csrfToken !== "string" ||
    !csrfPattern.test(body.csrfToken) ||
    typeof body.sessionNamespace !== "string" ||
    !namespacePattern.test(body.sessionNamespace) ||
    typeof user !== "object" ||
    user === null
  ) {
    return { status: "unavailable" };
  }
  return {
    status: "authenticated",
    authorized: body.authorized,
    mfa: body.mfa,
    csrfToken: body.csrfToken,
    sessionNamespace: body.sessionNamespace,
    user: {
      name: optionalText(user.name, 200),
      email: optionalText(user.email, 320),
    },
  };
}

export async function fetchBffSession(
  fetcher: typeof fetch = fetch,
  signal?: AbortSignal,
): Promise<BffSessionState> {
  let response: Response;
  try {
    response = await fetcher(bffPaths.session, {
      method: "GET",
      credentials: "include",
      cache: "no-store",
      headers: { Accept: "application/json" },
      signal,
    });
  } catch {
    return { status: "unavailable" };
  }
  if (response.status === 401) {
    return { status: "anonymous" };
  }
  if (!response.ok) {
    return { status: "unavailable" };
  }
  try {
    return parseBffSession(await response.json());
  } catch {
    return { status: "unavailable" };
  }
}

/**
 * Result of `POST /auth/logout` (AUTH-001-RP-INITIATED-LOGOUT). `endSessionUrl` is the
 * AuthCenter end-session URL to navigate to so the single sign-on session ends too;
 * `null` means only the local session ended (or it was already gone).
 */
export type BffLogoutResult =
  | { readonly ok: true; readonly endSessionUrl: string | null }
  | { readonly ok: false };

export async function logoutBffSession(
  csrfToken: string,
  fetcher: typeof fetch = fetch,
): Promise<BffLogoutResult> {
  if (!csrfPattern.test(csrfToken)) {
    return { ok: false };
  }
  let response: Response;
  try {
    response = await fetcher(bffPaths.logout, {
      method: "POST",
      credentials: "include",
      cache: "no-store",
      headers: { [csrfHeaderName]: csrfToken, Accept: "application/json" },
    });
  } catch {
    return { ok: false };
  }
  if (response.status === 401) {
    // The session was already gone: nothing left to end locally.
    return { ok: true, endSessionUrl: null };
  }
  if (response.status !== 200) {
    return { ok: false };
  }
  try {
    const body: unknown = await response.json();
    const value =
      typeof body === "object" && body !== null
        ? (body as Record<string, unknown>).endSessionUrl
        : undefined;
    return { ok: true, endSessionUrl: parseEndSessionUrl(value) };
  } catch {
    // The local session is already destroyed; fall back to the local login page.
    return { ok: true, endSessionUrl: null };
  }
}

/** Only an absolute HTTPS URL without credentials is followed; anything else is ignored. */
export function parseEndSessionUrl(value: unknown): string | null {
  if (typeof value !== "string" || value.length === 0 || value.length > 16_384) {
    return null;
  }
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return null;
  }
  return url.protocol === "https:" && url.username === "" && url.password === ""
    ? url.toString()
    : null;
}

export interface NavigationTarget {
  assign(url: string): void;
}

/**
 * After logout the browser leaves the application: to AuthCenter to end its single
 * sign-on session (it returns to /login), or straight to /login when that is not possible.
 */
export function navigateAfterLogout(
  result: BffLogoutResult,
  location: NavigationTarget,
): void {
  location.assign(result.ok && result.endSessionUrl ? result.endSessionUrl : "/login");
}

function optionalText(value: unknown, maximumLength: number): string | null {
  return typeof value === "string" && value.length > 0 && value.length <= maximumLength
    ? value
    : null;
}
