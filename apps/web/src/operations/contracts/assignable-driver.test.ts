import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import {
  activeAssignmentsText,
  assignableDriverSearch,
  assignDriverBody,
  driverAssignmentConfirmation,
  ineligibilityReasonLabels,
  ineligibilityReasons,
  ineligibilityText,
  parseAssignableDriverPage,
  parseAssignment,
  sortForPicker,
  vehicleTypes,
  type AssignableDriver,
} from "./assignable-driver";
import { syntheticUuid } from "./ui-001-screens.fixtures";

const openApi = readFileSync(
  resolve(process.cwd(), "../../docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml"),
  "utf8",
);

function driverResponse(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    driver_id: syntheticUuid(0x601),
    driver_reference: "DRV-0a1b2c3d",
    vehicle_type: "MOTORCYCLE",
    eligible: true,
    ineligibility_reasons: [],
    active_assignment_count: 0,
    ...overrides,
  };
}

function enumAfter(marker: string, property: string): string[] {
  const start = openApi.indexOf(marker);
  const from = openApi.indexOf(`${property}:`, start);
  const enumStart = openApi.indexOf("enum:\n", from);
  const values: string[] = [];
  for (const line of openApi.slice(enumStart + 6).split("\n")) {
    const match = /^\s+- ([A-Z_]+)$/.exec(line);
    if (match === null) break;
    values.push(match[1]);
  }
  return values;
}

