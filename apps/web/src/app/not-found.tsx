import type { Metadata } from "next";
import Link from "next/link";

/**
 * Any unmatched URL lands here, a malformed public tracking link (/track/a/b)
 * included, so the page names no brand while GATE-001 is open
 * (GATE-001-NEUTRAL-PUBLIC-BRAND-2026-10-10) and offers no driver PWA manifest.
 */
export const metadata: Metadata = {
  title: "Página no encontrada",
  manifest: null,
};

/**
 * Replaces the framework 404, which renders an inline `<style>` and `style="…"`
 * without the CSP nonce and would lose its styling under
 * `style-src 'self' 'nonce-…'`. Styling comes only from classes in
 * globals.css: never render a `style=` attribute on the server (see
 * docs/development/web-security-headers.md).
 */
export default function NotFound() {
  return (
    <main className="shell">
      <section className="card">
        <p className="eyebrow">404</p>
        <h1>Página no encontrada</h1>
        <p>La dirección no existe o ya no está disponible.</p>
        <Link className="button" href="/">
          Ir al inicio
        </Link>
      </section>
    </main>
  );
}
