import { describe, expect, it } from "vitest";
import { externalOfferConfirmation } from "./external-offer-confirmation";
import { dateTimeLocalToUtc, utcToDateTimeLocal } from "./filter-datetime";
import { removeRouteStopConfirmation, routeStatusLabels } from "./manual-route";
import { assignmentTypeLabel } from "./operations-formatters";

describe("external offer confirmation", () => {
  it("states the order, the commission in exact pesos and the Mazatlán expiration", () => {
    const text = externalOfferConfirmation(
      "PQ-000123",
      4_550,
      new Date("2026-10-05T20:30:00Z"),
      "CAR",
    );
    expect(text.confirmLabel).toBe("Publicar oferta");
    expect(text.description).toContain("PQ-000123");
    expect(text.description).toContain("$45.50 MXN");
    expect(text.description).toContain("automóvil");
    // 20:30 UTC is 13:30 in Mazatlán (UTC-7, no daylight saving time).
    expect(text.description).toContain("13:30");
    expect(text.description).toContain("hora de Mazatlán");
  });
});

describe("route stop removal confirmation", () => {
  it("names the stop and the order in human terms", () => {
    const text = removeRouteStopConfirmation({
      id: "44444444-4444-4444-4444-444444444444",
      order_id: "66666666-6666-6666-6666-666666666666",
      sequence: 2,
      stop_type: "DELIVERY",
      status: "PENDING",
    });
    expect(text.confirmLabel).toBe("Retirar");
    expect(text.description).toContain("parada 2");
    expect(text.description).toContain("entrega");
    expect(text.description).toContain("66666666");
    expect(routeStatusLabels.DRAFT).toBe("Borrador");
  });
});

describe("assignment labels", () => {
  it("uses one human label for every assignment type", () => {
    expect(assignmentTypeLabel("OWN")).toBe("Flota propia");
    expect(assignmentTypeLabel("EXTERNAL")).toBe("Externa");
    expect(assignmentTypeLabel("ALLY_CAPACITY")).toBe("Capacidad aliada");
    expect(assignmentTypeLabel(null)).toBe("Sin asignación");
    expect(assignmentTypeLabel(undefined)).toBe("Sin asignación");
    expect(assignmentTypeLabel("SOMETHING_NEW")).toBe("Asignada");
  });
});

describe("dashboard creation-date filter conversions", () => {
  it("round-trips a datetime-local value through the UTC filter", () => {
    const utc = dateTimeLocalToUtc("2026-10-05T08:15");
    expect(utc).toBeDefined();
    expect(utcToDateTimeLocal(utc)).toBe("2026-10-05T08:15");
  });

  it("empties the input when the filter is cleared or invalid", () => {
    expect(dateTimeLocalToUtc("")).toBeUndefined();
    expect(dateTimeLocalToUtc("not a date")).toBeUndefined();
    expect(utcToDateTimeLocal(undefined)).toBe("");
    expect(utcToDateTimeLocal("not a date")).toBe("");
  });
});
