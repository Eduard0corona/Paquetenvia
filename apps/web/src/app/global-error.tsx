"use client";

import "./globals.css";

/**
 * Replaces the framework global error page, which renders inline `style="…"`
 * that the nonce-based CSP blocks. It replaces the root layout, so it renders
 * its own <html> and <body> and styles itself only with classes from
 * globals.css (see docs/development/web-security-headers.md). Error details
 * are never shown: they may carry server data.
 */
export default function GlobalError({
  reset,
}: Readonly<{ error: Error & { digest?: string }; reset: () => void }>) {
  return (
    <html lang="es-MX">
      <body>
        <main className="shell">
          <section className="card">
            <p className="eyebrow">Error</p>
            <h1>Algo salió mal</h1>
            <p>No pudimos mostrar esta página. Intenta de nuevo.</p>
            <button className="button errorRetry" type="button" onClick={reset}>
              Reintentar
            </button>
          </section>
        </main>
      </body>
    </html>
  );
}