describe("AssignableDriverPage contract (UI-PHASE2-DRIVER-PICKER-2026-10-05)", () => {
  it("publishes exactly the vehicle types and reason codes the client knows", () => {
    expect(enumAfter("    AssignableDriver:\n", "vehicle_type")).toEqual([...vehicleTypes]);
    expect(enumAfter("    AssignableDriver:\n", "ineligibility_reasons")).toEqual([...ineligibilityReasons]);
    for (const reason of ineligibilityReasons) expect(ineligibilityReasonLabels[reason]).toMatch(/^[A-ZÁÉÍÓÚ]/);
  });

  it("parses a page with eligible and ineligible drivers", () => {
    const page = parseAssignableDriverPage({
      items: [
        driverResponse(),
        driverResponse({
          driver_id: syntheticUuid(0x602),
          driver_reference: "DRV-ffffffff",
          vehicle_type: "CAR",
          eligible: false,
          ineligibility_reasons: ["HOME_CITY_MISMATCH", "DOCUMENT_EXPIRED"],
          active_assignment_count: 3,
        }),
      ],
      next_cursor: "AdceAAAAAEAAgAAAAAAAAAI",
    });
    expect(page.items).toHaveLength(2);
    expect(page.items[1].ineligibility_reasons).toEqual(["HOME_CITY_MISMATCH", "DOCUMENT_EXPIRED"]);
    expect(page.next_cursor).toBe("AdceAAAAAEAAgAAAAAAAAAI");
  });

  it.each<[string, unknown]>([
    ["an extra key", { ...driverResponse(), name: "Persona" }],
    ["a missing key", Object.fromEntries(Object.entries(driverResponse()).filter(([name]) => name !== "active_assignment_count"))],
    ["a raw id as reference", driverResponse({ driver_reference: syntheticUuid(0x601) })],
    ["an uppercase reference", driverResponse({ driver_reference: "DRV-0A1B2C3D" })],
    ["an unknown vehicle", driverResponse({ vehicle_type: "TRUCK" })],
    ["an unknown reason", driverResponse({ eligible: false, ineligibility_reasons: ["SOMETHING_NEW"] })],
    ["a duplicated reason", driverResponse({ eligible: false, ineligibility_reasons: ["DOCUMENT_EXPIRED", "DOCUMENT_EXPIRED"] })],
    ["eligible with reasons", driverResponse({ ineligibility_reasons: ["DOCUMENT_EXPIRED"] })],
    ["ineligible without reasons", driverResponse({ eligible: false })],
    ["a negative count", driverResponse({ active_assignment_count: -1 })],
    ["a fractional count", driverResponse({ active_assignment_count: 1.5 })],
    ["a string eligibility", driverResponse({ eligible: "true" })],
  ])("rejects %s", (_, item) => {
    expect(() => parseAssignableDriverPage({ items: [item], next_cursor: null })).toThrow();
  });

  it("rejects duplicated drivers, extra page keys and foreign cursors", () => {
    expect(() => parseAssignableDriverPage({ items: [driverResponse(), driverResponse()], next_cursor: null })).toThrow();
    expect(() => parseAssignableDriverPage({ items: [], next_cursor: null, total: 0 })).toThrow();
    expect(() => parseAssignableDriverPage({ items: [], next_cursor: "a b" })).toThrow();
    expect(() => parseAssignableDriverPage({ items: [], next_cursor: "x".repeat(129) })).toThrow();
    expect(() => parseAssignableDriverPage({ items: [], next_cursor: null })).not.toThrow();
  });

  it("sends only the cursor query parameter", () => {
    expect(assignableDriverSearch().toString()).toBe("");
    expect(assignableDriverSearch("AdceAAAAAEAAgAAAAAAAAAI").toString()).toBe("cursor=AdceAAAAAEAAgAAAAAAAAAI");
    expect(() => assignableDriverSearch("../x")).toThrow();
  });

  it("builds an OWN request with integer cents and no route", () => {
    expect(assignDriverBody(syntheticUuid(0x601), 4500)).toEqual({
      driver_id: syntheticUuid(0x601),
      assignment_type: "OWN",
      cost_cents: 4500,
      route_id: null,
    });
    expect(() => assignDriverBody(syntheticUuid(0x601), 45.5)).toThrow();
    expect(() => assignDriverBody(syntheticUuid(0x601), -1)).toThrow();
    expect(() => assignDriverBody("not-an-id", 0)).toThrow();
  });

  it("parses the Assignment response strictly", () => {
    const assignment = {
      id: syntheticUuid(0x701),
      order_id: syntheticUuid(0x101),
      driver_id: syntheticUuid(0x601),
      route_id: null,
      status: "ACCEPTED",
      cost: { currency: "MXN", amount_cents: 4500 },
    };
    expect(parseAssignment(assignment).cost.amount_cents).toBe(4500);
    expect(() => parseAssignment({ ...assignment, cost: { currency: "USD", amount_cents: 4500 } })).toThrow();
    expect(() => parseAssignment({ ...assignment, cost: { currency: "MXN", amount_cents: 45.5 } })).toThrow();
    expect(() => parseAssignment({ ...assignment, extra: true })).toThrow();
  });

  it("explains ineligibility in Spanish without repeating a sentence", () => {
    expect(ineligibilityText(["PACKAGE_LENGTH_EXCEEDED", "PACKAGE_WIDTH_EXCEEDED", "DOCUMENT_EXPIRED"])).toBe(
      "Un paquete no cabe en su vehículo; Tiene un documento vencido",
    );
    expect(activeAssignmentsText(0)).toBe("sin entregas en curso");
    expect(activeAssignmentsText(1)).toBe("1 entrega en curso");
    expect(activeAssignmentsText(4)).toBe("4 entregas en curso");
  });

  it("lists eligible drivers first, then the least busy", () => {
    const drivers = [
      driverResponse({ driver_id: syntheticUuid(1), driver_reference: "DRV-00000001", eligible: false, ineligibility_reasons: ["DOCUMENT_EXPIRED"] }),
      driverResponse({ driver_id: syntheticUuid(2), driver_reference: "DRV-00000002", active_assignment_count: 2 }),
      driverResponse({ driver_id: syntheticUuid(3), driver_reference: "DRV-00000003", active_assignment_count: 0 }),
    ];
    const page = parseAssignableDriverPage({ items: drivers, next_cursor: null });
    expect(sortForPicker(page.items).map((driver) => driver.driver_reference)).toEqual([
      "DRV-00000003",
      "DRV-00000002",
      "DRV-00000001",
    ]);
  });

  it("confirms with the order, the driver reference and the cost, never the id", () => {
    const driver = parseAssignableDriverPage({ items: [driverResponse()], next_cursor: null })
      .items[0] as AssignableDriver;
    const confirmation = driverAssignmentConfirmation("ORD_synthetic", driver, 4500);
    expect(confirmation.title).toBe("¿Asignar repartidor?");
    expect(confirmation.description).toBe(
      "La orden ORD_synthetic se asignará al repartidor DRV-0a1b2c3d (motocicleta) con un costo de $45.00 MXN.",
    );
    expect(confirmation.description).not.toContain(driver.driver_id);
    expect(confirmation.confirmLabel).toBe("Asignar");
  });
});
