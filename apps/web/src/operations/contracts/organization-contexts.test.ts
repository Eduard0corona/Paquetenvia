import { describe, expect, it } from "vitest";
import {
  OperationsContractError,
  parseOrganizationContexts,
} from "./operations-parsers";

/**
 * ORG-CONTEXT-ROLES-2026-10-12: GET /me/organization-contexts answers one
 * context per organization; `roles` lists every role of the person there.
 */
const organizationId = "11111111-1111-4111-8111-111111111111";

function context(patch: Record<string, unknown> = {}) {
  return {
    organization_id: organizationId,
    display_name: "Paquetería Sintética",
    role: "DRIVER",
    is_default: true,
    roles: ["DISPATCHER", "DRIVER"],
    ...patch,
  };
}

describe("organization contexts parser", () => {
  it("reads every role of the person in the organization, next to role and is_default as before", () => {
    const [parsed] = parseOrganizationContexts([context()]);
    expect(parsed).toEqual({
      organization_id: organizationId,
      display_name: "Paquetería Sintética",
      role: "DRIVER",
      is_default: true,
      roles: ["DISPATCHER", "DRIVER"],
    });
    expect(Object.isFrozen(parsed.roles)).toBe(true);
  });

  it("reads a role this web does not know yet like role, so the sign-in never breaks on it", () => {
    expect(parseOrganizationContexts([context({ roles: ["DRIVER", "FUTURE_ROLE"] })])[0].roles).toEqual([
      "DRIVER",
      "FUTURE_ROLE",
    ]);
  });

  it("still reads an answer without roles (an API older than this web) and marks them unknown", () => {
    const older = {
      organization_id: organizationId,
      display_name: "Paquetería Sintética",
      role: "DRIVER",
      is_default: true,
    };
    expect(parseOrganizationContexts([older])).toEqual([
      {
        organization_id: organizationId,
        display_name: "Paquetería Sintética",
        role: "DRIVER",
        is_default: true,
        roles: null,
      },
    ]);
  });

  it.each<[string, Record<string, unknown>]>([
    ["empty roles", { roles: [] }],
    ["roles without role", { roles: ["DISPATCHER"] }],
    ["repeated roles", { roles: ["DRIVER", "DRIVER"] }],
    ["a role that is not text", { roles: ["DRIVER", 7] }],
    ["an empty role", { roles: ["DRIVER", ""] }],
    ["roles that are not a list", { roles: "DRIVER" }],
    ["null roles", { roles: null }],
    ["more than 64 roles", { roles: ["DRIVER", ...Array.from({ length: 64 }, (_, index) => `ROLE_${index}`)] }],
    ["an extra member", { permissions: ["ALL"] }],
  ])("refuses the whole answer with %s", (_name, patch) => {
    expect(() => parseOrganizationContexts([context(patch)])).toThrow(OperationsContractError);
  });
});
