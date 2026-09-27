import { isLocalReturnUrl, type BffSessionState } from "./bff-session";

/**
 * MFA step-up (AUTH-001-MFA-STEP-UP). The API answers a 403 problem with
 * `code: "MFA_REQUIRED"` when the only thing missing is a second factor; every other
 * 403 stays generic. The web then offers "Verificar identidad": `/login?mfa=required`
 * shows the prompt, which starts `/auth/login?mfa=required&return_url=…`. Nobody is
 * forced to MFA (drivers included); the prompt only appears when a route asked for it.
 */
export const mfaRequiredProblemCode = "MFA_REQUIRED";

export function isMfaRequiredProblem(status: number, body: unknown): boolean {
  return (
    status === 403 &&
    typeof body === "object" &&
    body !== null &&
    (body as Record<string, unknown>).code === mfaRequiredProblemCode
  );
}

/** Reads a failed response without consuming its body for the caller. */
export async function isMfaRequiredResponse(response: Response): Promise<boolean> {
  if (response.status !== 403) {
    return false;
  }
  const contentType = response.headers.get("content-type") ?? "";
  if (!/^application\/(problem\+)?json\b/i.test(contentType)) {
    return false;
  }
  try {
    return isMfaRequiredProblem(response.status, await response.clone().json());
  } catch {
    return false;
  }
}

/** `/login` with the step-up prompt, returning to `returnUrl` only when it is local. */
export function buildStepUpPromptHref(returnUrl: string | null | undefined): string {
  const search = new URLSearchParams({ mfa: "required" });
  if (isLocalReturnUrl(returnUrl) && returnUrl !== "/login") {
    search.set("return_url", returnUrl);
  }
  return `/login?${search.toString()}`;
}

/** The prompt is offered to a signed-in, authorized person whose session lacks MFA. */
export function shouldOfferStepUp(session: BffSessionState, requested: boolean): boolean {
  return (
    requested &&
    session.status === "authenticated" &&
    session.authorized &&
    !session.mfa
  );
}
