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
  createSettlement: ["FINANCE", "PLATFORM_ADMIN"],
  // AI05-LIST-SETTLEMENTS: same capability as getSettlement.
  listSettlements: ["FINANCE", "PLATFORM_ADMIN"],
  getSettlement: ["FINANCE", "PLATFORM_ADMIN"],
  addSettlementAdjustment: ["FINANCE", "PLATFORM_ADMIN"],
  approveSettlement: ["FINANCE", "PLATFORM_ADMIN"],
  markSettlementPaid: ["FINANCE", "PLATFORM_ADMIN"],
  voidSettlement: ["FINANCE", "PLATFORM_ADMIN"],
  exportSettlementCsv: ["FINANCE", "PLATFORM_ADMIN"],
  // x-capability-matrix tracking_link_operations (TRK-002-ISSUE-ENDPOINT).
  issueTrackingLink: ["DISPATCHER", "PLATFORM_ADMIN"],
  revokeTrackingLink: ["DISPATCHER", "PLATFORM_ADMIN"],
} as const satisfies Readonly<Record<string, readonly string[]>>;

export type CapabilityOperation = keyof typeof capabilityMatrix;

/**
 * Operations that need a satisfied MFA challenge for a given role. D7-SETTLEMENT-MFA:
 * approve and pay need MFA for every permitted role; SET-001: PLATFORM_ADMIN needs
 * MFA for every settlement operation; TRK-002-ISSUE-ENDPOINT: PLATFORM_ADMIN needs
 * MFA to issue or revoke a tracking link (AI-01 section 7 safer default, pending
 * owner confirmation). DISPATCHER never needs MFA. The client cannot see the MFA evidence, so
 * this only drives an explanatory hint; the API answers `403 MFA_REQUIRED`.
 */
const mfaOperations: Readonly<Partial<Record<string, readonly CapabilityOperation[]>>> = {
  FINANCE: ["approveSettlement", "markSettlementPaid"],
  PLATFORM_ADMIN: [
    "createSettlement",
    "listSettlements",
    "getSettlement",
    "addSettlementAdjustment",
    "approveSettlement",
    "markSettlementPaid",
    "voidSettlement",
    "exportSettlementCsv",
    "issueTrackingLink",
    "revokeTrackingLink",
  ],
};

export function canPerform(
  role: string | null,
  operation: CapabilityOperation,
): boolean {
  if (role === null) return false;
  return (capabilityMatrix[operation] as readonly string[]).includes(role);
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
