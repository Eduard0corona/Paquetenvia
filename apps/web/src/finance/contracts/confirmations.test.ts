import { describe, expect, it } from "vitest";
import {
  codReconciliationConfirmation,
  pendingCodReconciliationConfirmation,
  settlementAdjustmentConfirmation,
  settlementApprovalConfirmation,
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

  it("names the order before reconciling a COD collection", () => {
    const fromList = pendingCodReconciliationConfirmation({
      id: "00000000-0000-4000-8000-000000000101",
      public_id: "PQ-000123",
      status: "DELIVERED",
    });
    expect(fromList.description).toContain("PQ-000123");
    expect(fromList.confirmLabel).toBe("Conciliar");

    const record = codReconciliationConfirmation({
      id: "00000000-0000-4000-8000-000000000501",
      order_id: "abcdef01-0000-4000-8000-000000000101",
      amount_cents: 15_050,
      status: "RECORDED",
      recorded_at: "2026-09-16T10:00:00Z",
      reconciled_at: null,
    });
    expect(record.description).toContain("$150.50 MXN");
    expect(record.description).toContain("abcdef01");
  });
});
