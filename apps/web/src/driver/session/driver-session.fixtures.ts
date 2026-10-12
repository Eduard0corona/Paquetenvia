import {
  installBffSession,
  selectSessionInstallation,
  type SessionHost,
} from "../../auth/bff-session-installation";
import { parseOrganizationContexts } from "../../operations/contracts/operations-parsers";
import type { DriverSession } from "./driver-session";

/**
 * ORG-CONTEXT-ROLES-2026-10-12: GET /me/organization-contexts as the API
 * answers it for a person with ACTIVE memberships in one organization: one
 * context per organization, `role` and `is_default` from one membership and
 * every role of the person there in `roles` (unique, sorted). `roles: null`
 * leaves the member out, as an API older than this web answered.
 */
export function organizationContextsAnswer(
  organizationId: string,
  roles: readonly string[] | null,
  role = "DRIVER",
): unknown {
  return [
    {
      organization_id: organizationId,
      display_name: "Paquetería Sintética",
      role,
      is_default: true,
      ...(roles === null ? {} : { roles: [...roles].sort() }),
    },
  ];
}

/**
 * The driver session a BFF sign-in installs from `answer`, through the real
 * parser, selection and installation, so a test never injects the roles.
 */
export function driverSessionFromAnswer(
  answer: unknown,
  cacheNamespace: string,
): DriverSession {
  const host: SessionHost = { dispatchEvent: () => true };
  installBffSession(
    host,
    selectSessionInstallation(parseOrganizationContexts(answer)),
    "csrf-memory-only",
    cacheNamespace,
  );
  const session = host.__paquetenviaDriverSession;
  if (session === undefined) {
    throw new Error("The answer does not install a driver session.");
  }
  return session;
}
