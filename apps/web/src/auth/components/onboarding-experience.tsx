"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { fetchBffSession, type BffSessionState } from "../bff-session";
import {
  createIdempotencyKey,
  createOnboardingOrganization,
  createOrganizationMessage,
  displayNameMaxLength,
  fetchOwnApplications,
  legalNameMaxLength,
  organizationStatusLabels,
  organizationTypeLabels,
  validateOnboardingDraft,
  type OnboardingDraft,
  type OnboardingOrganizationType,
  type OrganizationApplication,
} from "../onboarding";

type ViewState =
  | { readonly kind: "loading" }
  | { readonly kind: "anonymous" }
  | { readonly kind: "unavailable" }
  | {
      readonly kind: "ready";
      readonly session: Extract<BffSessionState, { status: "authenticated" }>;
      readonly applications: readonly OrganizationApplication[];
    };

/**
 * REG-001 onboarding screen: create a business (active at once) or register an ally (pending
 * approval), and see the status of the caller's own applications. The API decides everything.
 */
export function OnboardingExperience() {
  const [view, setView] = useState<ViewState>({ kind: "loading" });
  const [organizationType, setOrganizationType] = useState<OnboardingOrganizationType>("BUSINESS");
  const [legalName, setLegalName] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // The same key is reused while the draft does not change, so a retry replays the original 201.
  const submission = useRef<{ key: string; draft: string } | null>(null);

  const load = useCallback(async (): Promise<ViewState> => {
    const session = await fetchBffSession();
    if (session.status === "anonymous") {
      return { kind: "anonymous" };
    }
    if (session.status !== "authenticated" || !session.authorized) {
      return { kind: "unavailable" };
    }
    const applications = await fetchOwnApplications();
    return applications === null ? { kind: "unavailable" } : { kind: "ready", session, applications };
  }, []);

  useEffect(() => {
    let active = true;
    void load().then((next) => {
      if (active) {
        setView(next);
      }
    });
    return () => {
      active = false;
    };
  }, [load]);

  async function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (view.kind !== "ready") {
      return;
    }
    const draft: OnboardingDraft = { organizationType, legalName, displayName };
    const validation = validateOnboardingDraft(draft);
    if (!validation.ok) {
      setMessage(validation.message);
      return;
    }
    const fingerprint = JSON.stringify(validation.body);
    if (submission.current?.draft !== fingerprint) {
      submission.current = { key: createIdempotencyKey(), draft: fingerprint };
    }
    setBusy(true);
    const result = await createOnboardingOrganization(draft, view.session.csrfToken, submission.current.key);
    setMessage(createOrganizationMessage(result));
    if (result.kind === "created") {
      submission.current = null;
      setView(await load());
    }
    setBusy(false);
  }

  return (
    <main className="shell">
      <section className="card" aria-labelledby="onboarding-title" aria-busy={view.kind === "loading"}>
        <p className="eyebrow">Paquetenvia</p>
        <h1 id="onboarding-title">Tu organización</h1>
        {view.kind === "loading" ? <p role="status">Consultando tu cuenta…</p> : null}
        {view.kind === "anonymous" ? (
          <>
            <p>Inicia sesión para crear tu organización.</p>
            <a className="button" href="/login">
              Iniciar sesión
            </a>
          </>
        ) : null}
        {view.kind === "unavailable" ? (
          <p role="alert">No fue posible consultar tu cuenta. Intenta de nuevo más tarde.</p>
        ) : null}
        {view.kind === "ready" ? (
          <>
            {view.applications.length > 0 ? (
              <>
                <h2>Tus solicitudes</h2>
                <ul>
                  {view.applications.map((application) => (
                    <li key={application.organizationId}>
                      {application.displayName} · {organizationTypeLabels[application.organizationType]} ·{" "}
                      {organizationStatusLabels[application.status]}
                    </li>
                  ))}
                </ul>
                {view.applications.some((application) => application.status === "ACTIVE") ? (
                  <a className="button" href="/login">
                    Entrar a Paquetenvia
                  </a>
                ) : null}
              </>
            ) : null}
            <h2>Crear una organización</h2>
            <p>
              Un negocio queda activo de inmediato. Una paquetería aliada queda pendiente hasta que un
              administrador de Paquetenvia la apruebe. Solo puedes tener una organización activa o
              pendiente a la vez.
            </p>
            <form onSubmit={(event) => void submit(event)}>
              <fieldset>
                <legend>Tipo de organización</legend>
                <label>
                  <input
                    type="radio"
                    name="organization_type"
                    value="BUSINESS"
                    checked={organizationType === "BUSINESS"}
                    onChange={() => setOrganizationType("BUSINESS")}
                  />{" "}
                  Crear un negocio
                </label>
                <label>
                  <input
                    type="radio"
                    name="organization_type"
                    value="ALLY"
                    checked={organizationType === "ALLY"}
                    onChange={() => setOrganizationType("ALLY")}
                  />{" "}
                  Registrar una paquetería aliada
                </label>
              </fieldset>
              <label htmlFor="legal_name">Razón social</label>
              <input
                id="legal_name"
                name="legal_name"
                required
                maxLength={legalNameMaxLength}
                value={legalName}
                onChange={(event) => setLegalName(event.target.value)}
              />
              <label htmlFor="display_name">Nombre visible</label>
              <input
                id="display_name"
                name="display_name"
                required
                maxLength={displayNameMaxLength}
                value={displayName}
                onChange={(event) => setDisplayName(event.target.value)}
              />
              <p>
                <button type="submit" className="button" disabled={busy}>
                  {organizationType === "BUSINESS" ? "Crear negocio" : "Enviar solicitud de aliado"}
                </button>
              </p>
            </form>
            {message ? <p role="status">{message}</p> : null}
          </>
        ) : null}
      </section>
    </main>
  );
}
