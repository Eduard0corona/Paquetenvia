import Link from "next/link";

/** Validation errors and the last server outcome, with the MFA step-up link when it applies. */
export function TenantFeedback({
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
        <ul className="opsAlert" role="alert">
          {errors.map((error) => <li key={error}>{error}</li>)}
        </ul>
      )}
      {message !== null && (
        <p className={stepUpHref === null ? "opsWarning" : "opsAlert"} role="status">
          {message}{" "}
          {stepUpHref !== null && <Link className="opsPrimary" href={stepUpHref}>Verificar identidad</Link>}
        </p>
      )}
    </>
  );
}

/** The no-session and no-access panels every UI-001 screen shows before any tenant data. */
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
        <section className="opsMessage" role="alert">
          <h2>Sin sesión</h2>
          <p>Inicia sesión y selecciona una organización.</p>
        </section>
      )}
      {phase === "access_unavailable" && (
        <section className="opsMessage" role="alert">
          <h2>Acceso no disponible</h2>
          <p>{accessMessage}</p>
        </section>
      )}
      {phase === "loading" && <p className="opsLive" aria-live="polite">Cargando permisos.</p>}
    </>
  );
}
