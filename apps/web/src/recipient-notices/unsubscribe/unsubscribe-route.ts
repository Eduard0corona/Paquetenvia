/**
 * The unsubscribe token of an e-mail link /avisos/baja/:token (AI-07): 88 Base64URL characters, the format the API
 * issues (NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 L4 choice 7). A path with anything else is an invalid link at once,
 * with no request; the API checks the token itself.
 */
const UNSUBSCRIBE_PATH = /^\/avisos\/baja\/([A-Za-z0-9_-]{88})\/?$/;

export type RecipientUnsubscribeRoute =
  | { readonly kind: "unsubscribe"; readonly token: string }
  | { readonly kind: "invalid" };

export function parseRecipientUnsubscribePathname(pathname: string): RecipientUnsubscribeRoute {
  const match = UNSUBSCRIBE_PATH.exec(pathname);
  return match === null ? { kind: "invalid" } : { kind: "unsubscribe", token: match[1] };
}
