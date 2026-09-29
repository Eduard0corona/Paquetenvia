import { describe, expect, it } from "vitest";
import {
  buildOpenIncidentBody,
  buildResolveIncidentBody,
  incidentListSearch,
  mazatlanLocalToUtc,
  parseIncident,
  parseIncidentPage,
  parseProofIds,
  parseProofPage,
  type OpenIncidentDraft,
} from "./incident";
import { ContractViolationError } from "./strict-json";
import {
  incidentId,
  incidentResponse,
  orderId,
  proofId,
  proofResponse,
  syntheticUuid,
} from "./ui-001-screens.fixtures";

const now = new Date("2026-09-28T17:00:00Z");

function draft(overrides: Partial<OpenIncidentDraft> = {}): OpenIncidentDraft {
  return {
    orderId,
    type: "FAILED_ATTEMPT",
    severity: "MEDIUM",
    reasonCode: "RECIPIENT_ABSENT",
    nextAction: "RESCHEDULED",
    description: "Nadie atendió en el domicilio.",
    occurredAtLocal: "2026-09-28T09:00",
    evidence: proofId,
    ...overrides,
  };
}

describe("Mazatlán wall time", () => {
  it("converts to UTC independently of the device time zone", () => {
    expect(mazatlanLocalToUtc("2026-09-28T09:00")).toBe("2026-09-28T16:00:00.000Z");
    expect(mazatlanLocalToUtc("2026-01-15T23:30")).toBe("2026-01-16T06:30:00.000Z");
  });

  it.each(["", "2026-02-30T10:00", "2026-09-28 09:00", "2026-09-28T24:00", "2026-09-28T09:00:00"])(
    "rejects %j",
    (value) => {
      expect(mazatlanLocalToUtc(value)).toBeNull();
    },
  );
});

describe("openIncident request", () => {
  it("builds the AI-05 OpenIncidentRequest", () => {
    const result = buildOpenIncidentBody(draft({ evidence: `${proofId}, ${syntheticUuid(0x402)}` }), now);
    expect(result).toEqual({
      ok: true,
      body: {
        type: "FAILED_ATTEMPT",
        severity: "MEDIUM",
        description: "Nadie atendió en el domicilio.",
        reason_code: "RECIPIENT_ABSENT",
        next_action: "RESCHEDULED",
        occurred_at: "2026-09-28T16:00:00.000Z",
        evidence_proof_ids: [proofId, syntheticUuid(0x402)],
      },
    });
  });

  it.each<[string, Partial<OpenIncidentDraft>]>([
    ["a non-UUID order", { orderId: "orden-1" }],
    ["a lowercase type", { type: "failed" }],
    ["an unknown severity", { severity: "URGENT" }],
    ["an unknown reason", { reasonCode: "OTHER" }],
    ["an unknown next action", { nextAction: "DELIVERED" }],
    ["an empty description", { description: "" }],
    ["a description over 2000 characters", { description: "x".repeat(2001) }],
    ["a future attempt", { occurredAtLocal: "2026-09-28T10:30" }],
    ["an attempt older than 72 hours", { occurredAtLocal: "2026-09-25T09:59" }],
    ["no evidence", { evidence: " " }],
    ["eleven proofs", { evidence: Array.from({ length: 11 }, (_, index) => syntheticUuid(0x500 + index)).join(",") }],
    ["repeated proofs", { evidence: `${proofId},${proofId}` }],
    ["a non-UUID proof", { evidence: "foto-1" }],
  ])("refuses %s", (_label, overrides) => {
    expect(buildOpenIncidentBody(draft(overrides), now).ok).toBe(false);
  });

  it("accepts exactly 72 hours and the 5-minute clock tolerance", () => {
    expect(buildOpenIncidentBody(draft({ occurredAtLocal: "2026-09-25T10:00" }), now).ok).toBe(true);
    expect(buildOpenIncidentBody(draft({ occurredAtLocal: "2026-09-28T10:05" }), now).ok).toBe(true);
  });

  it("splits proofs on commas, spaces and new lines", () => {
    expect(parseProofIds(` a,b\nc  d `)).toEqual(["a", "b", "c", "d"]);
  });
});

