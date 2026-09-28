import type { OperationsOrganizationContext } from "./operations-dashboard";

/**
 * Client mirror of AI-05 `x-capability-matrix` (D5-CAPABILITY-MATRIX) for the
 * operations these screens call. It only decides which actions are shown; the
 * backend capability check and RLS remain the authorization barrier, and any
 * 403 the API returns is still handled.
 */
export const capabilityMatrix = {
  createQuote: ["DISPATCHER", "PLATFORM_ADMIN"],
  getQuote: ["DISPATCHER", "PLATFORM_ADMIN", "VIEWER"],
  createOrder: ["DISPATCHER", "PLATFORM_ADMIN"],
  previewOrderCsv: ["DISPATCHER", "PLATFORM_ADMIN"],
  commitOrderCsv: ["DISPATCHER", "PLATFORM_ADMIN"],
  createSettlement: ["FINANCE", "PLATFORM_ADMIN"],
  // AI05-LIST-SETTLEMENTS: same capability as getSettlement.
  listSettlements: ["FINANCE", "PLATFORM_ADMIN"],
  getSettlement: ["FINANCE", "PLATFORM_ADMIN"],
  addSettlementAdjustment: ["FINANCE", "PLATFORM_ADMIN"],
  approveSettlement: ["FINANCE", "PLATFORM_ADMIN"],
  markSettlementPaid: ["FINANCE", "PLATFORM_ADMIN"],
  voidSettlement: ["FINANCE", "PLATFORM_ADMIN"],
  exportSettlementCsv: ["FINANCE", "PLATFORM_ADMIN"],
} as const satisfies Readonly<Record<string, readonly string[]>>;

/**
 * AI-05 `x-capability-matrix.finance_operations` (FINANCE-COD-RECONCILIATION,
 * FINANCE-COD-MFA-2026-09-27): DISPATCHER, and PLATFORM_ADMIN or FINANCE with MFA.
 */
export const financeOperationsMatrix = {
  getOrderFinancials: ["DISPATCHER", "PLATFORM_ADMIN", "FINANCE"],
  reconcileCod: ["DISPATCHER", "PLATFORM_ADMIN", "FINANCE"],
} as const satisfies Readonly<Record<string, readonly string[]>>;

/**
 * Operations outside the matrix keep their enforced rules (AI-05 operation
 * descriptions; openIncident follows the rule the API enforces). These rows are
 * the roles the UI-001 screens admit (AI-07 incident_desk, cod_control): DRIVER
 * opens incidents and records COD only from its own assignment in /driver, never
 * from these screens, and FINANCE never records a COD collection.
 */
export const screenOperationsMatrix = {
  openIncident: ["DISPATCHER", "PLATFORM_ADMIN"],
  resolveIncident: ["DISPATCHER", "PLATFORM_ADMIN"],
  recordCodCollection: ["DISPATCHER", "PLATFORM_ADMIN"],
} as const satisfies Readonly<Record<string, readonly string[]>>;

const allOperations: Readonly<Record<string, readonly string[]>> = {
  ...capabilityMatrix,
  ...financeOperationsMatrix,
  ...screenOperationsMatrix,
};

export type CapabilityOperation =
  | keyof typeof capabilityMatrix
  | keyof typeof financeOperationsMatrix
  | keyof typeof screenOperationsMatrix;

/**
 * Operations that need a satisfied MFA challenge for a given role. D7-SETTLEMENT-MFA:
 * approve and pay need MFA for every permitted role; SET-001: PLATFORM_ADMIN needs
 * MFA for every settlement operation. The client cannot see the MFA evidence, so
 * this only drives an explanatory hint; the API answers `403 MFA_REQUIRED`.
 */
const mfaOperations: Readonly<Partial<Record<string, readonly CapabilityOperation[]>>> = {
  // FINANCE-COD-MFA-2026-09-27: FINANCE needs MFA for financials and COD reconciliation.
  FINANCE: ["approveSettlement", "markSettlementPaid", "getOrderFinancials", "reconcileCod"],
  PLATFORM_ADMIN: [
    // PLATFORM_ADMIN keeps MFA wherever the operation already demanded it.
    "openIncident",
    "resolveIncident",
    "recordCodCollection",
    "getOrderFinancials",
    "reconcileCod",
    "createSettlement",
    "listSettlements",
    "getSettlement",
    "addSettlementAdjustment",
    "approveSettlement",
    "markSettlementPaid",
    "voidSettlement",
    "exportSettlementCsv",
  ],
};

export function canPerform(
  role: string | null,
  operation: CapabilityOperation,
): boolean {
  if (role === null) return false;
  return allOperations[operation]?.includes(role) ?? false;
}

export function requiresMfa(
  role: string | null,
  operation: CapabilityOperation,
): boolean {
  if (role === null) return false;
  return mfaOperations[role]?.includes(operation) ?? false;
}

/** The role the signed-in person holds in the organization selected with X-Organization-Id. */
export function resolveActiveRole(
  contexts: readonly OperationsOrganizationContext[],
  organizationId: string,
): string | null {
  return (
    contexts.find((context) => context.organization_id === organizationId)?.role ??
    null
  );
}

/** VIEWER never receives exact coordinates (D5-VIEWER-LOCATION-PRECISION-2026-09-27). */
export function mayHandleExactCoordinates(role: string | null): boolean {
  return role === "DISPATCHER" || role === "PLATFORM_ADMIN";
}
