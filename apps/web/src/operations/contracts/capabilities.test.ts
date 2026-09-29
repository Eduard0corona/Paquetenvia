import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import {
  canPerform,
  capabilityMatrix,
  financeOperationsMatrix,
  screenOperationsMatrix,
  mayHandleExactCoordinates,
  requiresMfa,
  resolveActiveRole,
  type CapabilityOperation,
} from "./capabilities";

const openApi = readFileSync(
  resolve(process.cwd(), "../../docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml"),
  "utf8",
);

const tenantCapabilities = readFileSync(
  resolve(
    process.cwd(),
    "../../src/Modules/Organizations/Organizations.Endpoints/Authorization/TenantCapabilities.cs",
  ),
  "utf8",
);

function publishedSection(section: string, next: string): Record<string, string[]> {
  const start = openApi.indexOf("x-capability-matrix:");
  const from = openApi.indexOf(`\n  ${section}:\n`, start);
  const end = openApi.indexOf(next, from);
  expect(from, section).toBeGreaterThan(start);
  expect(end, section).toBeGreaterThan(from);
  const rows: Record<string, string[]> = {};
  for (const line of openApi.slice(from, end).split("\n")) {
    const match = /^ {4}(\w+): \[([A-Z_, ]+)\]$/.exec(line);
    if (match) rows[match[1]] = match[2].split(",").map((role) => role.trim());
  }
  return rows;
}

function publishedMatrix(): Record<string, string[]> {
  return {
    ...publishedSection("operations", "  driver_scope:"),
    ...publishedSection("tracking_link_operations", "\nx-pilot-contract-deltas:"),
  };
}

