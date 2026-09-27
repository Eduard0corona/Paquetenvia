/**
 * Messages for `/login?error=…` set by the API after a failed AuthCenter callback.
 * `access_denied` (AUTH-001-ACCESS-DENIED-MESSAGE) and `email_not_verified`
 * (AUTH-EMAIL-VERIFIED-REQUIRED) have their own message; every other value, known or
 * not, shows the same generic text and no protocol detail.
 */
export const accessDeniedMessage =
  "Tu cuenta no tiene acceso a Paquetenvia; pídelo a un administrador";

export const emailNotVerifiedMessage =
  "Verifica tu correo en AuthCenter y vuelve a iniciar sesión";

export const genericSignInFailedMessage =
  "No fue posible completar el inicio de sesión. Intenta de nuevo.";

export function loginErrorMessage(error: string | null | undefined): string | null {
  if (error === null || error === undefined || error === "") {
    return null;
  }
  if (error === "access_denied") {
    return accessDeniedMessage;
  }
  return error === "email_not_verified" ? emailNotVerifiedMessage : genericSignInFailedMessage;
}
