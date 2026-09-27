/**
 * Security headers shared by every page of the web app.
 *
 * The Content-Security-Policy is nonce-based and emitted per request by
 * `src/proxy.ts`: Next.js reads the nonce from the request CSP header while
 * rendering and attaches it to its own scripts and styles, so production needs
 * neither `'unsafe-inline'` nor `'unsafe-eval'`. `next.config.ts` only adds
 * the static headers below; it must never emit a second CSP, because browsers
 * enforce every CSP header they receive.
 */

export const CONNECT_SOURCES_VARIABLE = "PAQUETERIA_CSP_CONNECT_SOURCES";

export type ContentSecurityPolicyInput = {
  readonly nonce: string;
  readonly pathname: string;
  readonly development: boolean;
  readonly connectSources: readonly string[];
};

export function createNonce(): string {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary);
}

export function buildContentSecurityPolicy({
  nonce,
  pathname,
  development,
  connectSources,
}: ContentSecurityPolicyInput): string {
  if (!/^[A-Za-z0-9+/]{16,}={0,2}$/.test(nonce)) {
    throw new Error("CSP nonce must be base64.");
  }

  const scriptSources = [
    "'self'",
    `'nonce-${nonce}'`,
    "'strict-dynamic'",
    // React needs eval only for development debugging, never in production.
    ...(development ? ["'unsafe-eval'"] : []),
  ];
  // With a nonce present browsers ignore 'unsafe-inline'; development keeps it
  // (without the nonce) only because the dev overlay and HMR inject styles.
  const styleSources = development
    ? ["'self'", "'unsafe-inline'"]
    : ["'self'", `'nonce-${nonce}'`];
  const formAction = isTrackingPath(pathname) ? "'none'" : "'self'";

  return [
    "default-src 'self'",
    `script-src ${scriptSources.join(" ")}`,
    `style-src ${styleSources.join(" ")}`,
    // 'strict-dynamic' disables 'self' for scripts, so the Service Worker
    // needs its own directive.
    "worker-src 'self'",
    "manifest-src 'self'",
    "img-src 'self' data: blob:",
    "font-src 'self'",
    `connect-src ${connectSources.join(" ")}`,
    "object-src 'none'",
    "base-uri 'none'",
    "frame-src 'none'",
    "frame-ancestors 'none'",
    `form-action ${formAction}`,
  ].join("; ");
}

/**
 * `'self'` plus the configured API origin (and its WebSocket origin), plus any
 * extra origins listed at runtime in PAQUETERIA_CSP_CONNECT_SOURCES, such as
 * the object-storage origin that receives signed proof uploads. Every extra
 * entry must be an exact origin; wildcards and paths are rejected.
 */
export function resolveConnectSources(
  apiBaseUrl: string | undefined,
  extraSources: string | undefined,
  production: boolean,
): string[] {
  const sources = ["'self'"];
  if (apiBaseUrl !== undefined && apiBaseUrl !== "") {
    const origin = new URL(apiBaseUrl).origin;
    sources.push(origin, toWebSocketOrigin(origin));
  }

  for (const entry of (extraSources ?? "").split(/\s+/)) {
    if (entry === "") continue;
    if (entry.includes("*")) {
      throw new Error(`${CONNECT_SOURCES_VARIABLE} entries must not use wildcards.`);
    }
    let parsed: URL;
    try {
      parsed = new URL(entry);
    } catch {
      throw new Error(`${CONNECT_SOURCES_VARIABLE} entries must be absolute origins.`);
    }
    const allowedProtocol =
      parsed.protocol === "https:" ||
      parsed.protocol === "wss:" ||
      (!production && (parsed.protocol === "http:" || parsed.protocol === "ws:"));
    if (!allowedProtocol || parsed.origin !== entry.replace(/\/$/, "")) {
      throw new Error(`${CONNECT_SOURCES_VARIABLE} entries must be exact secure origins.`);
    }
    sources.push(parsed.origin);
  }

  return [...new Set(sources)];
}

/**
 * Resolves the connect-src origins from the server environment once, when the
 * server starts (`src/instrumentation.ts`) and when the proxy module loads, so
 * a misconfigured PAQUETERIA_CSP_CONNECT_SOURCES is reported at startup with a
 * clear message instead of being re-parsed and thrown on every request. It still fails closed: the
 * proxy never falls back to a looser policy. `NEXT_PUBLIC_API_BASE_URL` is
 * read with a literal `process.env.` access so Next.js inlines its build-time
 * value.
 */
export function resolveConfiguredConnectSources(): string[] {
  try {
    return resolveConnectSources(
      process.env.NEXT_PUBLIC_API_BASE_URL,
      process.env[CONNECT_SOURCES_VARIABLE],
      process.env.NODE_ENV === "production",
    );
  } catch (error) {
    const reason = error instanceof Error ? error.message : String(error);
    throw new Error(
      `Invalid Content-Security-Policy configuration: ${reason} ` +
        `Set ${CONNECT_SOURCES_VARIABLE} to a space-separated list of exact ` +
        "origins such as https://storage.example.test, and " +
        "NEXT_PUBLIC_API_BASE_URL to an absolute URL.",
      { cause: error },
    );
  }
}

export function isTrackingPath(pathname: string): boolean {
  return pathname === "/track" || pathname.startsWith("/track/");
}

export function isPrivateNoStorePath(pathname: string): boolean {
  return (
    isTrackingPath(pathname) ||
    pathname === "/ops" ||
    pathname.startsWith("/ops/")
  );
}

function toWebSocketOrigin(origin: string): string {
  return origin.replace(/^http:/, "ws:").replace(/^https:/, "wss:");
}
