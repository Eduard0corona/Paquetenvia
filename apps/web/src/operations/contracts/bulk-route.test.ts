import { describe, expect, it } from "vitest";
import type { BulkOrder } from "./bulk-assignment";
import {
  bulkRouteConfirmation,
  bulkRouteOffer,
  bulkRouteOfferText,
  bulkRoutePreviewText,
  bulkRouteRunSummary,
  draftRouteChoices,
  isDraftRouteOf,
  ordersText,
  routeDateText,
  routeStopConflictMessages,
  stopCountText,
  type FinishedBatch,
} from "./bulk-route";
import type { ManualRoute } from "./manual-route";
import { syntheticUuid } from "./ui-001-screens.fixtures";

const driverId = syntheticUuid(0x601);
const otherDriverId = syntheticUuid(0x602);
const uuidText = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/;

function bulkOrder(counter: number): BulkOrder {
  return {
    orderId: syntheticUuid(0x100 + counter),
    publicId: `ORD_${String(counter).padStart(22, "0")}`,
    zoneName: "Centro",
    totalCents: 9_000,
  };
}

const [first, second, third] = [bulkOrder(1), bulkOrder(2), bulkOrder(3)];

function route(counter: number, overrides: Partial<ManualRoute> = {}): ManualRoute {
  return {
    id: syntheticUuid(0x800 + counter),
    status: "DRAFT",
    version: 3,
    driver_id: driverId,
    city_id: syntheticUuid(0x900),
    service_area_id: null,
    scheduled_for: "2026-10-12",
    assignment_cost_cents_total: 9_000,
    stop_count: 2,
    ...overrides,
  };
}

function batch(overrides: Partial<FinishedBatch> = {}): FinishedBatch {
  return {
    phase: "done",
    orders: [first, second, third],
    run: { driver: { driverId, driverReference: "DRV-0a1b2c3d" } },
    results: {
      [first.orderId]: { kind: "assigned" },
      [second.orderId]: { kind: "failed" },
      [third.orderId]: { kind: "assigned" },
    },
    ...overrides,
  };
}

