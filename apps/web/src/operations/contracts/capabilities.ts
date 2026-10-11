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
  listOrders: ["DISPATCHER", "PLATFORM_ADMIN", "VIEWER"],
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
  // x-capability-matrix tracking_link_operations (TRK-002-ISSUE-ENDPOINT,
  // TRK-002-NO-REVOCATION: nobody revokes a tracking link, so there is no revoke row).
  issueTrackingLink: ["DISPATCHER", "PLATFORM_ADMIN"],
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
  // x-capability-matrix incident_operations (API-INC-LIST-PROOFS-2026-09-29): the
  // incident desk reads admit exactly the resolveIncident roles.
  listIncidents: ["DISPATCHER", "PLATFORM_ADMIN"],
  getIncident: ["DISPATCHER", "PLATFORM_ADMIN"],
  listOrderProofs: ["DISPATCHER", "PLATFORM_ADMIN"],
  recordCodCollection: ["DISPATCHER", "PLATFORM_ADMIN"],
  // UI-PHASE2-DRIVER-PICKER-2026-10-05: the order detail assigns an OWN driver picked from
  // listAssignableDrivers (x-capability-matrix assignable_driver_operations), which admits
  // exactly the assignDriver roles.
  assignDriver: ["DISPATCHER", "PLATFORM_ADMIN"],
  listAssignableDrivers: ["DISPATCHER", "PLATFORM_ADMIN"],
  // UI-PHASE2-QUEUE-COUNTS-2026-10-05: the dashboard indicators read
  // getOperationsQueueCounts (x-capability-matrix operations_queue_operations),
  // which admits exactly the operations dashboard roles.
  getOperationsQueueCounts: ["DISPATCHER", "PLATFORM_ADMIN"],
  // UI-PHASE3-ORDER-WIZARD-2026-10-10: "Nueva orden" confirms the order it creates with
  // transitionOrder, which admits DISPATCHER and PLATFORM_ADMIN with MFA (AI-05
  // TransitionConflict); DRIVER only from /driver for its own assignment.
  transitionOrder: ["DISPATCHER", "PLATFORM_ADMIN"],
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
 * MFA for every settlement operation; TRK-002-NO-REVOCATION: PLATFORM_ADMIN needs
 * MFA to view the tracking link (owner: "Sí, con MFA"). DISPATCHER never needs MFA. The client cannot see the MFA evidence, so
 * this only drives an explanatory hint; the API answers `403 MFA_REQUIRED`.
 */
const mfaOperations: Readonly<Partial<Record<string, readonly CapabilityOperation[]>>> = {
  // FINANCE-COD-MFA-2026-09-27: FINANCE needs MFA for financials and COD reconciliation.
  FINANCE: ["approveSettlement", "markSettlementPaid", "getOrderFinancials", "reconcileCod"],
  PLATFORM_ADMIN: [
    // PLATFORM_ADMIN keeps MFA wherever the operation already demanded it.
    "openIncident",
    "resolveIncident",
    "listIncidents",
    "getIncident",
    "listOrderProofs",
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
    "issueTrackingLink",
    "assignDriver",
    "listAssignableDrivers",
    "getOperationsQueueCounts",
    "transitionOrder",
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

/**
 * FIN-PENDING-COD-LIST-FINANCE-2026-10-02 (literal "finanzas sí ve la lista"): FINANCE
 * may call listOrders only with cod_pending_reconciliation=true, and only with MFA. It
 * never gains any other listOrders call, so it is not in `capabilityMatrix.listOrders`.
 */
export const codPendingListOnlyRoles = ["FINANCE"] as const;

/**
 * API-FIN-COD-VISIBILITY-2026-09-29 and FIN-PENDING-COD-LIST-FINANCE-2026-10-02
 * (x-capability-matrix cod_pending_reconciliation_filter): the COD pending list is
 * requested by a role holding both listOrders and getOrderFinancials (DISPATCHER,
 * PLATFORM_ADMIN with MFA) and by FINANCE (with MFA); VIEWER and DRIVER never request
 * it. A missing second factor is answered by the API with 403 MFA_REQUIRED.
 */
export function canListPendingCod(role: string | null): boolean {
  if (!canPerform(role, "getOrderFinancials")) return false;
  return canPerform(role, "listOrders") || (codPendingListOnlyRoles as readonly (string | null)[]).includes(role);
}

/**
 * AI-05 `x-capability-matrix.low_price_authorization` (LOW-PRICE-MANUAL-AUTH-2026-10-02): the
 * createQuote field `low_price_authorization` admits DISPATCHER and PLATFORM_ADMIN with MFA. It
 * is a field of createQuote, not an operation, so it is kept apart from the operation matrices.
 */
export const lowPriceAuthorizationMatrix = {
  "createQuote.low_price_authorization": ["DISPATCHER", "PLATFORM_ADMIN"],
} as const satisfies Readonly<Record<string, readonly string[]>>;

/** Whether the "Autorizar envío de bajo monto" option is shown; the API remains the barrier. */
export function canAuthorizeLowPrice(role: string | null): boolean {
  if (role === null) return false;
  return (lowPriceAuthorizationMatrix["createQuote.low_price_authorization"] as readonly string[]).includes(role);
}

/** PLATFORM_ADMIN needs a satisfied MFA challenge to authorize; DISPATCHER never does. */
export function lowPriceAuthorizationRequiresMfa(role: string | null): boolean {
  return role === "PLATFORM_ADMIN";
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

/**
 * UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05: "Buscar guía" opens the order detail, which reads the
 * operations dashboard (OBS-001, ADR-OBS-001: DISPATCHER, and PLATFORM_ADMIN with MFA), so the
 * search is offered to the listOrders roles that can also open that detail. VIEWER lists orders
 * but cannot open the detail, so it is not offered the search.
 */
export const orderDetailRoles = ["DISPATCHER", "PLATFORM_ADMIN"] as const;

export function canSearchOrders(role: string | null): boolean {
  return canPerform(role, "listOrders") && (orderDetailRoles as readonly (string | null)[]).includes(role);
}

/**
 * UI-PHASE3-INBOX-2026-10-10: the work inbox reads the operations dashboard (OBS-001) and
 * getOperationsQueueCounts, so it is offered exactly to the roles both admit (DISPATCHER, and
 * PLATFORM_ADMIN with MFA, which the API enforces); VIEWER and every other role are excluded
 * as from the dashboard and the counts.
 */
export function canOpenWorkInbox(role: string | null): boolean {
  return (
    canPerform(role, "getOperationsQueueCounts") && (orderDetailRoles as readonly (string | null)[]).includes(role)
  );
}

/** VIEWER never receives exact coordinates (D5-VIEWER-LOCATION-PRECISION-2026-09-27). */
export function mayHandleExactCoordinates(role: string | null): boolean {
  return role === "DISPATCHER" || role === "PLATFORM_ADMIN";
}
