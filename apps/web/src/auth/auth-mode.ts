/**
 * `NEXT_PUBLIC_AUTH_MODE=bff` switches the web to the AuthCenter BFF: the API
 * is reached through the same origin (Next rewrites or the ingress) and the
 * browser only holds the HttpOnly session cookie. Any other value keeps the
 * existing in-memory Mock sessions for local development.
 */
export function isBffAuthenticationEnabled(value: string | undefined): boolean {
  return value === "bff";
}