describe("D5-CAPABILITY-MATRIX client mirror", () => {
  it("matches every AI-05 x-capability-matrix row it mirrors", () => {
    const published = publishedMatrix();
    for (const [operation, roles] of Object.entries(capabilityMatrix)) {
      // AI05-LIST-SETTLEMENTS: same capabilities as getSettlement.
      const source = operation === "listSettlements" ? "getSettlement" : operation;
      expect(published[source], operation).toEqual([...roles]);
    }
  });

  it("matches every AI-05 finance_operations row it mirrors", () => {
    const published = publishedSection("finance_operations", "  platform_operations_decision:");
    expect(Object.keys(published).length).toBeGreaterThan(0);
    for (const [operation, roles] of Object.entries(financeOperationsMatrix)) {
      expect(published[operation], operation).toEqual([...roles]);
    }
  });

  it("admits on the UI-001 screens no role the API refuses for operations outside the matrix", () => {
    // DRIVER is admitted by the API only for its own assignment, from /driver; these screens exclude it.
    const grants: Record<string, string> = {
      DISPATCHER: "Dispatcher",
      PLATFORM_ADMIN: "PlatformAdminMfa",
    };
    for (const [operation, roles] of Object.entries(screenOperationsMatrix)) {
      const line = new RegExp(`Create\\("${operation}", ([^)]*)\\)`).exec(tenantCapabilities.replace(/\s+/g, " "));
      expect(line, operation).not.toBeNull();
      const enforced = line![1].split(",").map((grant) => grant.trim());
      for (const role of roles) expect(enforced, `${operation} ${role}`).toContain(grants[role]);
      expect(roles).not.toContain("DRIVER");
      expect(roles).not.toContain("FINANCE");
    }
  });

  it("each mirrored operation exists in AI-05", () => {
    for (const operation of [
      ...Object.keys(capabilityMatrix),
      ...Object.keys(financeOperationsMatrix),
      ...Object.keys(screenOperationsMatrix),
    ]) {
      expect(openApi).toContain(`operationId: ${operation}`);
    }
  });

  it.each<[string, CapabilityOperation, boolean]>([
    ["DISPATCHER", "createQuote", true],
    ["DISPATCHER", "createOrder", true],
    ["PLATFORM_ADMIN", "createOrder", true],
    ["VIEWER", "createQuote", false],
    ["VIEWER", "createOrder", false],
    ["FINANCE", "createOrder", false],
    ["DRIVER", "createQuote", false],
    ["CUSTOMER_SUPPORT", "createQuote", false],
    ["FINANCE", "listSettlements", true],
    ["FINANCE", "approveSettlement", true],
    ["PLATFORM_ADMIN", "markSettlementPaid", true],
    ["DISPATCHER", "listSettlements", false],
    ["VIEWER", "exportSettlementCsv", false],
    ["DRIVER", "getSettlement", false],
    ["DISPATCHER", "previewOrderCsv", true],
    ["PLATFORM_ADMIN", "commitOrderCsv", true],
    ["VIEWER", "previewOrderCsv", false],
    ["FINANCE", "commitOrderCsv", false],
    ["DISPATCHER", "openIncident", true],
    ["PLATFORM_ADMIN", "resolveIncident", true],
    ["DRIVER", "openIncident", false],
    ["VIEWER", "resolveIncident", false],
    ["FINANCE", "getOrderFinancials", true],
    ["FINANCE", "reconcileCod", true],
    ["FINANCE", "recordCodCollection", false],
    ["DISPATCHER", "recordCodCollection", true],
    ["DRIVER", "reconcileCod", false],
    ["VIEWER", "getOrderFinancials", false],
    ["DISPATCHER", "issueTrackingLink", true],
    ["PLATFORM_ADMIN", "issueTrackingLink", true],
    ["VIEWER", "issueTrackingLink", false],
    ["FINANCE", "issueTrackingLink", false],
  ])("%s may %s: %s", (role, operation, expected) => {
    expect(canPerform(role, operation)).toBe(expected);
  });

  it("shows nothing without a resolved role", () => {
    expect(canPerform(null, "createQuote")).toBe(false);
    expect(canPerform(null, "listSettlements")).toBe(false);
  });

  it("hints MFA for approve and pay (D7) and every settlement operation of PLATFORM_ADMIN", () => {
    expect(requiresMfa("FINANCE", "approveSettlement")).toBe(true);
    expect(requiresMfa("FINANCE", "markSettlementPaid")).toBe(true);
    expect(requiresMfa("FINANCE", "voidSettlement")).toBe(false);
    expect(requiresMfa("FINANCE", "exportSettlementCsv")).toBe(false);
    expect(requiresMfa("PLATFORM_ADMIN", "listSettlements")).toBe(true);
    expect(requiresMfa("DISPATCHER", "createOrder")).toBe(false);
    expect(requiresMfa(null, "approveSettlement")).toBe(false);
  });

  it("hints MFA for FINANCE financials and reconciliation and PLATFORM_ADMIN incidents and COD", () => {
    expect(requiresMfa("FINANCE", "getOrderFinancials")).toBe(true);
    expect(requiresMfa("FINANCE", "reconcileCod")).toBe(true);
    expect(requiresMfa("DISPATCHER", "reconcileCod")).toBe(false);
    expect(requiresMfa("DISPATCHER", "openIncident")).toBe(false);
    for (const operation of ["openIncident", "resolveIncident", "recordCodCollection", "getOrderFinancials", "reconcileCod"] as const)
      expect(requiresMfa("PLATFORM_ADMIN", operation), operation).toBe(true);
    expect(requiresMfa("PLATFORM_ADMIN", "previewOrderCsv")).toBe(false);
  });

  it("hints MFA for PLATFORM_ADMIN, never DISPATCHER, on the tracking link (TRK-002)", () => {
    expect(requiresMfa("PLATFORM_ADMIN", "issueTrackingLink")).toBe(true);
    expect(requiresMfa("DISPATCHER", "issueTrackingLink")).toBe(false);
    const start = openApi.indexOf("  tracking_link_operations_decision:");
    const decision = openApi.slice(start, openApi.indexOf("  tracking_link_operations:", start));
    expect(decision.replace(/\s+/g, " ")).toContain(
      "DISPATCHER members without MFA and PLATFORM_ADMIN members with a satisfied MFA challenge",
    );
  });

  it("has no tracking link revocation (TRK-002-NO-REVOCATION)", () => {
    expect(Object.keys(capabilityMatrix)).not.toContain("revokeTrackingLink");
    expect(Object.keys(publishedMatrix())).not.toContain("revokeTrackingLink");
    expect(openApi).not.toContain("revokeTrackingLink");
  });

  it("resolves the role of the selected organization only", () => {
    const contexts = [
      { organization_id: "11111111-1111-4111-8111-111111111111", display_name: "A", role: "FINANCE", is_default: true },
      { organization_id: "22222222-2222-4222-8222-222222222222", display_name: "B", role: "VIEWER", is_default: false },
    ];
    expect(resolveActiveRole(contexts, "22222222-2222-4222-8222-222222222222")).toBe("VIEWER");
    expect(resolveActiveRole(contexts, "33333333-3333-4333-8333-333333333333")).toBeNull();
  });

  it("never lets VIEWER handle exact coordinates", () => {
    expect(mayHandleExactCoordinates("VIEWER")).toBe(false);
    expect(mayHandleExactCoordinates("FINANCE")).toBe(false);
    expect(mayHandleExactCoordinates("DISPATCHER")).toBe(true);
    expect(mayHandleExactCoordinates("PLATFORM_ADMIN")).toBe(true);
  });
});
