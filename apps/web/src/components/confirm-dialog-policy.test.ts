import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const read = (path: string) => readFileSync(path, "utf8");

describe("confirmation before risky money and destructive actions", () => {
  it("uses an accessible native modal dialog that starts on Cancelar and returns focus", () => {
    const source = read("src/components/confirm-dialog.tsx");
    expect(source).toContain("<dialog");
    expect(source).toContain("showModal()");
    expect(source).toContain("aria-labelledby={titleId}");
    expect(source).toContain("aria-describedby={descriptionId}");
    expect(source).toContain("cancelRef.current?.focus()");
    expect(source).toContain('"Cancelar"');
    // Escape fires the native cancel/close; focus goes back to the control that opened it.
    expect(source).toContain("onClose=");
    expect(source).toContain("target.focus()");
    // Cancelar is rendered before the confirming button.
    expect(source.indexOf("ref={cancelRef}")).toBeLessThan(source.indexOf("finish(true)"));
    // A dialog opened after an asynchronous read can name the control that gets focus back.
    expect(source).toContain("returnFocusRef.current = returnFocus ??");
  });

  it.each([
    [
      "src/finance/components/settlements-shell.tsx",
      ["settlementApprovalConfirmation(", "settlementPaymentConfirmation(", "settlementCreationConfirmation("],
      ["controller.approve()", "controller.markPaid()", "controller.create(body)"],
    ],
    [
      "src/finance/components/cod-shell.tsx",
      ["pendingCodReconciliationConfirmation(", "codReconciliationConfirmation(", "codRecordConfirmation("],
      ["controller.reconcileFromList(order.id, record.id)", "controller.reconcile()", "controller.record(amount, reference)"],
    ],
    ["src/operations/components/manual-routes-shell.tsx", ["removeRouteStopConfirmation("], ["state.removeStop(stop.id)"]],
    ["src/operations/components/operations-order-card.tsx", ["externalOfferConfirmation("], ["void onPublishExternalOffer("]],
    ["src/operations/components/operations-driver-assignment.tsx", ["driverAssignmentConfirmation("], ["assign(driver, costCents)"]],
    ["src/operations/components/operations-next-step.tsx", ["transitionConfirmation("], ["submit(action, trimmed, version, acknowledged)"]],
  ] as const)("%s runs each risky action only from the confirmation", (path, builders, actions) => {
    const source = read(path);
    for (const builder of builders) expect(source, builder).toContain(builder);
    for (const action of actions) {
      const occurrences = [...source.matchAll(new RegExp(action.replace(/[.()]/g, "\\$&"), "g"))];
      expect(occurrences.length, action).toBeGreaterThan(0);
      for (const occurrence of occurrences)
        expect(source.slice(Math.max(0, occurrence.index - 400), occurrence.index), action).toContain("onConfirm:");
    }
  });

  it("states money and its target before confirming a COD or settlement action", () => {
    const cod = read("src/finance/components/cod-shell.tsx");
    // Registrar cobro: validated to integer cents first; the dialog shows that amount, never the reference.
    expect(cod).toContain("const body = controller.prepareRecord(amount, reference);");
    expect(cod).toContain("codRecordConfirmation(financials.order_id, body.amount_cents,");
    expect(cod).not.toMatch(/codRecordConfirmation\([^)]*reference/);
    // Pending-list Conciliar: getOrderFinancials is read before the dialog opens; a failed read opens none.
    const readAt = cod.indexOf("await controller.readReconcilableFromList(order.id)");
    expect(readAt).toBeGreaterThan(0);
    expect(cod.indexOf("if (record === null) return;", readAt)).toBeGreaterThan(readAt);
    expect(cod.indexOf("pendingCodReconciliationConfirmation(order, record)")).toBeGreaterThan(readAt);
    expect(cod).toContain("returnFocus: trigger");

    const settlements = read("src/finance/components/settlements-shell.tsx");
    // Calcular liquidación: validated first, then the dialog names driver and period.
    expect(settlements).toContain("const body = controller.prepareCreate({");
    expect(settlements.indexOf("if (body === null) return;")).toBeLessThan(
      settlements.indexOf("settlementCreationConfirmation(body)"),
    );
    // Exportar CSV needs no confirmation, but the screen says the export is audited (D7-SETTLEMENT-RULES).
    expect(settlements).toContain('aria-describedby="settlement-export-note"');
    expect(settlements).toContain("Cada exportación a CSV queda registrada en la bitácora de auditoría.");
    expect(settlements).toContain("onClick={() => void controller.exportCsv()}");
  });
});
