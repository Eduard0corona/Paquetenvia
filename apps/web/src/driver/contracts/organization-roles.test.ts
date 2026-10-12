import { describe, expect, it } from "vitest";
import {
  OrganizationRolesContractError,
  driverReportRole,
  parseMyOrganizationRoles,
} from "./organization-roles";

/**
 * ORG-MY-ROLES-2026-10-12: GET /api/v1/me/organization-roles is read strictly,
 * and only DRIVER, DISPATCHER and PLATFORM_ADMIN decide who reports
 * "No se pudo entregar".
 */
describe("getMyOrganizationRoles answer", () => {
  it("reads the person's role codes, each once, as the API orders them", () => {
    const roles = parseMyOrganizationRoles({ roles: ["DISPATCHER", "DRIVER"] });
    expect(roles).toEqual(["DISPATCHER", "DRIVER"]);
    expect(Object.isFrozen(roles)).toBe(true);
  });

  it("keeps a role code this app does not know yet, so a new role never breaks the read", () => {
    expect(parseMyOrganizationRoles({ roles: ["DRIVER", "FUTURE_ROLE"] })).toEqual(["DRIVER", "FUTURE_ROLE"]);
  });

  it.each<[string, unknown]>([
    ["an empty list", { roles: [] }],
    ["a repeated role", { roles: ["DRIVER", "DRIVER"] }],
    ["a role that is not text", { roles: ["DRIVER", 7] }],
    ["a role that is not a role code", { roles: ["DRIVER", "driver"] }],
    ["a name instead of a code", { roles: ["Ana López"] }],
    ["more than 64 roles", { roles: Array.from({ length: 65 }, (_, index) => `ROLE_${String.fromCharCode(65 + (index % 26))}${index}`) }],
    ["roles that are not a list", { roles: "DRIVER" }],
    ["null roles", { roles: null }],
    ["an extra member", { roles: ["DRIVER"], organization_id: "11111111-1111-4111-8111-111111111111" }],
    ["no member", {}],
    ["a list", ["DRIVER"]],
    ["null", null],
  ])("refuses %s", (_name, value) => {
    expect(() => parseMyOrganizationRoles(value)).toThrow(OrganizationRolesContractError);
  });
});

describe("who reports a failed delivery", () => {
  it.each<[unknown, ReturnType<typeof driverReportRole>]>([
    [["DRIVER"], "driver"],
    [["DRIVER", "VIEWER"], "driver"],
    [["DRIVER", "FINANCE"], "driver"],
    [["DISPATCHER", "DRIVER"], "dispatch"],
    [["DRIVER", "PLATFORM_ADMIN"], "dispatch"],
    [["DISPATCHER", "DRIVER", "PLATFORM_ADMIN"], "dispatch"],
    // Never a driver there, never known, or malformed: the report fails closed.
    [["DISPATCHER"], "unknown"],
    [null, "unknown"],
    [undefined, "unknown"],
    [[], "unknown"],
    [["DRIVER", "DRIVER"], "unknown"],
    ["DRIVER", "unknown"],
  ])("roles %j report as %s", (roles, expected) => {
    expect(driverReportRole(roles)).toBe(expected);
  });
});
