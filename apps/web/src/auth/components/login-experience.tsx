"use client";

import { useSearchParams } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import {
  buildLoginHref,
  buildStepUpHref,
  isLocalReturnUrl,
  logoutBffSession,
  navigateAfterLogout,
  type BffLogoutResult,
  type BffSessionState,
} from "../bff-session";
import {
  bootstrapBffSession,
  clearInstalledSessions,
  type BffSessionInstallation,
} from "../bff-session-installation";
import { loginErrorMessage } from "../login-errors";
import { shouldOfferStepUp } from "../step-up";
import { StepUpPrompt } from "./step-up-prompt";

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
  const errorMessage = loginErrorMessage(search.get("error"));
  const mfaRequested = search.get("mfa") === "required";
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
    let result: BffLogoutResult = { ok: false };
    try {
      result = await logoutBffSession(csrfToken);
    } finally {
      clearInstalledSessions(window);
    }
    if (result.ok) {
      // AUTH-001-RP-INITIATED-LOGOUT: AuthCenter ends its single sign-on session and returns
      // to /login; without an end-session URL the local logout is all there is.
      navigateAfterLogout(result, window.location);
      return;
    }
    setBusy(false);
    await reload();
  }

  return (
    <main className="shell">
      <section className="card" aria-labelledby="login-title" aria-busy={view.kind === "loading"}>
        <p className="eyebrow">Paquetenvia</p>
        <h1 id="login-title">Iniciar sesión</h1>
        {errorMessage ? <p role="alert">{errorMessage}</p> : null}
        {view.kind === "loading" ? <p role="status">Consultando tu sesión…</p> : null}
        {view.kind === "ready" ? (
          <SessionView
            view={view}
            returnUrl={returnUrl}
            mfaRequested={mfaRequested}
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
  mfaRequested,
  busy,
  onLogout,
  onRetry,
}: {
  readonly view: Extract<ViewState, { kind: "ready" }>;
  readonly returnUrl: string | null;
  readonly mfaRequested: boolean;
  readonly busy: boolean;
  readonly onLogout: (csrfToken: string) => Promise<void>;
  readonly onRetry: () => Promise<void>;
}) {
  const { session, installation } = view;
  if (session.status === "anonymous") {
    return (
      <>
        <p>Accede con tu cuenta de AuthCenter. Paquetenvia no ve ni guarda tu contraseña.</p>
        <a
          className="button"
          href={
            mfaRequested
              ? buildStepUpHref(returnUrl ?? "/login")
              : buildLoginHref(returnUrl ?? "/login")
          }
        >
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
        <p>Tu cuenta no tiene acceso a Paquetenvia en este momento.</p>
      ) : shouldOfferStepUp(session, mfaRequested) ? (
        <StepUpPrompt returnUrl={returnUrl} />
      ) : installation.kind === "none" ? (
        <>
          <p>Tu cuenta todavía no tiene una organización activa.</p>
          <a className="button" href="/onboarding">
            Crear o registrar tu organización
          </a>
        </>
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
