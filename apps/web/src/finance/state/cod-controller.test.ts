import { describe, expect, it, vi } from "vitest";
import { TenantApiError } from "../../operations/api/tenant-request";
import {
  bearerSession,
  codId,
  codTransactionResponse,
  financialsResponse,
  orderId,
  orgA,
  orgB,
  syntheticUuid,
} from "../../operations/contracts/ui-001-screens.fixtures";
import type { OperationsSession } from "../../operations/session/operations-session";
import { PendingSubmissions } from "../../operations/state/pending-submissions";
import type { CodApi } from "../api/cod-api";
import { parseCodTransaction, parseOrderFinancials } from "../contracts/cod";
import { CodController } from "./cod-controller";

const expected = () => parseOrderFinancials(financialsResponse());
const recorded = () =>
  parseOrderFinancials(
    financialsResponse({}, { status: "RECORDED", amount_cents: 25_050, recorded: true, satisfies_delivery_requirement: true }),
  );

function setup(roles: Record<string, string>, overrides: Partial<CodApi> = {}, initialOrder: string | null = null) {
  let current: OperationsSession | null = bearerSession(orgA);
  const api: CodApi = {
    financials: vi.fn(async () => expected()),
    pendingReconciliation: vi.fn(async () => ({ items: [], next_cursor: null })),
    record: vi.fn(async () => parseCodTransaction(codTransactionResponse())),
    reconcile: vi.fn(async () =>
      parseCodTransaction(codTransactionResponse({ status: "RECONCILED", reconciled_at: "2026-09-28T18:00:00Z" })),
    ),
    ...overrides,
  };
  const pending = new PendingSubmissions();
  const controller = new CodController(
    {
      readSession: () => current,
      createApi: () => api,
      loadRole: async (session) => roles[session.organizationId] ?? null,
      initialOrder: () => initialOrder,
    },
    pending,
  );
  return {
    controller,
    api,
    pending,
    switchTo(organizationId: string) {
      current = bearerSession(organizationId);
    },
  };
}

describe("COD capability gating (finance_operations)", () => {
  it("admits DISPATCHER to read, record and reconcile without MFA hint", async () => {
    const { controller } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    expect(controller.getSnapshot()).toMatchObject({ phase: "ready", canRecord: true, canReconcile: true, mfaHint: null });
  });

  it("admits FINANCE to read and reconcile but never to record, with the MFA hint", async () => {
    const { controller, api } = setup({ [orgA]: "FINANCE" });
    await controller.start();
    expect(controller.getSnapshot()).toMatchObject({ phase: "ready", canRecord: false, canReconcile: true });
    expect(controller.getSnapshot().mfaHint).toContain("MFA");
    await controller.load(orderId);
    await controller.record("250.50", "Recibo 17");
    expect(api.record).not.toHaveBeenCalled();
  });

  it("hints MFA for PLATFORM_ADMIN", async () => {
    const { controller } = setup({ [orgA]: "PLATFORM_ADMIN" });
    await controller.start();
    expect(controller.getSnapshot().mfaHint).toContain("MFA");
  });

  it.each(["VIEWER", "DRIVER", "CUSTOMER_SUPPORT"])("never reads financials for %s", async (role) => {
    const { controller, api } = setup({ [orgA]: role }, {}, orderId);
    await controller.start();
    await controller.load(orderId);
    expect(controller.getSnapshot().phase).toBe("access_unavailable");
    expect(api.financials).not.toHaveBeenCalled();
  });
});

