import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import {
  canPerform,
  capabilityMatrix,
  mayHandleExactCoordinates,
  requiresMfa,
  resolveActiveRole,
  type CapabilityOperation,
} from "./capabilities";

const openApi = readFileSync(
  resolve(process.cwd(), "../../docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml"),
  "utf8",
);

function publishedMatrix(): Record<string, string[]> {
  const start = openApi.indexOf("x-capability-matrix:");
  const operations = openApi.indexOf("  operations:", start);
  const end = openApi.indexOf("  driver_scope:", operations);
  const rows: Record<string, string[]> = {};
  for (const line of openApi.slice(operations, end).split("\n")) {
    const match = /^ {4}(\w+): \[([A-Z_, ]+)\]$/.exec(line);
    if (match) rows[match[1]] = match[2].split(",").map((role) => role.trim());
  }
  return rows;
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

  it("each mirrored operation exists in AI-05", () => {
    for (const operation of Object.keys(capabilityMatrix)) {
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
