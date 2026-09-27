import { buildStepUpHref } from "../bff-session";

/**
 * "Verificar identidad" (AUTH-001-MFA-STEP-UP): with a live single sign-on session AuthCenter
 * asks only for the second factor (or enrolls one); the new session replaces this one.
 */
export function StepUpPrompt({ returnUrl }: { readonly returnUrl: string | null }) {
  return (
    <div role="region" aria-labelledby="step-up-title">
      <h2 id="step-up-title">Verifica tu identidad</h2>
      <p>
        Esta sección requiere un segundo factor de autenticación. AuthCenter te lo pedirá
        (o te ayudará a configurarlo) y después volverás aquí.
      </p>
      <a className="button" href={buildStepUpHref(returnUrl)}>
        Verificar identidad
      </a>
    </div>
  );
}
