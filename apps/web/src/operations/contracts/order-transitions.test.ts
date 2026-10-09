import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import {
  transitionRejectionCodes,
  transitionRejectionMessage,
  maximumReasonLength,
  nextStepActions,
  normalizePublicIdInput,
  parseAllowedTransitions,
  transitionConfirmation,
  transitionConflictMessage,
  transitionRequestBody,
  type AllowedTransition,
} from "./order-transitions";
import { orderStatuses } from "./operations-dashboard";

const confirm: AllowedTransition = { target_status: "CONFIRMED", required_metadata: ["restricted_goods_acknowledged"] };
const cancel: AllowedTransition = { target_status: "CANCELLED", required_metadata: [] };

describe("order transitions contract (UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05)", () => {
  it("reads AI-05 allowed_transitions exactly", () => {
    expect(
      parseAllowedTransitions([
        { target_status: "CONFIRMED", required_metadata: ["restricted_goods_acknowledged"] },
        { target_status: "CANCELLED", required_metadata: [] },
      ]),
    ).toEqual([confirm, cancel]);
    expect(parseAllowedTransitions([])).toEqual([]);
  });

  it.each([
    [[{ target_status: "SHIPPED", required_metadata: [] }]],
    [[{ target_status: "CANCELLED", required_metadata: ["guard_code"] }]],
    [[{ target_status: "CANCELLED", required_metadata: [], guard: "cancellation_reason_present" }]],
    [[{ target_status: "CANCELLED" }]],
    [[cancel, cancel]],
    [[{ target_status: "FAILED_ATTEMPT", required_metadata: ["incident_id", "incident_id"] }]],
    [{ target_status: "CANCELLED", required_metadata: [] }],
    [null],
  ])("fails closed on %j", (value) => {
    expect(() => parseAllowedTransitions(value)).toThrow();
  });

  it("offers only server-allowed transitions this screen supports, in a fixed order", () => {
    const actions = nextStepActions([
      cancel,
      { target_status: "READY_FOR_PICKUP", required_metadata: [] },
      { target_status: "ASSIGNED", required_metadata: [] },
      { target_status: "AT_PICKUP", required_metadata: [] },
      { target_status: "FAILED_ATTEMPT", required_metadata: ["incident_id"] },
      { target_status: "CLAIM_OPEN", required_metadata: [] },
    ]);
    expect(actions.map((action) => [action.target, action.label, action.danger])).toEqual([
      ["READY_FOR_PICKUP", "Liberar para recolección", false],
      ["CLAIM_OPEN", "Abrir reclamación", false],
      ["CANCELLED", "Cancelar orden", true],
    ]);
    expect(nextStepActions([])).toEqual([]);
    expect(nextStepActions([{ target_status: "CLOSED", required_metadata: [] }])[0].label).toBe("Cerrar orden");
  });

  // UI-NEXT-STEP-RETURNS-CLAIMS-2026-10-09: every AI-04 target is either offered here or
  // explicitly left to another screen, so a new status forces a decision.
  it("offers reschedules, returns and claims, and leaves the rest to their own screens", () => {
    const everyTarget = nextStepActions(
      orderStatuses.map((status) => ({ target_status: status, required_metadata: [] })),
    );
    expect(everyTarget.map((action) => [action.target, action.label, action.reasonLabel, action.danger])).toEqual([
      ["CONFIRMED", "Confirmar orden", "Motivo: confirmar orden", false],
      ["READY_FOR_PICKUP", "Liberar para recolección", "Motivo: liberar para recolección", false],
      ["RESCHEDULED", "Reprogramar entrega", "Motivo de la reprogramación", false],
      ["RETURNING", "Iniciar devolución", "Motivo de la devolución", false],
      ["RETURNED", "Marcar como devuelta", "Detalle de la devolución", false],
      ["CLOSED", "Cerrar orden", "Motivo: cerrar orden", false],
      ["CLAIM_OPEN", "Abrir reclamación", "Motivo de la reclamación", false],
      ["CLAIM_RESOLVED", "Resolver reclamación", "Cómo se resolvió la reclamación", false],
      ["CANCELLED", "Cancelar orden", "Motivo: cancelar orden", true],
    ]);
    const offeredTargets = new Set(everyTarget.map((action) => action.target));
    expect(orderStatuses.filter((status) => !offeredTargets.has(status))).toEqual([
      "DRAFT",
      "ASSIGNED",
      "AT_PICKUP",
      "PICKED_UP",
      "IN_TRANSIT",
      "DELIVERING",
      "FAILED_ATTEMPT",
      "DELIVERED",
    ]);
  });

  it("builds claim and return bodies with only the reason, which the claim guards require", () => {
    // Actions come back in the fixed screen order, not the order the server listed them.
    const [returning, claim, resolution] = nextStepActions([
      { target_status: "CLAIM_OPEN", required_metadata: [] },
      { target_status: "CLAIM_RESOLVED", required_metadata: [] },
      { target_status: "RETURNING", required_metadata: [] },
    ]);
    expect(transitionRequestBody(returning, " Destinatario rechazó ", 7, false)).toEqual({
      target_status: "RETURNING",
      reason: "Destinatario rechazó",
      expected_version: 7,
    });
    expect(transitionRequestBody(claim, "Paquete dañado", 9, false).target_status).toBe("CLAIM_OPEN");
    expect(transitionRequestBody(resolution, "Reembolso acordado", 10, false).target_status).toBe("CLAIM_RESOLVED");
    expect(() => transitionRequestBody(claim, "  ", 9, false)).toThrow();
  });

  it("never shows a transition whose metadata this screen cannot collect", () => {
    expect(nextStepActions([{ target_status: "CANCELLED", required_metadata: ["incident_id"] }])).toEqual([]);
    const [confirmation] = nextStepActions([confirm]);
    expect(confirmation.label).toBe("Confirmar orden");
    expect(confirmation.needsRestrictedGoodsAcknowledgement).toBe(true);
  });

  it("builds the transitionOrder body with a trimmed reason and metadata only when required", () => {
    const [cancelAction] = nextStepActions([cancel]);
    expect(transitionRequestBody(cancelAction, "  Cliente canceló  ", 4, false)).toEqual({
      target_status: "CANCELLED",
      reason: "Cliente canceló",
      expected_version: 4,
    });
    const [confirmAction] = nextStepActions([confirm]);
    expect(transitionRequestBody(confirmAction, "Revisado", 1, true)).toEqual({
      target_status: "CONFIRMED",
      reason: "Revisado",
      expected_version: 1,
      metadata: { restricted_goods_acknowledged: true },
    });
    expect(() => transitionRequestBody(confirmAction, "Revisado", 1, false)).toThrow();
    expect(() => transitionRequestBody(cancelAction, "   ", 1, false)).toThrow();
    expect(() => transitionRequestBody(cancelAction, "x".repeat(maximumReasonLength + 1), 1, false)).toThrow();
    expect(() => transitionRequestBody(cancelAction, "Motivo", 0, false)).toThrow();
    expect(() => transitionRequestBody(cancelAction, "Motivo", 1.5, false)).toThrow();
  });

  it("names the order and the effect in the confirmation", () => {
    const [cancelAction] = nextStepActions([cancel]);
    const view = transitionConfirmation("ORD_AAAAAAAAAAAAAAAAAAAAAA", cancelAction);
    expect(view.title).toBe("¿Cancelar orden?");
    expect(view.description).toContain("ORD_AAAAAAAAAAAAAAAAAAAAAA");
    expect(view.confirmLabel).toBe("Cancelar orden");
    expect(transitionConflictMessage).toBe(
      "No se pudo cambiar el estado; la orden cambió o falta un requisito. Actualiza e intenta de nuevo.",
    );
  });

  it("accepts only the exact tracking number format, ignoring surrounding spaces", () => {
    expect(normalizePublicIdInput("  ORD_abcdefghij-_0123456789  ")).toBe("ORD_abcdefghij-_0123456789");
    for (const value of [
      "",
      "ORD_",
      "ord_abcdefghij-_0123456789",
      "ORD_abcdefghij-_012345678",
      "ORD_abcdefghij-_01234567890",
      "ORD_abcdefghij+/0123456789",
      "ORD_abcdefghij %0123456789",
      "Juan Pérez",
      "6671234567",
    ])
      expect(normalizePublicIdInput(value), value).toBeNull();
  });
});

