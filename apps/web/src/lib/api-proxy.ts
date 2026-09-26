/**
 * Same-origin routing for the AuthCenter BFF (AUTH-001).
 *
 * The browser must reach the API on the web origin so the `__Host-` session
 * cookie is first-party and CSRF/Origin checks hold. In Azure the ingress routes
 * these prefixes to Paqueteria.Api; locally (or when no ingress exists)
 * `PAQUETENVIA_API_PROXY_ORIGIN` makes Next rewrite them. The value is read when
 * the config is evaluated (`next build` and `next start`), and is server-only:
 * it is never exposed to the browser bundle.
 */
export const apiProxyPrefixes = [
  "/api/:path*",
  "/hubs/:path*",
  "/auth/:path*",
  "/signin-authcenter",
] as const;

export interface ProxyRewrite {
  readonly source: string;
  readonly destination: string;
}

export function parseApiProxyOrigin(value: string | undefined): string | undefined {
  const candidate = value?.trim();
  if (!candidate) {
    return undefined;
  }
  let url: URL;
  try {
    url = new URL(candidate);
  } catch {
    throw new Error("PAQUETENVIA_API_PROXY_ORIGIN must be an absolute URL.");
  }
  if (
    (url.protocol !== "http:" && url.protocol !== "https:") ||
    url.username ||
    url.password ||
    url.search ||
    url.hash ||
    (url.pathname !== "/" && url.pathname !== "")
  ) {
    throw new Error("PAQUETENVIA_API_PROXY_ORIGIN must be a bare http(s) origin.");
  }
  return url.origin;
}

export function buildApiProxyRewrites(origin: string | undefined): ProxyRewrite[] {
  if (origin === undefined) {
    return [];
  }
  return apiProxyPrefixes.map((source) => ({
    source,
    destination: `${origin}${source}`,
  }));
}

export function assertSameOriginApiForBff(
  authMode: string | undefined,
  publicApiBaseUrl: string | undefined,
): void {
  if (authMode === "bff" && publicApiBaseUrl !== undefined && publicApiBaseUrl.trim() !== "") {
    throw new Error(
      "NEXT_PUBLIC_API_BASE_URL must be unset when NEXT_PUBLIC_AUTH_MODE=bff: the API is reached through the same origin.",
    );
  }
}
