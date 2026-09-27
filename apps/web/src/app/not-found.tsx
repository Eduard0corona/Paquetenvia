import Link from "next/link";

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
