import { createOperationsApi } from "../api/operations-api";
import { resolveActiveRole } from "../contracts/capabilities";
import type { OperationsSession } from "../session/operations-session";

export function apiBaseUrl(): string {
  return (
    process.env.NEXT_PUBLIC_API_BASE_URL ??
    (typeof window === "undefined" ? "http://127.0.0.1" : window.location.origin)
  );
}

/** Role in the selected organization from GET /me/organization-contexts (AI-07 global_rules). */
export async function loadActiveRole(
  session: OperationsSession,
  signal: AbortSignal,
): Promise<string | null> {
  const contexts = await createOperationsApi(apiBaseUrl(), session).organizationContexts(signal);
  return resolveActiveRole(contexts, session.organizationId);
}
