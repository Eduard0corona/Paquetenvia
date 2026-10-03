/**
 * Synthetic AI-05 payloads for the UI-001 CSV, incident and COD tests. Identifiers
 * are built at runtime from small counters so no key-like literal is committed.
 */
import type { OperationsSession } from "../session/operations-session";

export function syntheticUuid(counter: number): string {
  return `00000000-0000-4000-8000-${counter.toString(16).padStart(12, "0")}`;
}

/** A 43-character base64url string of one repeated letter, like a content_digest. */
export function syntheticDigest(letter = "d"): string {
  return letter.repeat(43);
}

/** Synthetic Idempotency-Key-shaped value, clearly not a secret. */
export function syntheticKey(counter: number): string {
  return `synthetic-key-${counter}`.padEnd(36, "0");
}

export const orgA = syntheticUuid(0xa1);
export const orgB = syntheticUuid(0xb2);
export const orderId = syntheticUuid(0x101);
export const quoteId = syntheticUuid(0x201);
export const incidentId = syntheticUuid(0x301);
export const proofId = syntheticUuid(0x401);
export const codId = syntheticUuid(0x501);

export function bearerSession(organizationId = orgA): OperationsSession {
  return { organizationId, sessionNamespace: "synthetic", getAccessToken: () => "ephemeral" };
}

export function previewResponse(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    content_digest: syntheticDigest(),
    total_rows: 1,
    valid_rows: 1,
    invalid_rows: 0,
    file_errors: [],
    rows: [{ row_number: 2, quote_id: quoteId, payer_type: "SENDER", valid: true, errors: [], cod_expected_cents: 0 }],
    ...overrides,
  };
}

export function invalidPreviewResponse(): Record<string, unknown> {
  return previewResponse({
    total_rows: 2,
    valid_rows: 1,
    invalid_rows: 1,
    rows: [
      { row_number: 2, quote_id: quoteId, payer_type: "SENDER", valid: true, errors: [], cod_expected_cents: 15_050 },
      {
        row_number: 3,
        quote_id: null,
        payer_type: null,
        valid: false,
        errors: [{ column: "quote_id", code: "QUOTE_ID_INVALID" }],
      },
    ],
  });
}

export function commitResponse(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    content_digest: syntheticDigest(),
    total_rows: 1,
    created_rows: 1,
    failed_rows: 0,
    rows: [
      {
        row_number: 2,
        quote_id: quoteId,
        status: "CREATED",
        order_id: orderId,
        public_id: "PQ-SYNTH-1",
        error_code: null,
      },
    ],
    ...overrides,
  };
}

export function incidentResponse(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: incidentId,
    order_id: orderId,
    status: "OPEN",
    severity: "MEDIUM",
    reason_code: "RECIPIENT_ABSENT",
    next_action: "RESCHEDULED",
    custody_acquired: true,
    occurred_at: "2026-09-28T16:00:00Z",
    sla_due_at: "2026-09-29T16:00:00Z",
    evidence_proof_ids: [proofId],
    ...overrides,
  };
}

export function proofResponse(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: proofId,
    proof_type: "DELIVERY_PHOTO",
    sha256: "0a".repeat(32),
    captured_at: "2026-09-28T15:55:00Z",
    ...overrides,
  };
}

/**
 * getOrderFinancials. cod_record (API-FIN-COD-VISIBILITY-2026-09-29) follows the
 * COD position unless an override names it: null without a status, otherwise the
 * CodTransaction of that status and amount.
 */
export function financialsResponse(
  overrides: Record<string, unknown> = {},
  cod: Record<string, unknown> = {},
): Record<string, unknown> {
  const position: Record<string, unknown> = {
    expected_cents: 25_050,
    status: null,
    recorded: false,
    reconciled: false,
    satisfies_delivery_requirement: false,
    satisfies_close_requirement: false,
    ...cod,
  };
  const status = position.status;
  const record =
    status === null || status === undefined
      ? null
      : codTransactionResponse({
          status,
          amount_cents: typeof position.amount_cents === "number" ? position.amount_cents : 25_050,
          ...(status === "RECONCILED" ? { reconciled_at: "2026-09-28T18:00:00Z" } : {}),
        });
  return {
    order_id: orderId,
    order_status: "DELIVERING",
    currency: "MXN",
    revenue_cents: 10_000,
    cost_cents: 4_500,
    margin_cents: 5_500,
    margin_basis_points: 5_500,
    cost_by_modality: [
      { modality: "OWN", cost_cents: 4_500, assignment_count: 1 },
      { modality: "EXTERNAL", cost_cents: 0, assignment_count: 0 },
      { modality: "ALLY_CAPACITY", cost_cents: 0, assignment_count: 0 },
    ],
    cod: position,
    cod_record: record,
    ...overrides,
  };
}

export function codTransactionResponse(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: codId,
    order_id: orderId,
    amount_cents: 25_050,
    status: "RECORDED",
    recorded_at: "2026-09-28T17:00:00Z",
    reconciled_at: null,
    ...overrides,
  };
}

/** One AI-05 Order as listOrders returns it, for the COD pending list. */
export function pendingOrderResponse(id: string, publicId: string): Record<string, unknown> {
  return {
    id,
    public_id: publicId,
    owner_org_id: orgA,
    operator_org_id: null,
    status: "DELIVERED",
    price_net: { currency: "MXN", amount_cents: 10_000 },
    version: 3,
    origin_location_id: syntheticUuid(0x601),
    destination_location_id: syntheticUuid(0x602),
    service_type: "SAME_DAY",
    quote_id: quoteId,
    city_id: syntheticUuid(0x701),
    service_area_id: null,
    pricing_tier: "OCCASIONAL",
    total: { currency: "MXN", amount_cents: 10_000 },
    claim_window_ends_at: null,
    finalized_at: null,
    service_window: null,
  };
}

export function jsonResponse(status: number, body: unknown, type = "application/json"): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": type } });
}

export function problem(status: number, code?: string): Response {
  return jsonResponse(
    status,
    { type: "about:blank", title: "Problem", status, ...(code === undefined ? {} : { code }) },
    "application/problem+json",
  );
}
