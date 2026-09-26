import { NextResponse, type NextRequest } from "next/server";
import {
  CONNECT_SOURCES_VARIABLE,
  buildContentSecurityPolicy,
  createNonce,
  isPrivateNoStorePath,
  resolveConnectSources,
} from "./security/security-headers";

/**
 * Emits a fresh nonce-based Content-Security-Policy for every page request.
 * Next.js reads the nonce from the forwarded request CSP header and applies it
 * to its framework scripts and styles, which requires dynamic rendering (see
 * `await connection()` in the root layout).
 */
export function proxy(request: NextRequest) {
  const nonce = createNonce();
  const contentSecurityPolicy = buildContentSecurityPolicy({
    nonce,
    pathname: request.nextUrl.pathname,
    development: process.env.NODE_ENV === "development",
    connectSources: resolveConnectSources(
      process.env.NEXT_PUBLIC_API_BASE_URL,
      process.env[CONNECT_SOURCES_VARIABLE],
      process.env.NODE_ENV === "production",
    ),
  });

  const requestHeaders = new Headers(request.headers);
  requestHeaders.set("x-nonce", nonce);
  requestHeaders.set("Content-Security-Policy", contentSecurityPolicy);

  const response = NextResponse.next({ request: { headers: requestHeaders } });
  response.headers.set("Content-Security-Policy", contentSecurityPolicy);
  if (isPrivateNoStorePath(request.nextUrl.pathname)) {
    response.headers.set("Cache-Control", "no-store, private");
    response.headers.set("Pragma", "no-cache");
    response.headers.set("X-Robots-Tag", "noindex, nofollow, noarchive");
  }
  return response;
}

export const config = {
  matcher: [
    // Every page, including /driver, /ops, /track and /dev. Static build
    // output, the Service Worker and the manifest carry no inline code and keep
    // the static headers from next.config.ts. Same-origin /api and /hubs are
    // forwarded to the API untouched (no nonce or CSP request headers).
    "/((?!api/|hubs/|_next/static|_next/image|favicon.ico|sw.js|manifest.webmanifest).*)",
  ],
};
