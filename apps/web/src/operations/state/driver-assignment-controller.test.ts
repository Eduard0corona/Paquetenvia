import { describe, expect, it, vi } from "vitest";
import type { AssignmentApi } from "../api/assignment-api";
import { TenantApiError } from "../api/tenant-request";
import type { AssignableDriver, AssignableDriverPage, Assignment } from "../contracts/assignable-driver";
import { orderId, syntheticUuid } from "../contracts/ui-001-screens.fixtures";
import { DriverAssignmentController, type DriverAssignmentState } from "./driver-assignment-controller";

const eligible: AssignableDriver = {
  driver_id: syntheticUuid(0x601),
  driver_reference: "DRV-0a1b2c3d",
  vehicle_type: "MOTORCYCLE",
  eligible: true,
  ineligibility_reasons: [],
  active_assignment_count: 0,
};
const ineligible: AssignableDriver = {
  ...eligible,
  driver_id: syntheticUuid(0x602),
  driver_reference: "DRV-ffffffff",
  eligible: false,
  ineligibility_reasons: ["DOCUMENT_EXPIRED"],
};

function page(items: readonly AssignableDriver[], next_cursor: string | null = null): AssignableDriverPage {
  return { items, next_cursor };
}

function assignment(costCents = 4500): Assignment {
  return {
    id: syntheticUuid(0x701),
    order_id: orderId,
    driver_id: eligible.driver_id,
    route_id: null,
    status: "ACCEPTED",
    cost: { currency: "MXN", amount_cents: costCents },
  };
}

function setup(api: Partial<AssignmentApi>) {
  const states: DriverAssignmentState[] = [];
  const onOrderChanged = vi.fn();
  let counter = 0;
  const full: AssignmentApi = {
    listAssignableDrivers: vi.fn().mockResolvedValue(page([eligible, ineligible])),
    assignDriver: vi.fn().mockResolvedValue(assignment()),
    ...api,
  };
  const controller = new DriverAssignmentController(
    full,
    orderId,
    (state) => states.push(state),
    onOrderChanged,
    () => `uuid-${++counter}`,
  );
  return { controller, states, onOrderChanged, api: full };
}