describe("COD flows", () => {
  it("records the exact expected cents and refetches the REST position", async () => {
    const financials = vi.fn<CodApi["financials"]>().mockResolvedValueOnce(expected()).mockResolvedValueOnce(recorded());
    const { controller, api } = setup({ [orgA]: "DISPATCHER" }, { financials });
    await controller.start();
    await controller.load(orderId);
    await controller.record("250.50", "Recibo 17");
    expect(vi.mocked(api.record).mock.calls[0][1]).toEqual({ amount_cents: 25_050, reference: "Recibo 17" });
    expect(financials).toHaveBeenCalledTimes(2);
    expect(controller.getSnapshot().financials?.cod.status).toBe("RECORDED");
    expect(controller.getSnapshot().transaction?.id).toBe(codId);
    expect(controller.getSnapshot().message).toBe("Cobro registrado.");
  });

  it("refuses an amount that differs from the expectation without calling the API", async () => {
    const { controller, api } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    await controller.load(orderId);
    await controller.record("250.49", "Recibo 17");
    expect(api.record).not.toHaveBeenCalled();
    expect(controller.getSnapshot().errors[0]).toContain("exactamente");
  });

  it("reconciles the record the API returned", async () => {
    const { controller, api } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    await controller.load(orderId);
    await controller.record("250.50", "Recibo 17");
    await controller.reconcile();
    expect(vi.mocked(api.reconcile).mock.calls[0][0]).toBe(codId);
    expect(controller.getSnapshot().transaction?.status).toBe("RECONCILED");
  });

  it("reconciles a typed record id and rejects a malformed one", async () => {
    const { controller, api } = setup({ [orgA]: "FINANCE" });
    await controller.start();
    await controller.reconcile("no-es-uuid");
    expect(api.reconcile).not.toHaveBeenCalled();
    await controller.reconcile(codId);
    expect(api.reconcile).toHaveBeenCalledOnce();
  });

  it("fails closed when the reconciled record is another one", async () => {
    const { controller } = setup(
      { [orgA]: "DISPATCHER" },
      { reconcile: vi.fn(async () => parseCodTransaction(codTransactionResponse({ id: syntheticUuid(0x777) }))) },
    );
    await controller.start();
    await controller.reconcile(codId);
    expect(controller.getSnapshot().transaction).toBeNull();
  });

  it("refetches after a conflict and keeps the conflict message", async () => {
    const { controller, api } = setup(
      { [orgA]: "DISPATCHER" },
      { record: vi.fn(async () => { throw new TenantApiError("conflict", "COD_ALREADY_RECORDED"); }) },
    );
    await controller.start();
    await controller.load(orderId);
    await controller.record("250.50", "Recibo 17");
    expect(api.financials).toHaveBeenCalledTimes(2);
    expect(controller.getSnapshot().message).toContain("ya estaba registrado");
  });

  it("reuses the Idempotency-Key on a retry after a network failure", async () => {
    const record = vi
      .fn<CodApi["record"]>()
      .mockRejectedValueOnce(new TenantApiError("network"))
      .mockResolvedValueOnce(parseCodTransaction(codTransactionResponse()));
    const { controller } = setup({ [orgA]: "DISPATCHER" }, { record });
    await controller.start();
    await controller.load(orderId);
    await controller.record("250.50", "Recibo 17");
    await controller.record("250.50", "Recibo 17");
    expect(record.mock.calls[0][2]).toBe(record.mock.calls[1][2]);
  });

  it("offers the MFA step-up and returns to the same order", async () => {
    const { controller } = setup(
      { [orgA]: "FINANCE" },
      { financials: vi.fn(async () => { throw new TenantApiError("forbidden", "MFA_REQUIRED", true); }) },
    );
    await controller.start();
    await controller.load(orderId);
    expect(controller.getSnapshot().stepUpHref).toContain(encodeURIComponent(`/finance/cod?order=${orderId}`));
  });

  it("reopens the order named in the return URL", async () => {
    const { controller, api } = setup({ [orgA]: "DISPATCHER" }, {}, orderId);
    await controller.start();
    expect(api.financials).toHaveBeenCalledOnce();
    expect(controller.getSnapshot().financials?.order_id).toBe(orderId);
  });
});