describe("bulk add to route (UI-PHASE3-INBOX-BULK-ADD-TO-ROUTE-2026-10-11)", () => {
  it("is offered only once the batch ended, with the orders it assigned and its driver", () => {
    expect(bulkRouteOffer(batch())).toEqual({
      driver: { driverId, driverReference: "DRV-0a1b2c3d" },
      orders: [first, third],
    });
    for (const phase of ["loading", "ready", "running"]) expect(bulkRouteOffer(batch({ phase }))).toBeNull();
    expect(bulkRouteOffer(batch({ run: null }))).toBeNull();
    // A batch that assigned nothing offers nothing; unsent, pending or failed orders never join.
    expect(
      bulkRouteOffer(
        batch({
          results: {
            [first.orderId]: { kind: "failed" },
            [second.orderId]: { kind: "not_sent" },
            [third.orderId]: { kind: "pending" },
          },
        }),
      ),
    ).toBeNull();
    // Only the driver id and reference travel: nothing else of the bulk assignment's driver choice.
    const choice = { driverId, driverReference: "DRV-0a1b2c3d", vehicleType: "CAR", activeAssignmentCount: 1, blockedBy: [] };
    const offer = bulkRouteOffer(batch({ run: { driver: choice } }));
    expect(offer?.driver).toEqual({ driverId, driverReference: "DRV-0a1b2c3d" });
  });

  it("says what the offer holds before opening it", () => {
    const offer = bulkRouteOffer(batch());
    expect(offer).not.toBeNull();
    if (offer === null) return;
    expect(bulkRouteOfferText(offer)).toBe(
      "Las 2 órdenes asignadas a DRV-0a1b2c3d pueden ir a una de sus rutas en borrador; antes verás una vista previa.",
    );
    expect(bulkRouteOfferText({ ...offer, orders: [first] })).toBe(
      "La orden asignada a DRV-0a1b2c3d puede ir a una de sus rutas en borrador; antes verás una vista previa.",
    );
  });

  it("writes a route date in words and refuses a date that does not exist", () => {
    expect(routeDateText("2026-10-12")).toBe("12 de octubre de 2026");
    expect(routeDateText("2027-01-01")).toBe("1 de enero de 2027");
    expect(routeDateText("2028-02-29")).toBe("29 de febrero de 2028");
    for (const value of ["2026-02-30", "2026-13-01", "2026-00-10", "0999-01-01", "2026-1-01", "12/10/2026", ""])
      expect(routeDateText(value), value).toBeNull();
  });

  it("accepts only DRAFT routes of the driver with a real date or none", () => {
    expect(isDraftRouteOf(route(1), driverId)).toBe(true);
    expect(isDraftRouteOf(route(1, { scheduled_for: null }), driverId)).toBe(true);
    expect(isDraftRouteOf(route(1), otherDriverId)).toBe(false);
    for (const status of ["PLANNED", "ACTIVE", "COMPLETED", "CANCELLED"] as const)
      expect(isDraftRouteOf(route(1, { status }), driverId), status).toBe(false);
    expect(isDraftRouteOf(route(1, { scheduled_for: "2026-02-31" }), driverId)).toBe(false);
  });

  it("names each route by its date and current stops, newest first, without ids or amounts", () => {
    const choices = draftRouteChoices([
      route(1, { scheduled_for: null, stop_count: 0 }),
      route(2, { scheduled_for: "2026-10-12", stop_count: 1 }),
      route(3, { scheduled_for: null, stop_count: 4 }),
      route(4, { scheduled_for: null, stop_count: 2 }),
      route(5, { scheduled_for: "2026-10-13" }),
    ]);
    expect(choices.map((choice) => choice.label)).toEqual([
      "Ruta sin fecha (la más reciente) · sin paradas",
      "Ruta del 12 de octubre de 2026 · 1 parada",
      "Ruta sin fecha (la segunda más reciente) · 4 paradas",
      "Ruta sin fecha (la tercera más reciente) · 2 paradas",
      "Ruta del 13 de octubre de 2026 · 2 paradas",
    ]);
    expect(choices.map((choice) => choice.route.id)).toEqual([1, 2, 3, 4, 5].map((counter) => syntheticUuid(0x800 + counter)));
    for (const choice of choices) {
      expect(choice.label).not.toMatch(uuidText);
      expect(choice.label).not.toMatch(/\$|MXN|90/);
    }
    // Beyond the tenth route that shares a name, the recency is a number.
    const many = draftRouteChoices(Array.from({ length: 12 }, (_, index) => route(index, { scheduled_for: null })));
    expect(many[9]?.name).toBe("Ruta sin fecha (la décima más reciente)");
    expect(many[11]?.name).toBe("Ruta sin fecha (la 12.ª más reciente)");
  });

  it("counts stops and orders in words", () => {
    expect(stopCountText(0)).toBe("sin paradas");
    expect(stopCountText(1)).toBe("1 parada");
    expect(stopCountText(7)).toBe("7 paradas");
    expect(ordersText(1)).toBe("1 orden");
    expect(ordersText(3)).toBe("3 órdenes");
  });

  it("previews the orders of the driver and, once chosen, the route they go to", () => {
    const [choice] = draftRouteChoices([route(1)]);
    expect(bulkRoutePreviewText("DRV-0a1b2c3d", 2, null)).toBe(
      "2 órdenes de DRV-0a1b2c3d. Se agregan al final de la ruta, una por una y en el orden de esta tabla.",
    );
    expect(bulkRoutePreviewText("DRV-0a1b2c3d", 1, choice ?? null)).toBe(
      "1 orden de DRV-0a1b2c3d a «Ruta del 12 de octubre de 2026». Se agregan al final de la ruta, una por una y en el orden de esta tabla.",
    );
  });

  it("confirms naming the route, the driver and the number of orders", () => {
    const [choice] = draftRouteChoices([route(1)]);
    if (choice === undefined) throw new Error("No route.");
    expect(bulkRouteConfirmation(choice, "DRV-0a1b2c3d", 3)).toEqual({
      title: "¿Agregar 3 órdenes a la ruta?",
      description:
        "Se agregarán 3 órdenes de DRV-0a1b2c3d al final de «Ruta del 12 de octubre de 2026», que hoy tiene 2 paradas, " +
        "en el orden de la vista previa. Se envían una por una y cada fila mostrará su resultado.",
      confirmLabel: "Agregar 3 órdenes",
    });
    const [empty] = draftRouteChoices([route(1, { scheduled_for: null, stop_count: 0 })]);
    if (empty === undefined) throw new Error("No route.");
    const single = bulkRouteConfirmation(empty, "DRV-0a1b2c3d", 1);
    expect(single.title).toBe("¿Agregar 1 orden a la ruta?");
    expect(single.description).toContain("Se agregará 1 orden de DRV-0a1b2c3d al final de «Ruta sin fecha», que hoy no tiene paradas,");
    expect(single.confirmLabel).toBe("Agregar 1 orden");
  });

  it("has an es-MX message for every addRouteStop conflict code, never the code itself", () => {
    expect(Object.keys(routeStopConflictMessages).sort()).toEqual([
      "CONFLICT",
      "DRIVER_INELIGIBLE",
      "INVALID_REQUEST",
      "VERSION_CONFLICT",
    ]);
    for (const [code, message] of Object.entries(routeStopConflictMessages)) {
      expect(message, code).not.toContain(code);
      expect(message, code).not.toMatch(/[A-Z]{2,}_[A-Z]/);
    }
  });

  it("summarizes a run without hiding a partial failure", () => {
    expect(bulkRouteRunSummary(1, 1)).toBe("Se agregó la orden a la ruta.");
    expect(bulkRouteRunSummary(3, 3)).toBe("Se agregaron las 3 órdenes a la ruta.");
    expect(bulkRouteRunSummary(0, 2)).toBe("No se agregó ninguna orden a la ruta. Revisa el motivo en cada fila.");
    expect(bulkRouteRunSummary(1, 3)).toBe("Se agregó 1 de 3 órdenes a la ruta. Revisa el motivo de las demás en su fila.");
    expect(bulkRouteRunSummary(2, 3)).toBe("Se agregaron 2 de 3 órdenes a la ruta. Revisa el motivo de las demás en su fila.");
  });
});
