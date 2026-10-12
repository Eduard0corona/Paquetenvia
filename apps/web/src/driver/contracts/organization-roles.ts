/**
 * ORG-MY-ROLES-2026-10-12 (project owner, "Endpoint nuevo de roles
 * (Recommended)"): GET /api/v1/me/organization-roles (AI-05
 * getMyOrganizationRoles) answers the requesting person's own roles in the
 * active organization as {"roles": [...]}. Only role codes: no id, name or any
 * other person's data.
 */

export class OrganizationRolesContractError extends Error {
  public constructor() {
    super("Los roles de la organización no pudieron validarse.");
    this.name = "OrganizationRolesContractError";
  }
}

/** The most role codes an answer or a saved record may carry. */
export const MaximumOrganizationRoles = 64;

const roleCodePattern = /^[A-Z][A-Z_]{0,63}$/;

/**
 * Reads the answer strictly: exactly the member `roles`, a non-empty list of
 * at most {@link MaximumOrganizationRoles} role codes, each once. A code this
 * app does not know yet is kept as a code, so a new role never breaks the read;
 * only DRIVER, DISPATCHER and PLATFORM_ADMIN decide anything here.
 */
export function parseMyOrganizationRoles(value: unknown): readonly string[] {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new OrganizationRolesContractError();
  }
  const keys = Object.keys(value);
  if (keys.length !== 1 || keys[0] !== "roles") {
    throw new OrganizationRolesContractError();
  }
  return parseOrganizationRoleCodes((value as { readonly roles: unknown }).roles);
}

/** A non-empty list of unique role codes (also how the phone checks a saved answer). */
export function parseOrganizationRoleCodes(value: unknown): readonly string[] {
  if (
    !Array.isArray(value) ||
    value.length === 0 ||
    value.length > MaximumOrganizationRoles
  ) {
    throw new OrganizationRolesContractError();
  }
  const roles = value.map((item: unknown) => {
    if (typeof item !== "string" || !roleCodePattern.test(item)) {
      throw new OrganizationRolesContractError();
    }
    return item;
  });
  if (new Set(roles).size !== roles.length) {
    throw new OrganizationRolesContractError();
  }
  return Object.freeze(roles);
}

/**
 * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12 with ORG-MY-ROLES-2026-10-12:
 * who files "No se pudo entregar" in the active organization.
 *
 * - `driver`: the roles are known, include DRIVER and neither DISPATCHER nor
 *   PLATFORM_ADMIN; the report is offered.
 * - `dispatch`: they include DISPATCHER or PLATFORM_ADMIN. The server treats a
 *   report from the phone as dispatch's and refuses the short one, so the stop
 *   says to register the attempt in Despacho (owner answer "Registrar desde
 *   Despacho (Recommended)").
 * - `loading`: no answer is known yet and a request for it is on its way (the
 *   first one after a sign-in, or a retry). The report is not offered yet and
 *   the stop says nothing about the role: it is not an error.
 * - `unknown`: no answer of getMyOrganizationRoles is known for the session
 *   and none is on its way (the request failed with nothing saved, an older
 *   API without the operation, 401 or 403, or a malformed value). The report
 *   is not offered (fails closed) and the stop says why; nothing else of the
 *   app changes.
 */
export type DriverReportRole = "driver" | "dispatch" | "loading" | "unknown";

/**
 * ORG-MY-ROLES-2026-10-12: what the phone knows of the person's roles in the
 * session's organization (DriverRolesController). `roles` is the answer of this
 * page, or else the one saved for the session's partition, and null while none
 * is known; `loading` is true while a request for them is on its way.
 */
export interface DriverOrganizationRolesState {
  readonly roles: readonly string[] | null;
  readonly loading: boolean;
}

/** Who reports, from what the phone knows now: a known answer decides; with none, `loading` or `unknown`. */
export function driverReportRoleOf(
  state: DriverOrganizationRolesState | null | undefined,
): DriverReportRole {
  if (state === null || state === undefined) return "unknown";
  if (state.roles !== null) return driverReportRole(state.roles);
  return state.loading ? "loading" : "unknown";
}

/** Who reports for a known answer (the role codes); anything else is `unknown`. */
export function driverReportRole(organizationRoles: unknown): Exclude<DriverReportRole, "loading"> {
  let roles: readonly string[];
  try {
    roles = parseOrganizationRoleCodes(organizationRoles);
  } catch {
    return "unknown";
  }
  if (!roles.includes("DRIVER")) return "unknown";
  return roles.includes("DISPATCHER") || roles.includes("PLATFORM_ADMIN")
    ? "dispatch"
    : "driver";
}
