/**
 * The signed-in person as the app shell shows it. Only the display name the BFF already
 * returned (GET /auth/session) is kept, in memory, next to the installed session objects;
 * it is never persisted, logged or sent anywhere.
 */
export interface SessionAccount {
  readonly displayName: string | null;
}

declare global {
  interface Window {
    __paquetenviaAccount?: SessionAccount;
  }
}

/** Name to show in the account menu; a neutral label when the identity has none. */
export function accountLabel(account: SessionAccount | undefined | null): string {
  const name = account?.displayName?.trim();
  return name ? name : "Mi cuenta";
}

export function readSessionAccount(): SessionAccount | null {
  if (typeof window === "undefined") return null;
  return window.__paquetenviaAccount ?? null;
}
