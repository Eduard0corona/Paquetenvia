export interface OperationsSession {
  readonly organizationId: string;
  readonly sessionNamespace: string;
  getAccessToken(): string | Promise<string>;
  requestOrganizationChange?(organizationId: string): void | Promise<void>;
}

export const operationsSessionChangedEvent =
  "paquetenvia:operations-session-changed";

declare global {
  interface Window {
    __paquetenviaOperationsSession?: OperationsSession;
  }
}

const uuidPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

export function readOperationsSession(): OperationsSession | null {
  const session = window.__paquetenviaOperationsSession;
  if (
    session === undefined ||
    !uuidPattern.test(session.organizationId) ||
    session.organizationId === "00000000-0000-0000-0000-000000000000" ||
    session.sessionNamespace.length < 1 ||
    session.sessionNamespace.length > 128 ||
    typeof session.getAccessToken !== "function"
  ) {
    return null;
  }
  return session;
}

export function subscribeToOperationsSession(
  listener: () => void,
): () => void {
  window.addEventListener(operationsSessionChangedEvent, listener);
  return () =>
    window.removeEventListener(operationsSessionChangedEvent, listener);
}
