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
  });

  it.each([
    ["src/finance/components/settlements-shell.tsx", ["settlementApprovalConfirmation(", "settlementPaymentConfirmation("], ["controller.approve()", "controller.markPaid()"]],
    ["src/finance/components/cod-shell.tsx", ["pendingCodReconciliationConfirmation(", "codReconciliationConfirmation("], ["controller.reconcileFromList(order.id)", "controller.reconcile()"]],
    ["src/operations/components/manual-routes-shell.tsx", ["removeRouteStopConfirmation("], ["state.removeStop(stop.id)"]],
    ["src/operations/components/operations-order-card.tsx", ["externalOfferConfirmation("], ["void onPublishExternalOffer("]],
    ["src/operations/components/operations-driver-assignment.tsx", ["driverAssignmentConfirmation("], ["assign(driver, costCents)"]],
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
});