describe("COD pending list (API-FIN-COD-VISIBILITY-2026-09-29)", () => {
  const pendingOrder = { id: orderId, public_id: "PQ-000123", status: "DELIVERED" };
  const otherOrder = { id: syntheticUuid(0x102), public_id: "PQ-000124", status: "DELIVERED" };

  it.each(["DISPATCHER", "PLATFORM_ADMIN", "FINANCE"])("lists pending collections for %s on start", async (role) => {
    const pendingReconciliation = vi.fn<CodApi["pendingReconciliation"]>(async () => ({
      items: [pendingOrder],
      next_cursor: null,
    }));
    const { controller } = setup({ [orgA]: role }, { pendingReconciliation });
    await controller.start();
    expect(pendingReconciliation).toHaveBeenCalledWith(null, expect.anything());
    expect(controller.getSnapshot()).toMatchObject({ canListPending: true, pending: [pendingOrder], pendingCursor: null });
  });

  it.each(["VIEWER", "DRIVER"])("never requests the pending list for %s", async (role) => {
    const { controller, api } = setup({ [orgA]: role });
    await controller.start();
    await controller.loadPending();
    expect(api.pendingReconciliation).not.toHaveBeenCalled();
    expect(controller.getSnapshot()).toMatchObject({ canListPending: false, pending: null });
  });

  it("appends the next page by cursor without duplicates", async () => {
    const pendingReconciliation = vi
      .fn<CodApi["pendingReconciliation"]>()
      .mockResolvedValueOnce({ items: [pendingOrder], next_cursor: "page-2" })
      .mockResolvedValueOnce({ items: [pendingOrder, otherOrder], next_cursor: null });
    const { controller } = setup({ [orgA]: "DISPATCHER" }, { pendingReconciliation });
    await controller.start();
    await controller.loadPending(true);
    expect(pendingReconciliation.mock.calls[1][0]).toBe("page-2");
    expect(controller.getSnapshot().pending).toEqual([pendingOrder, otherOrder]);
    expect(controller.getSnapshot().pendingCursor).toBeNull();
  });

  it.each(["PLATFORM_ADMIN", "FINANCE"])("offers the MFA step-up to %s when the list is refused for a missing second factor", async (role) => {
    const { controller } = setup(
      { [orgA]: role },
      { pendingReconciliation: vi.fn(async () => { throw new TenantApiError("forbidden", "MFA_REQUIRED", true); }) },
    );
    await controller.start();
    expect(controller.getSnapshot().pending).toBeNull();
    expect(controller.getSnapshot().stepUpHref).toContain(encodeURIComponent("/finance/cod"));
  });

  it("reconciles from the list with the record the financials name and refreshes the list", async () => {
    const financials = vi
      .fn<CodApi["financials"]>()
      .mockResolvedValueOnce(recorded())
      .mockResolvedValue(
        parseOrderFinancials(
          financialsResponse({}, { status: "RECONCILED", amount_cents: 25_050, recorded: true, reconciled: true }),
        ),
      );
    const pendingReconciliation = vi
      .fn<CodApi["pendingReconciliation"]>()
      .mockResolvedValueOnce({ items: [pendingOrder], next_cursor: null })
      .mockResolvedValue({ items: [], next_cursor: null });
    const { controller, api } = setup({ [orgA]: "DISPATCHER" }, { financials, pendingReconciliation });
    await controller.start();
    await controller.reconcileFromList(orderId);
    expect(financials.mock.calls[0][0]).toBe(orderId);
    expect(vi.mocked(api.reconcile).mock.calls[0][0]).toBe(codId);
    expect(pendingReconciliation).toHaveBeenCalledTimes(2);
    expect(controller.getSnapshot()).toMatchObject({ pending: [], message: "Cobro conciliado." });
    expect(controller.getSnapshot().financials?.cod.status).toBe("RECONCILED");
  });

  it("shows the recorded collection as reconcilable once its order is opened", async () => {
    const { controller, api } = setup({ [orgA]: "FINANCE" }, { financials: vi.fn(async () => recorded()) });
    await controller.start();
    await controller.load(orderId);
    expect(controller.getSnapshot().transaction).toMatchObject({ id: codId, status: "RECORDED" });
    await controller.reconcile();
    expect(vi.mocked(api.reconcile).mock.calls[0][0]).toBe(codId);
  });

  it("does not reconcile when the order is no longer pending and refreshes the list", async () => {
    const { controller, api } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    await controller.reconcileFromList(orderId);
    expect(api.reconcile).not.toHaveBeenCalled();
    expect(api.pendingReconciliation).toHaveBeenCalledTimes(2);
    expect(controller.getSnapshot().errors[0]).toContain("ya no está pendiente");
  });

  it("drops the pending list on a tenant switch", async () => {
    const context = setup(
      { [orgA]: "DISPATCHER", [orgB]: "VIEWER" },
      { pendingReconciliation: vi.fn(async () => ({ items: [pendingOrder], next_cursor: null })) },
    );
    await context.controller.start();
    expect(context.controller.getSnapshot().pending).toHaveLength(1);
    context.switchTo(orgB);
    await context.controller.start();
    expect(context.controller.getSnapshot()).toMatchObject({ pending: null, canListPending: false });
  });

  it("replaces the pending list with the new tenant's own for FINANCE after a switch", async () => {
    const pendingReconciliation = vi
      .fn<CodApi["pendingReconciliation"]>()
      .mockResolvedValueOnce({ items: [pendingOrder], next_cursor: null })
      .mockResolvedValueOnce({ items: [otherOrder], next_cursor: null });
    const context = setup({ [orgA]: "DISPATCHER", [orgB]: "FINANCE" }, { pendingReconciliation });
    await context.controller.start();
    context.switchTo(orgB);
    await context.controller.start();
    expect(context.controller.getSnapshot()).toMatchObject({ canListPending: true, pending: [otherOrder] });
  });

  it("lets FINANCE reconcile from the pending list without ever recording", async () => {
    const financials = vi.fn<CodApi["financials"]>().mockResolvedValue(recorded());
    const pendingReconciliation = vi
      .fn<CodApi["pendingReconciliation"]>()
      .mockResolvedValueOnce({ items: [pendingOrder], next_cursor: null })
      .mockResolvedValue({ items: [], next_cursor: null });
    const { controller, api } = setup({ [orgA]: "FINANCE" }, { financials, pendingReconciliation });
    await controller.start();
    await controller.reconcileFromList(orderId);
    expect(vi.mocked(api.reconcile).mock.calls[0][0]).toBe(codId);
    expect(api.record).not.toHaveBeenCalled();
    expect(controller.getSnapshot()).toMatchObject({ canRecord: false, pending: [] });
  });
});