describe("resolveIncident request", () => {
  it("sends the reason exactly as typed", () => {
    expect(buildResolveIncidentBody(incidentId, "RESOLVED", "Se reprogramó con el cliente")).toEqual({
      ok: true,
      body: { outcome: "RESOLVED", reason: "Se reprogramó con el cliente" },
    });
  });

  it.each([
    ["an invalid incident", "x", "RESOLVED", "ok"],
    ["an unknown outcome", incidentId, "CLOSED", "ok"],
    ["a reason with surrounding space", incidentId, "REJECTED", " motivo"],
    ["an empty reason", incidentId, "REJECTED", ""],
    ["a reason over 500 characters", incidentId, "REJECTED", "x".repeat(501)],
    ["a control character", incidentId, "REJECTED", "a\u0007b"],
  ])("refuses %s", (_label, id, outcome, reason) => {
    expect(buildResolveIncidentBody(id, outcome, reason).ok).toBe(false);
  });
});

describe("Incident parser", () => {
  it("accepts the AI-05 Incident shape", () => {
    expect(parseIncident(incidentResponse()).status).toBe("OPEN");
  });

  it.each([
    ["an extra key such as the description", incidentResponse({ description: "texto" })],
    ["an unknown status", incidentResponse({ status: "CLOSED" })],
    ["no evidence", incidentResponse({ evidence_proof_ids: [] })],
    ["repeated evidence", incidentResponse({ evidence_proof_ids: [proofId, proofId] })],
    ["a non-boolean custody", incidentResponse({ custody_acquired: "yes" })],
    ["a timestamp without offset", incidentResponse({ sla_due_at: "2026-09-29T16:00:00" })],
  ])("fails closed on %s", (_label, body) => {
    expect(() => parseIncident(body)).toThrow(ContractViolationError);
  });
});

describe("incident desk read parsers (API-INC-LIST-PROOFS-2026-09-29)", () => {
  it("reads an incident page and its cursor", () => {
    const page = parseIncidentPage({ items: [incidentResponse()], next_cursor: null });
    expect(page.items.map((item) => item.id)).toEqual([incidentId]);
    expect(page.next_cursor).toBeNull();
    expect(parseIncidentPage({ items: [], next_cursor: "abc_DEF-1" }).next_cursor).toBe("abc_DEF-1");
  });

  it.each([
    { items: [] },
    { items: [], next_cursor: null, total: 1 },
    { items: [incidentResponse(), incidentResponse()], next_cursor: null },
    { items: [incidentResponse({ description: "texto" })], next_cursor: null },
    { items: [], next_cursor: "has space" },
    { items: [], next_cursor: "x".repeat(129) },
  ])("fails closed on incident page %#", (value) => {
    expect(() => parseIncidentPage(value)).toThrow(ContractViolationError);
  });

  it("reads proof metadata only", () => {
    const page = parseProofPage({ items: [proofResponse()], next_cursor: null });
    expect(page.items).toEqual([
      { id: proofId, proof_type: "DELIVERY_PHOTO", sha256: "0a".repeat(32), captured_at: "2026-09-28T15:55:00Z" },
    ]);
  });

  it.each([
    proofResponse({ object_key: "proofs/x" }),
    proofResponse({ recipient_name: "Nombre" }),
    proofResponse({ proof_type: "SELFIE" }),
    proofResponse({ sha256: "0A".repeat(32) }),
    proofResponse({ sha256: "0a" }),
    proofResponse({ id: "not-a-uuid" }),
  ])("fails closed on proof %#", (proof) => {
    expect(() => parseProofPage({ items: [proof], next_cursor: null })).toThrow(ContractViolationError);
  });

  it("builds only the published list filters", () => {
    expect(incidentListSearch({ status: "OPEN", orderId }, "c1").toString()).toBe(
      `status=OPEN&order_id=${orderId}&cursor=c1`,
    );
    expect(incidentListSearch({}).toString()).toBe("");
    expect(() => incidentListSearch({ status: "CLOSED" as never })).toThrow(ContractViolationError);
    expect(() => incidentListSearch({ orderId: "not-a-uuid" })).toThrow(ContractViolationError);
    expect(() => incidentListSearch({}, "bad cursor")).toThrow(ContractViolationError);
  });
});
