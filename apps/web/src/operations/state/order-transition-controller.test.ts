import { describe, expect, it, vi } from "vitest";
import type { OrderActionsApi } from "../api/order-actions-api";
import { TenantApiError } from "../api/tenant-request";
import { nextStepActions, transitionConflictMessage } from "../contracts/order-transitions";
import { orderId } from "../contracts/ui-001-screens.fixtures";
import { OrderTransitionController, type OrderTransitionState } from "./order-transition-controller";
import { searchOrderByPublicId } from "./order-search";

const [cancel] = nextStepActions([{ target_status: "CANCELLED", required_metadata: [] }]);

function setup(transitionOrder: OrderActionsApi["transitionOrder"]) {
  const states: OrderTransitionState[] = [];
  const onOrderChanged = vi.fn();
  let counter = 0;
  const api: OrderActionsApi = { findOrderIdByPublicId: vi.fn(), transitionOrder: vi.fn(transitionOrder) };
  const controller = new OrderTransitionController(
    api,
    orderId,
    (state) => states.push(state),
    onOrderChanged,
    () => `uuid-${++counter}`,
  );
  return { controller, states, onOrderChanged, api };
}

describe("order transition controller (UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05)", () => {
  it("sends the confirmed transition once and refetches the order", async () => {
    const { controller, states, onOrderChanged, api } = setup(async () => ({ id: orderId, status: "CANCELLED", version: 5 }));
    await controller.submit(cancel, "Cliente canceló", 4, false);
    expect(api.transitionOrder).toHaveBeenCalledWith(orderId, cancel, "Cliente canceló", 4, false, "order-transition-uuid-1");
    expect(states.at(-1)).toMatchObject({ busy: false, success: true });
    expect(onOrderChanged).toHaveBeenCalledTimes(1);
  });

  it("shows the generic conflict text on 409 and refetches", async () => {
    const { controller, states, onOrderChanged } = setup(async () => {
      throw new TenantApiError("conflict", "CONFLICT");
    });
    await controller.submit(cancel, "Motivo", 4, false);
    expect(states.at(-1)).toMatchObject({ busy: false, success: false, message: transitionConflictMessage });
    expect(onOrderChanged).toHaveBeenCalledTimes(1);
  });

  it("reuses the Idempotency-Key only to retry the same payload after a network failure", async () => {
    const transition = vi
      .fn<OrderActionsApi["transitionOrder"]>()
      .mockRejectedValueOnce(new TenantApiError("network"))
      .mockRejectedValueOnce(new TenantApiError("network"))
      .mockResolvedValue({ id: orderId, status: "CANCELLED", version: 5 });
    const { controller, onOrderChanged } = setup(transition);
    await controller.submit(cancel, "Motivo", 4, false);
    expect(onOrderChanged).not.toHaveBeenCalled();
    await controller.submit(cancel, "Motivo ", 4, false);
    await controller.submit(cancel, "Otro motivo", 4, false);
    expect(transition.mock.calls.map((call) => call[5])).toEqual([
      "order-transition-uuid-1",
      "order-transition-uuid-1",
      "order-transition-uuid-2",
    ]);
  });

  it("offers the MFA step-up on 403 MFA_REQUIRED without refetching", async () => {
    const { controller, states, onOrderChanged } = setup(async () => {
      throw new TenantApiError("forbidden", "MFA_REQUIRED", true);
    });
    await controller.submit(cancel, "Motivo", 4, false);
    expect(states.at(-1)?.stepUpHref).toContain("mfa=required");
    expect(onOrderChanged).not.toHaveBeenCalled();
  });

  it("does nothing after dispose", async () => {
    const { controller, api } = setup(async () => ({ id: orderId, status: "CANCELLED", version: 5 }));
    controller.dispose();
    await controller.submit(cancel, "Motivo", 4, false);
    expect(api.transitionOrder).not.toHaveBeenCalled();
  });
});

describe("order search (UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05)", () => {
  it("opens the order for an exact match and reports every other case uniformly", async () => {
    const find = vi.fn<OrderActionsApi["findOrderIdByPublicId"]>().mockResolvedValueOnce(orderId).mockResolvedValueOnce(null);
    await expect(searchOrderByPublicId({ findOrderIdByPublicId: find }, " ORD_abcdefghij-_0123456789 ")).resolves.toEqual({
      kind: "found",
      orderId,
    });
    await expect(searchOrderByPublicId({ findOrderIdByPublicId: find }, "ORD_abcdefghij-_0123456789")).resolves.toEqual({
      kind: "not_found",
    });
    await expect(searchOrderByPublicId({ findOrderIdByPublicId: find }, "María López")).resolves.toEqual({
      kind: "not_found",
    });
    expect(find).toHaveBeenCalledTimes(2);
    expect(find.mock.calls[0][0]).toBe("ORD_abcdefghij-_0123456789");
    find.mockRejectedValueOnce(new TenantApiError("unavailable"));
    await expect(searchOrderByPublicId({ findOrderIdByPublicId: find }, "ORD_abcdefghij-_0123456789")).resolves.toEqual({
      kind: "failed",
    });
  });
});
