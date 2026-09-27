/**
 * Credential strategies shared by every private API and realtime client.
 *
 * - `bearer`: local Mock provider (Development/Testing/DevSynthetic). The token is
 *   supplied by an in-memory session and sent as `Authorization: Bearer`.
 * - `cookie`: AuthCenter BFF. The browser holds only the `__Host-` HttpOnly
 *   session cookie; requests are same-origin with `credentials: "include"` and
 *   every write carries the session-bound CSRF token in `X-AuthCenter-CSRF`.
 *   No OAuth token ever reaches JavaScript.
 */
export const csrfHeaderName = "X-AuthCenter-CSRF";

export interface BearerSessionCredentials {
  readonly credentialMode?: "bearer";
  getAccessToken(): string | Promise<string>;
}

export interface CookieSessionCredentials {
  readonly credentialMode: "cookie";
  getCsrfToken(): string;
}

export type SessionCredentials =
  | BearerSessionCredentials
  | CookieSessionCredentials;

export interface RequestAuthorization {
  readonly headers: Readonly<Record<string, string>>;
  readonly credentials: RequestCredentials;
}

export class MissingCredentialError extends Error {
  public constructor() {
    super("The session has no usable credential.");
    this.name = "MissingCredentialError";
  }
}

const safeMethods = new Set(["GET", "HEAD", "OPTIONS"]);
const csrfPattern = /^[A-Za-z0-9_-]{32,256}$/;

export function isCookieSession(
  session: SessionCredentials,
): session is CookieSessionCredentials {
  return session.credentialMode === "cookie";
}

export function hasUsableCredentials(value: unknown): boolean {
  if (typeof value !== "object" || value === null) {
    return false;
  }
  const candidate = value as Partial<CookieSessionCredentials> &
    Partial<BearerSessionCredentials>;
  if (candidate.credentialMode === "cookie") {
    return typeof candidate.getCsrfToken === "function";
  }
  return (
    (candidate.credentialMode === undefined ||
      candidate.credentialMode === "bearer") &&
    typeof candidate.getAccessToken === "function"
  );
}

export async function resolveRequestAuthorization(
  session: SessionCredentials,
  method: string,
): Promise<RequestAuthorization> {
  if (isCookieSession(session)) {
    if (safeMethods.has(method.toUpperCase())) {
      return { headers: {}, credentials: "include" };
    }
    return {
      headers: { [csrfHeaderName]: readCsrfToken(session) },
      credentials: "include",
    };
  }

  // Provider failures propagate unchanged so callers keep their own semantics.
  const token: unknown = await session.getAccessToken();
  if (typeof token !== "string" || token.length < 1 || token.length > 8192) {
    throw new MissingCredentialError();
  }
  return { headers: { Authorization: `Bearer ${token}` }, credentials: "omit" };
}

export interface RealtimeCredentialOptions {
  readonly tokenFactory?: () => string | Promise<string>;
  readonly csrfTokenFactory?: () => string;
}

export function realtimeCredentials(
  session: SessionCredentials,
): RealtimeCredentialOptions {
  return isCookieSession(session)
    ? { csrfTokenFactory: () => readCsrfToken(session) }
    : { tokenFactory: () => session.getAccessToken() };
}

function readCsrfToken(session: CookieSessionCredentials): string {
  let token: unknown;
  try {
    token = session.getCsrfToken();
  } catch {
    throw new MissingCredentialError();
  }
  if (typeof token !== "string" || !csrfPattern.test(token)) {
    throw new MissingCredentialError();
  }
  return token;
}
