/** Synthetic AI-05 Settlement payloads for tests. */
export const settlementId = "5e7e1e00-0000-4000-8000-000000000001";
export const driverId = "d71e0000-0000-4000-8000-000000000002";
const orderId = "0d0e0000-0000-4000-8000-000000000003";

export function settlementLine(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: "11ee0000-0000-4000-8000-000000000004",
    line_type: "DELIVERY",
    order_id: orderId,
    amount_cents: 4_500,
    source_reference: "dispatch.assignments/a551e000-0000-4000-8000-000000000005",
    created_at: "2026-09-27T08:00:00.123456Z",
    ...overrides,
  };
}

export function adjustmentLine(amountCents: number): Record<string, unknown> {
  return settlementLine({
    id: "11ee0000-0000-4000-8000-000000000006",
    line_type: "ADJUSTMENT",
    order_id: null,
    amount_cents: amountCents,
    source_reference: "platform.audit_logs/a0d10000-0000-4000-8000-000000000007",
  });
}

export function settlementResponse(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: settlementId,
    payee_type: "DRIVER",
    payee_id: driverId,
    status: "CALCULATED",
    total_cents: 4_000,
    period_from: "2026-09-20",
    period_to: "2026-09-26",
    created_at: "2026-09-27T09:00:00Z",
    lines: [settlementLine(), adjustmentLine(-500)],
    ...overrides,
  };
}
