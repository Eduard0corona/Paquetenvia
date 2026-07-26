import { describe, expect, it } from "vitest";
import { driverStopStatuses, driverStopTypes } from "./driver-stop";
import { driverStopStatusLabel, driverStopTypeLabel } from "./labels";

describe("driver stop labels", () => {
  it("maps all three stop types", () => {
    expect(driverStopTypes.map(driverStopTypeLabel)).toEqual([
      "Recolección",
      "Entrega",
      "Devolución",
    ]);
  });

  it("maps all eight statuses without a raw fallback", () => {
    expect(driverStopStatuses.map(driverStopStatusLabel)).toEqual([
      "Asignada",
      "En punto de recolección",
      "Recolectada",
      "En tránsito",
      "En reparto",
      "Intento fallido",
      "Reprogramada",
      "En devolución",
    ]);
  });
});
