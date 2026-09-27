/**
 * Messages for `/login?error=…` set by the API after a failed AuthCenter callback.
 * Only `access_denied` (AUTH-001-ACCESS-DENIED-MESSAGE) has its own message; every
 * other value, known or not, shows the same generic text and no protocol detail.
 */
export const accessDeniedMessage =
  "Tu cuenta no tiene acceso a Paquetenvia; pídelo a un administrador";

export const genericSignInFailedMessage =
  "No fue posible completar el inicio de sesión. Intenta de nuevo.";

export function loginErrorMessage(error: string | null | undefined): string | null {
  if (error === null || error === undefined || error === "") {
    return null;
  }
  return error === "access_denied" ? accessDeniedMessage : genericSignInFailedMessage;
}
