"use client";

import { useSearchParams } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import {
  buildLoginHref,
  isLocalReturnUrl,
  logoutBffSession,
  type BffSessionState,
} from "../bff-session";
import {
  bootstrapBffSession,
  clearInstalledSessions,
  type BffSessionInstallation,
} from "../bff-session-installation";

type ViewState =
  | { readonly kind: "loading" }
  | {
      readonly kind: "ready";
      readonly session: BffSessionState;
      readonly installation: BffSessionInstallation;
    };

const destinations = {
  operations: "/ops/dashboard",
  driver: "/driver/stops",
} as const;

export function LoginExperience() {
  const search = useSearchParams();
  const requested = search.get("return_url");
  const returnUrl = isLocalReturnUrl(requested) && requested !== "/login" ? requested : null;
  const signInFailed = search.get("error") === "signin_failed";
  const [view, setView] = useState<ViewState>({ kind: "loading" });
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    const result = await bootstrapBffSession(window);
    setView({ kind: "ready", ...result });
  }, []);

  const reload = useCallback(async () => {
    setView({ kind: "loading" });
    await load();
  }, [load]);

  useEffect(() => {
    let active = true;
    void bootstrapBffSession(window).then((result) => {
      if (active) {
        setView({ kind: "ready", ...result });
      }
    });
    return () => {
      active = false;
    };
  }, []);

  async function logout(csrfToken: string) {
    setBusy(true);
    try {
      await logoutBffSession(csrfToken);
    } finally {
      clearInstalledSessions(window);
      setBusy(false);
      await reload();
    }
  }

  return (
    <main className="shell">
      <section className="card" aria-labelledby="login-title" aria-busy={view.kind === "loading"}>
        <p className="eyebrow">Paquetenvia</p>
        <h1 id="login-title">Iniciar sesión</h1>
        {signInFailed ? (
          <p role="alert">No fue posible completar el inicio de sesión. Intenta de nuevo.</p>
        ) : null}
        {view.kind === "loading" ? <p role="status">Consultando tu sesión…</p> : null}
        {view.kind === "ready" ? (
          <SessionView
            view={view}
            returnUrl={returnUrl}
            busy={busy}
            onLogout={logout}
            onRetry={reload}
          />
        ) : null}
      </section>
    </main>
  );
}

function SessionView({
  view,
  returnUrl,
  busy,
  onLogout,
  onRetry,
}: {
  readonly view: Extract<ViewState, { kind: "ready" }>;
  readonly returnUrl: string | null;
  readonly busy: boolean;
  readonly onLogout: (csrfToken: string) => Promise<void>;
  readonly onRetry: () => Promise<void>;
}) {
  const { session, installation } = view;
  if (session.status === "anonymous") {
    return (
      <>
        <p>Accede con tu cuenta de AuthCenter. Paquetenvia no ve ni guarda tu contraseña.</p>
        <a className="button" href={buildLoginHref(returnUrl ?? "/login")}>
          Continuar con AuthCenter
        </a>
      </>
    );
  }
  if (session.status === "unavailable") {
    return (
      <>
        <p role="alert">El servicio de sesión no está disponible en este momento.</p>
        <button type="button" className="button" onClick={() => void onRetry()}>
          Reintentar
        </button>
      </>
    );
  }

  const name = session.user.name ?? "tu cuenta";
  return (
    <>
      <p role="status">Sesión iniciada como {name}.</p>
      {!session.authorized ? (
        <p>
          Tu cuenta todavía no tiene acceso a Paquetenvia. Pide a un administrador de tu
          organización que te registre o te invite.
        </p>
      ) : installation.kind === "none" ? (
        <p>Tu cuenta no tiene organizaciones activas.</p>
      ) : (
        <>
          <p>Organización activa: {installation.displayName}.</p>
          <a className="button" href={returnUrl ?? destinations[installation.kind]}>
            Continuar
          </a>
        </>
      )}
      <p>
        <button
          type="button"
          className="textLink"
          disabled={busy}
          onClick={() => void onLogout(session.csrfToken)}
        >
          Cerrar sesión
        </button>
      </p>
    </>
  );
}
