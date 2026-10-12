import { describe, expect, it } from "vitest";
import {
  activeStopsText,
  organizationDriverSearch,
  organizationDriverStatusLabels,
  organizationDriverStatuses,
  parseOrganizationDriver,
  parseOrganizationDriverPage,
  sortByReference,
} from "./organization-driver";

/** UI-PHASE3-DRIVERS-2026-10-11: AI-05 OrganizationDriver and OrganizationDriverPage, read fail-closed. */
const organizationDriver = {
  driver_id: "d71e0000-0000-4000-8000-000000000002",
  driver_reference: "DRV-3d93b4c6",
  vehicle_type: "MOTORCYCLE",
  status: "ACTIVE",
  active_stop_count: 2,
  last_position_at: "2026-10-11T15:04:05.123456+00:00",
};

describe("organization drivers", () => {
  it("reads exactly the published driver and page", () => {
    expect(parseOrganizationDriver(organizationDriver)).toEqual(organizationDriver);
    expect(parseOrganizationDriver({ ...organizationDriver, last_position_at: null }).last_position_at).toBeNull();
    expect(
      parseOrganizationDriverPage({ items: [organizationDriver], next_cursor: "AgAAAAAAAAAAAAAAAAAAAAB" }),
    ).toEqual({ items: [organizationDriver], next_cursor: "AgAAAAAAAAAAAAAAAAAAAAB" });
    expect(organizationDriverStatuses).toEqual(["PENDING", "ACTIVE", "SUSPENDED"]);
  });

  it.each([
    ["an extra field", { ...organizationDriver, phone: "6670000000" }],
    ["a name", { ...organizationDriver, name: "Persona" }],
    ["a position", { ...organizationDriver, lat: 24.8, lng: -107.4 }],
    ["a missing last position", { ...organizationDriver, last_position_at: undefined }],
    ["a retired driver", { ...organizationDriver, status: "INACTIVE" }],
    ["an unknown vehicle", { ...organizationDriver, vehicle_type: "TRUCK" }],
    ["a negative count", { ...organizationDriver, active_stop_count: -1 }],
    ["a fractional count", { ...organizationDriver, active_stop_count: 1.5 }],
    ["a time without offset", { ...organizationDriver, last_position_at: "2026-10-11T15:04:05" }],
    ["a reference that is not DRV-", { ...organizationDriver, driver_reference: "DRV-3d93b4c" }],
  ])("refuses %s", (_, value) => {
    const object = Object.fromEntries(Object.entries(value).filter(([, entry]) => entry !== undefined));
    expect(() => parseOrganizationDriver(object)).toThrow();
  });

  it("refuses a page with duplicates, too many items or a cursor that is not opaque", () => {
    expect(() => parseOrganizationDriverPage({ items: [organizationDriver, organizationDriver], next_cursor: null })).toThrow();
    expect(() =>
      parseOrganizationDriverPage({
        items: Array.from({ length: 101 }, (_, index) => ({
          ...organizationDriver,
          driver_id: `d71e0000-0000-4000-8000-${String(index).padStart(12, "0")}`,
        })),
        next_cursor: null,
      }),
    ).toThrow();
    expect(() => parseOrganizationDriverPage({ items: [], next_cursor: "a/b" })).toThrow();
  });

  it("sends only a cursor", () => {
    expect(organizationDriverSearch().toString()).toBe("");
    expect(organizationDriverSearch("AgAAAAAAAAAAAAAAAAAAAAB").toString()).toBe("cursor=AgAAAAAAAAAAAAAAAAAAAAB");
    expect(() => organizationDriverSearch("x".repeat(129))).toThrow();
  });

  it("words statuses and stops in es-MX and lists drivers by reference", () => {
    expect(organizationDriverStatusLabels).toEqual({
      PENDING: "Pendiente de activar",
      ACTIVE: "Activo",
      SUSPENDED: "Suspendido",
    });
    expect([0, 1, 3].map(activeStopsText)).toEqual(["Sin paradas", "1 parada", "3 paradas"]);
    const drivers = [
      { ...parseOrganizationDriver(organizationDriver), driver_reference: "DRV-ffffffff" },
      { ...parseOrganizationDriver(organizationDriver), driver_id: "d71e0000-0000-4000-8000-000000000001" },
    ];
    expect(sortByReference(drivers).map((driver) => driver.driver_reference)).toEqual(["DRV-3d93b4c6", "DRV-ffffffff"]);
  });
});
