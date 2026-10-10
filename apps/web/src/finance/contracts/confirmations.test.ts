import { describe, expect, it } from "vitest";
import {
  codReconciliationConfirmation,
  codRecordConfirmation,
  pendingCodReconciliationConfirmation,
  settlementAdjustmentConfirmation,
  settlementApprovalConfirmation,
  settlementCreationConfirmation,
  settlementPaymentConfirmation,
  settlementVoidConfirmation,
} from "./confirmations";
import type { Settlement } from "./settlement";

const settlement: Settlement = {
  id: "5e7e1e00-0000-4000-8000-000000000001",
  payee_type: "DRIVER",
  payee_id: "d71e0000-0000-4000-8000-000000000002",
  status: "CALCULATED",
  total_cents: 123_456_789,
  period_from: "2026-09-01",
  period_to: "2026-09-15",
  created_at: "2026-09-16T10:00:00Z",
  lines: [],
};

describe("finance confirmations", () => {
  it("states the settlement total in exact pesos and the payee before approving or paying", () => {
    const approval = settlementApprovalConfirmation(settlement);
    expect(approval.confirmLabel).toBe("Aprobar");
    expect(approval.description).toContain("$1,234,567.89 MXN");
    expect(approval.description).toContain("repartidor d71e0000");
    expect(approval.description).toContain("2026-09-01 a 2026-09-15");

    const payment = settlementPaymentConfirmation({ ...settlement, status: "APPROVED", total_cents: 5 });
    expect(payment.confirmLabel).toBe("Marcar pagada");
    expect(payment.description).toContain("$0.05 MXN");
  });

  it("states the exact adjustment and refuses to describe an invalid amount", () => {
    const discount = settlementAdjustmentConfirmation(settlement, "-40.5");
    expect(discount?.confirmLabel).toBe("Agregar ajuste");
    expect(discount?.description).toContain("descontará $40.50 MXN");
    const bonus = settlementAdjustmentConfirmation(settlement, "0.29");
    expect(bonus?.description).toContain("sumará $0.29 MXN");
    expect(settlementAdjustmentConfirmation(settlement, "0")).toBeNull();
    expect(settlementAdjustmentConfirmation(settlement, "1e3")).toBeNull();
  });

  it("warns that voiding a settlement cannot be undone", () => {
    const voided = settlementVoidConfirmation(settlement);
    expect(voided.confirmLabel).toBe("Anular");
    expect(voided.description).toContain("$1,234,567.89 MXN");
    expect(voided.description).toContain("no se puede deshacer");
  });

  it("names the order and states the amount before reconciling a COD collection", () => {
    const recorded = {
      id: "00000000-0000-4000-8000-000000000501",
      order_id: "abcdef01-0000-4000-8000-000000000101",
      amount_cents: 15_050,
      status: "RECORDED" as const,
      recorded_at: "2026-09-16T10:00:00Z",
      reconciled_at: null,
    };
    const fromList = pendingCodReconciliationConfirmation(
      { id: recorded.order_id, public_id: "PQ-000123", status: "DELIVERED" },
      recorded,
    );
    expect(fromList.description).toBe(
      "Se conciliará el cobro de $150.50 MXN de la orden PQ-000123. " +
        "Confirma solo si el efectivo ya se recibió completo.",
    );
    expect(fromList.confirmLabel).toBe("Conciliar");

    const record = codReconciliationConfirmation(recorded);
    expect(record.description).toContain("$150.50 MXN");
    expect(record.description).toContain("abcdef01");
    // The tracking number replaces the short id when the screen has it.
    expect(codReconciliationConfirmation(recorded, "PQ-000123").description).toContain(
      "de la orden PQ-000123.",
    );
  });

  it("states the exact amount and the order before recording a COD collection, never the reference", () => {
    const orderId = "abcdef01-0000-4000-8000-000000000101";
    const record = codRecordConfirmation(orderId, 2_000_000);
    expect(record).toEqual({
      title: "¿Registrar cobro?",
      description:
        "Se registrará el cobro contra entrega de $20,000.00 MXN de la orden abcdef01. " +
        "Después quedará pendiente de conciliar.",
      confirmLabel: "Registrar cobro",
    });
    expect(codRecordConfirmation(orderId, 5, "PQ-000123").description).toContain(
      "$0.05 MXN de la orden PQ-000123.",
    );
  });

  it("names the driver and the period in readable Mazatlán days before calculating", () => {
    const calculation = settlementCreationConfirmation({
      driver_id: "d71e0000-0000-4000-8000-000000000002",
      period_from: "2026-09-01",
      period_to: "2026-09-15",
    });
    expect(calculation).toEqual({
      title: "¿Calcular liquidación?",
      description:
        "Se calculará la liquidación del repartidor d71e0000 del 1 de septiembre de 2026 " +
        "al 15 de septiembre de 2026 (días completos, hora de Mazatlán).",
      confirmLabel: "Calcular",
    });
    // Calendar days are shown as written, never shifted by a zone offset.
    expect(
      settlementCreationConfirmation({
        driver_id: "d71e0000-0000-4000-8000-000000000002",
        period_from: "2026-12-31",
        period_to: "2026-12-31",
      }).description,
    ).toBe(
      "Se calculará la liquidación del repartidor d71e0000 del 31 de diciembre de 2026 " +
        "(día completo, hora de Mazatlán).",
    );
    expect(
      settlementCreationConfirmation({
        driver_id: "d71e0000-0000-4000-8000-000000000002",
        period_from: "2026-01-01",
        period_to: "2026-03-01",
      }).description,
    ).toContain("del 1 de enero de 2026 al 1 de marzo de 2026");
  });
});
