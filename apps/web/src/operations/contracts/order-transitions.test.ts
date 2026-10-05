import { describe, expect, it } from "vitest";
import {
  maximumReasonLength,
  nextStepActions,
  normalizePublicIdInput,
  parseAllowedTransitions,
  transitionConfirmation,
  transitionConflictMessage,
  transitionRequestBody,
  type AllowedTransition,
} from "./order-transitions";

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
      ["CANCELLED", "Cancelar orden", true],
    ]);
    expect(nextStepActions([])).toEqual([]);
    expect(nextStepActions([{ target_status: "CLOSED", required_metadata: [] }])[0].label).toBe("Cerrar orden");
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
