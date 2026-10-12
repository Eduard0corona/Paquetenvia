import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { transitionRejectionCodes } from "../../operations/contracts/order-transitions";
import {
  codReceivedLabel,
  codRejectionCodeToKeep,
  driverCodRejectionCodes,
  driverGenericConflictMessage,
  driverRejectionMessage,
  transitionRejectionCodeToKeep,
} from "./rejection-codes";

const openApi = readFileSync(
  resolve(process.cwd(), "../../docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml"),
  "utf8",
);

describe("driver rejection messages", () => {
  it("tells the driver to record the cash when the delivery is refused for it", () => {
    expect(driverRejectionMessage("DELIVERY_PROOF", "COD_NOT_RECORDED")).toBe(
      "Falta registrar el cobro contra entrega. Registra el cobro antes de confirmar la entrega.",
    );
    expect(driverRejectionMessage("DELIVERY_PROOF", "COD_NOT_RECORDED")).not.toBe(
      driverGenericConflictMessage,
    );
  });

  it("reuses the ORD-002 rule wording for the other transition codes", () => {
    expect(driverRejectionMessage("PICKUP_PROOF", "PICKUP_PROOF_REQUIRED")).toBe(
      "Falta la foto de recolección.",
    );
    expect(driverRejectionMessage("START_DELIVERY", "VERSION_CONFLICT")).toContain(
      "La orden cambió",
    );
    for (const code of transitionRejectionCodes) {
      expect(driverRejectionMessage("CHECK_IN", code)).not.toBe("");
    }
  });

  it("explains a refused cash collection with the cash messages", () => {
    expect(driverRejectionMessage("COD_COLLECTION", "COD_AMOUNT_MISMATCH")).toBe(
      "El monto a cobrar cambió. Descarta esta acción y revisa el cobro de la parada.",
    );
    for (const code of driverCodRejectionCodes) {
      const message = driverRejectionMessage("COD_COLLECTION", code);
      expect(message).not.toBe(driverGenericConflictMessage);
      expect(message).not.toMatch(/COD|cod_/);
    }
    // A transition code on a cash collection is not a cash rule.
    expect(driverRejectionMessage("COD_COLLECTION", "COD_NOT_RECORDED")).toBe(
      driverGenericConflictMessage,
    );
  });

  it("keeps the generic message without a code", () => {
    expect(driverRejectionMessage("DELIVERY_PROOF", null)).toBe(driverGenericConflictMessage);
    expect(driverGenericConflictMessage).toBe(
      "Esta operación no pudo aplicarse porque la parada cambió. Revisa el estado confirmado antes de continuar.",
    );
  });

  it("keeps only codes AI-05 publishes for the refused request", () => {
    expect(transitionRejectionCodeToKeep("COD_NOT_RECORDED")).toBe("COD_NOT_RECORDED");
    expect(transitionRejectionCodeToKeep("COD_AMOUNT_MISMATCH")).toBeNull();
    expect(transitionRejectionCodeToKeep(null)).toBeNull();
    expect(codRejectionCodeToKeep("COD_AMOUNT_MISMATCH")).toBe("COD_AMOUNT_MISMATCH");
    expect(codRejectionCodeToKeep("COD_NOT_RECORDED")).toBeNull();
    expect(codRejectionCodeToKeep("UPLOAD_SESSION_NOT_READY")).toBeNull();
    const finance = sliceBetween(openApi, "    FinanceConflictProblem:", "    SettlementConflictProblem:");
    for (const code of driverCodRejectionCodes) expect(finance).toContain(`- ${code}`);
  });

  it("names the exact amount on the cash button", () => {
    expect(codReceivedLabel(125_000)).toBe("Recibí $1,250.00");
    expect(codReceivedLabel(1)).toBe("Recibí $0.01");
  });
});

function sliceBetween(source: string, start: string, end: string): string {
  const startIndex = source.indexOf(start);
  const endIndex = source.indexOf(end, startIndex + start.length);
  expect(startIndex).toBeGreaterThanOrEqual(0);
  expect(endIndex).toBeGreaterThan(startIndex);
  return source.slice(startIndex, endIndex);
}
