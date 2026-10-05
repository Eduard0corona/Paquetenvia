import { clientApiBaseUrl } from "../../lib/api-base-url";
import { createOperationsApi } from "../api/operations-api";
import { resolveActiveRole } from "../contracts/capabilities";
import type { OperationsSession } from "../session/operations-session";

/** @see clientApiBaseUrl — the one fallback for every operations and finance screen. */
export const apiBaseUrl = clientApiBaseUrl;

/** Role in the selected organization from GET /me/organization-contexts (AI-07 global_rules). */
export async function loadActiveRole(
  session: OperationsSession,
  signal: AbortSignal,
): Promise<string | null> {
  const contexts = await createOperationsApi(apiBaseUrl(), session).organizationContexts(signal);
  return resolveActiveRole(contexts, session.organizationId);
}
