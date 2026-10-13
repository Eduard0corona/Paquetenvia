import Link from "next/link";
import type { RateLimitedOperation } from "../../lib/rate-limit-wait";
import { RateLimitNotice } from "./rate-limit-notice";

/**
 * Validation errors and the last server outcome of a tenant screen, with the MFA step-up
 * link when it applies. The only implementation in the app (UI-001 phase 1). With
 * `rateLimit`, it also shows the SEC-004 wait of those operations while it lasts.
 */
export function Feedback({
  errors,
  message,
  stepUpHref,
  rateLimit,
}: {
  readonly errors: readonly string[];
  readonly message: string | null;
  readonly stepUpHref: string | null;
  readonly rateLimit?: readonly RateLimitedOperation[];
}) {
  return (
    <>
      {rateLimit !== undefined && <RateLimitNotice operations={rateLimit} />}
      {errors.length > 0 && (
        <ul className="notice noticeCrit" role="alert">
          {errors.map((error) => <li key={error}>{error}</li>)}
        </ul>
      )}
      {message !== null && (
        <p className={stepUpHref === null ? "notice noticeWarn" : "notice noticeCrit"} role="status">
          {message}{" "}
          {stepUpHref !== null && <Link className="btn btnPrimary" href={stepUpHref}>Verificar identidad</Link>}
        </p>
      )}
    </>
  );
}

/** The no-session and no-access panels every tenant screen shows before any tenant data. */
export function ScreenGate({
  phase,
  accessMessage,
}: {
  readonly phase: "no_session" | "loading" | "ready" | "access_unavailable";
  readonly accessMessage: string;
}) {
  return (
    <>
      {phase === "no_session" && (
        <section className="panel" role="alert">
          <h2>Sin sesión</h2>
          <p>Inicia sesión y selecciona una organización.</p>
        </section>
      )}
      {phase === "access_unavailable" && (
        <section className="panel" role="alert">
          <h2>Acceso no disponible</h2>
          <p>{accessMessage}</p>
        </section>
      )}
      {phase === "loading" && <p className="live" aria-live="polite">Cargando permisos.</p>}
    </>
  );
}
