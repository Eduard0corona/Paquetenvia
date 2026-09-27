# Web security headers (apps/web)

This guide describes the security headers of the Next.js web app and the rules
that keep pages working under them.

## Where the headers come from

| Header | Source | Scope |
| --- | --- | --- |
| `Content-Security-Policy` (nonce-based) | `apps/web/src/proxy.ts` via `src/security/security-headers.ts` | Every page response; fresh nonce per request |
| `Referrer-Policy`, `X-Content-Type-Options`, `X-Frame-Options`, `Cross-Origin-Opener-Policy`, `Permissions-Policy` | `apps/web/next.config.ts` `headers()` | Every route |
| `Cache-Control: no-store, private`, `Pragma`, `X-Robots-Tag` | Proxy and `next.config.ts` | `/track` and `/ops` only |

`next.config.ts` must never emit a second CSP: browsers enforce every CSP they
receive. Every page is rendered per request (`await connection()` in the root
layout) so Next.js can attach the nonce to its scripts and styles.

The production policy is `script-src 'self' 'nonce-…' 'strict-dynamic'` and
`style-src 'self' 'nonce-…'`, without `'unsafe-inline'` or `'unsafe-eval'`.
Only `next dev` adds `'unsafe-eval'` and `'unsafe-inline'` for styles, for the
dev overlay and HMR.

## Routes the proxy does not touch

The proxy matcher excludes `api/`, `hubs/`, `auth/`, `signin-authcenter` and
`icons/`, plus the static build output, `favicon.ico`, `sw.js` and
`manifest.webmanifest`. Same-origin `/api`, `/hubs`, `/auth` and
`/signin-authcenter` are routed to the API, which owns their responses and
headers, so they are forwarded without nonce or CSP request headers. Static
files carry no inline code and keep only the static headers.

## Rule: no `style=` rendered on the server

`style-src` has no `'unsafe-inline'`, so the browser blocks any `style="…"`
attribute and any `<style>` element without the nonce that arrives in the
server HTML. The element renders unstyled; nothing fails loudly.

- Style server-rendered markup only with classes defined in
  `apps/web/src/app/globals.css` (or another imported stylesheet).
- Do not render `style={…}` from server components or from the first render of
  client components. A `style` prop that React applies after hydration (for
  example the operations positions view, shown only after the dispatcher
  switches to it) is set through the CSSOM and is allowed.
- Do not rely on framework fallback pages that inline styles. The app provides
  its own `src/app/not-found.tsx` and `src/app/global-error.tsx`, styled only
  with classes, because the Next.js defaults emit `<style>` without a nonce and
  `style="…"`. `global-error.tsx` replaces the root layout, so it renders its
  own `<html>`/`<body>` and imports `globals.css` itself.

## Runtime connect-src configuration

`connect-src` is `'self'`, the origin of `NEXT_PUBLIC_API_BASE_URL` and its
WebSocket origin, plus the exact origins listed (space-separated) in
`PAQUETERIA_CSP_CONNECT_SOURCES`, such as the object-storage origin for signed
proof uploads. Wildcards, paths and, in production, `http:`/`ws:` are rejected.

The configuration is validated once when the server starts
(`src/instrumentation.ts`) and when the proxy module loads, with a message that
names the variable (`Invalid Content-Security-Policy configuration: …`). It is
never re-parsed per request. An invalid value fails closed: `next start` logs
the error once at startup ("Failed to prepare server"), the process stays up but
every request answers 500, and no page is served with a weaker policy. Treat
that log line as a deployment failure.

## Verification

- `pnpm --dir apps/web test` covers the policy, the matcher exclusions, the
  connect-src validation and the class-only 404/global-error pages
  (`src/security/security-headers.test.ts`).
- `WebSecurityHeadersTests` (integration, `DriverStopsPwa` category) runs
  `next build` + `next start` and checks the headers and script nonces of real
  responses, and that the production `.next` output contains no `/dev` portal.