describe("transition rejection messages (ORD-002-GUARD-CODES-2026-10-05)", () => {
  const openApi = readFileSync(
    resolve(process.cwd(), "../../docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml"),
    "utf8",
  );

  it("covers exactly the AI-05 TransitionConflictProblem.code enum, in order", () => {
    const problem = openApi.slice(
      openApi.indexOf("    TransitionConflictProblem:"),
      openApi.indexOf("    ProofConflictProblem:"),
    );
    const enumBlock = problem.slice(
      problem.indexOf("          enum:"),
      problem.indexOf("          x-ord-002-guard-codes:"),
    );
    const published = [...enumBlock.matchAll(/^ {10}- ([A-Z_]+)$/gm)].map((match) => match[1]);
    expect(published.length).toBe(24);
    expect([...transitionRejectionCodes]).toEqual(published);
  });

  it("names what is missing for the codes the owner asked for", () => {
    expect(transitionRejectionMessage("PICKUP_PROOF_REQUIRED")).toBe("Falta la foto de recolección.");
    expect(transitionRejectionMessage("COD_NOT_RECONCILED")).toBe("El cobro contra entrega aún no está conciliado.");
  });

  it("gives every code its own Spanish sentence without identifiers or codes", () => {
    const messages = transitionRejectionCodes.map((code) => transitionRejectionMessage(code));
    expect(new Set(messages).size).toBe(messages.length);
    for (const message of messages) {
      expect(message).not.toBe(transitionConflictMessage);
      expect(message).toMatch(/^[A-ZÁÉÍÓÚÑ¿][^_{}<>]*[.?]$/);
      expect(message).not.toMatch(/[A-Z]{2,}_|[0-9a-f]{8}-/);
    }
  });

  it.each([null, undefined, "", "CONFLICT", "pickup_proof_complete", "constructor", "toString", "__proto__", "UNKNOWN_RULE"])(
    "falls back to the generic message for %j",
    (code) => {
      expect(transitionRejectionMessage(code)).toBe(transitionConflictMessage);
    },
  );
});