describe("COD tenant switch", () => {
  it("clears the order, the COD record and pending keys", async () => {
    const context = setup(
      { [orgA]: "DISPATCHER", [orgB]: "DISPATCHER" },
      { reconcile: vi.fn(async () => { throw new TenantApiError("network"); }) },
    );
    await context.controller.start();
    await context.controller.load(orderId);
    await context.controller.record("250.50", "Recibo 17");
    await context.controller.reconcile();
    expect(context.pending.size).toBe(1);
    context.switchTo(orgB);
    await context.controller.start();
    expect(context.pending.size).toBe(0);
    expect(context.controller.getSnapshot()).toMatchObject({ financials: null, transaction: null });
  });

  it("ignores financials that resolve after the switch", async () => {
    let resolve!: (value: ReturnType<typeof parseOrderFinancials>) => void;
    const context = setup(
      { [orgA]: "DISPATCHER", [orgB]: "DISPATCHER" },
      { financials: vi.fn(() => new Promise<ReturnType<typeof parseOrderFinancials>>((done) => { resolve = done; })) },
    );
    await context.controller.start();
    const inFlight = context.controller.load(orderId);
    context.switchTo(orgB);
    await context.controller.start();
    resolve(expected());
    await inFlight;
    expect(context.controller.getSnapshot().financials).toBeNull();
  });
});
