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

export async function logoutBffSession(
  csrfToken: string,
  fetcher: typeof fetch = fetch,
): Promise<boolean> {
  if (!csrfPattern.test(csrfToken)) {
    return false;
  }
  try {
    const response = await fetcher(bffPaths.logout, {
      method: "POST",
      credentials: "include",
      cache: "no-store",
      headers: { [csrfHeaderName]: csrfToken },
    });
    return response.status === 204 || response.status === 401;
  } catch {
    return false;
  }
}

function optionalText(value: unknown, maximumLength: number): string | null {
  return typeof value === "string" && value.length > 0 && value.length <= maximumLength
    ? value
    : null;
}
