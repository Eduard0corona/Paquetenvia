/**
 * Base URL of Paqueteria.Api for the operations, finance and public tracking screens.
 *
 * `NEXT_PUBLIC_API_BASE_URL` is inlined at build time (it must stay unset in BFF mode, see
 * next.config.ts); without it the API is reached through the page's own origin. The server
 * render never calls the API, so its placeholder origin is never used for a request. The
 * driver PWA keeps its stricter validation in src/driver/api/api-base-url.ts.
 */
export function clientApiBaseUrl(): string {
  return (
    process.env.NEXT_PUBLIC_API_BASE_URL ??
    (typeof window === "undefined" ? "http://127.0.0.1" : window.location.origin)
  );
}
