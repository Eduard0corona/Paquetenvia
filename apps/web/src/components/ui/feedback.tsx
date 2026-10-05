import Link from "next/link";

/**
 * Validation errors and the last server outcome of a tenant screen, with the MFA step-up
 * link when it applies. The only implementation in the app (UI-001 phase 1).
 */
export function Feedback({
  errors,
  message,
  stepUpHref,
}: {
  readonly errors: readonly string[];
  readonly message: string | null;
  readonly stepUpHref: string | null;
}) {
  return (
    <>
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