describe("driver assignment controller (UI-PHASE2-DRIVER-PICKER-2026-10-05)", () => {
  it("loads the drivers of the order and pages with the issued cursor", async () => {
    const listAssignableDrivers = vi
      .fn()
      .mockResolvedValueOnce(page([eligible], "AdceAAAAAEAAgAAAAAAAAAI"))
      .mockResolvedValueOnce(page([eligible, ineligible]));
    const { controller } = setup({ listAssignableDrivers });
    await controller.load();
    expect(controller.current).toMatchObject({ kind: "ready", drivers: [eligible], nextCursor: "AdceAAAAAEAAgAAAAAAAAAI" });
    await controller.loadMore();
    expect(listAssignableDrivers.mock.calls[1]).toEqual([orderId, "AdceAAAAAEAAgAAAAAAAAAI"]);
    expect(controller.current.drivers).toEqual([eligible, ineligible]);
    expect(controller.current.nextCursor).toBeNull();
  });

  it("assigns once with integer cents and refetches the order (REST is the source of truth)", async () => {
    const { controller, api, onOrderChanged } = setup({});
    await controller.load();
    await controller.assign(eligible, 4500);
    expect(api.assignDriver).toHaveBeenCalledWith(orderId, eligible.driver_id, 4500, "assign-driver-uuid-1");
    expect(onOrderChanged).toHaveBeenCalledTimes(1);
    expect(controller.current).toMatchObject({
      busy: "idle",
      success: true,
      message: "Repartidor DRV-0a1b2c3d asignado.",
    });
  });

  it("reuses the Idempotency-Key only to retry the same driver and cost after a network failure", async () => {
    const assignDriver = vi
      .fn()
      .mockRejectedValueOnce(new TenantApiError("network"))
      .mockRejectedValueOnce(new TenantApiError("unavailable"))
      .mockResolvedValueOnce(assignment(5000));
    const { controller, onOrderChanged } = setup({ assignDriver });
    await controller.load();
    await controller.assign(eligible, 4500);
    expect(controller.current.message).toContain("Puedes reintentar");
    expect(onOrderChanged).not.toHaveBeenCalled();
    await controller.assign(eligible, 4500);
    await controller.assign(eligible, 5000);
    expect(assignDriver.mock.calls.map((call) => [call[2], call[3]])).toEqual([
      [4500, "assign-driver-uuid-1"],
      [4500, "assign-driver-uuid-1"],
      [5000, "assign-driver-uuid-2"],
    ]);
    expect(onOrderChanged).toHaveBeenCalledTimes(1);
  });

  it("explains a conflict in Spanish, refetches the order and the list, and uses a new key next time", async () => {
    const assignDriver = vi
      .fn()
      .mockRejectedValueOnce(new TenantApiError("conflict", "DRIVER_INELIGIBLE"))
      .mockResolvedValueOnce(assignment());
    const { controller, onOrderChanged, api } = setup({ assignDriver });
    await controller.load();
    await controller.assign(eligible, 4500);
    expect(controller.current.message).toBe(
      "El repartidor ya no cumple los requisitos para esta orden. Revisa la lista actualizada.",
    );
    expect(onOrderChanged).toHaveBeenCalledTimes(1);
    expect(api.listAssignableDrivers).toHaveBeenCalledTimes(2);
    await controller.assign(eligible, 4500);
    expect(assignDriver.mock.calls.map((call) => call[3])).toEqual(["assign-driver-uuid-1", "assign-driver-uuid-2"]);
  });

  it.each<[TenantApiError, string]>([
    [new TenantApiError("conflict", "DRIVER_DOCUMENT_EXPIRED"), "El repartidor tiene un documento vencido. Elige a otro repartidor."],
    [new TenantApiError("conflict", "CONFLICT"), "La orden cambió y ya no admite esta asignación. Se actualizó la información."],
    [new TenantApiError("not_found"), "La orden o el repartidor ya no están disponibles. Se actualizó la información."],
    [new TenantApiError("forbidden"), "Tu rol no permite esta acción en la organización activa."],
  ])("maps %o to a clear message", async (error, message) => {
    const { controller } = setup({ assignDriver: vi.fn().mockRejectedValue(error) });
    await controller.load();
    await controller.assign(eligible, 4500);
    expect(controller.current.message).toBe(message);
    expect(controller.current.message).not.toMatch(/[0-9a-f]{8}-[0-9a-f]{4}/);
  });

  it("offers the MFA step-up back to the order detail", async () => {
    const { controller } = setup({
      assignDriver: vi.fn().mockRejectedValue(new TenantApiError("forbidden", "MFA_REQUIRED", true)),
    });
    await controller.load();
    await controller.assign(eligible, 4500);
    expect(controller.current.stepUpHref).toContain("mfa=required");
    expect(controller.current.stepUpHref).toContain(encodeURIComponent(`/ops/orders/${orderId}`));
  });

  it("never sends an ineligible driver or a non-integer cost", async () => {
    const { controller, api } = setup({});
    await controller.load();
    await controller.assign(ineligible, 4500);
    await controller.assign(eligible, 45.5);
    await controller.assign(eligible, -1);
    expect(api.assignDriver).not.toHaveBeenCalled();
    expect(controller.current.message).toBe("Elige un repartidor disponible y un costo válido.");
  });

  it("reports an order that no longer admits an assignment", async () => {
    const { controller } = setup({
      listAssignableDrivers: vi.fn().mockRejectedValue(new TenantApiError("conflict", "CONFLICT")),
    });
    await controller.load();
    expect(controller.current).toMatchObject({
      kind: "unavailable",
      drivers: [],
      message: "La orden ya no admite asignación. Se actualizó la información.",
    });
  });

  it("ignores answers that arrive after dispose", async () => {
    let resolve: (value: AssignableDriverPage) => void = () => undefined;
    const { controller, states } = setup({
      listAssignableDrivers: vi.fn().mockReturnValue(new Promise((done) => (resolve = done))),
    });
    const loading = controller.load();
    controller.dispose();
    resolve(page([eligible]));
    await loading;
    expect(states.every((state) => state.kind === "loading")).toBe(true);
  });
});
