import { describe, expect, it } from "vitest";
import {
  activeServiceAreas,
  cityLabel,
  parseCities,
  parseServiceAreas,
  type City,
} from "./locations";
import { syntheticUuid } from "./ui-001-screens.fixtures";

/** UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11: the AI-05 City and ServiceArea readers. */
const cityId = syntheticUuid(0x900);

function city(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: cityId,
    name: "Culiacán",
    state_code: "SIN",
    country_code: "MX",
    timezone: "America/Mazatlan",
    ...overrides,
  };
}

function area(counter: number, overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return { id: syntheticUuid(0x950 + counter), city_id: cityId, name: `Área ${counter}`, status: "ACTIVE", ...overrides };
}

describe("locations contract (UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11)", () => {
  it("reads the cities exactly as AI-05 City and names each by name and state", () => {
    const [read] = parseCities([city()]);
    expect(read).toEqual(city());
    expect(cityLabel(read as City)).toBe("Culiacán, SIN");
    expect(parseCities([])).toEqual([]);
  });

  it("fails closed on a city outside the contract", () => {
    for (const value of [
      city({ extra: true }),
      city({ id: "not-an-id" }),
      city({ id: "00000000-0000-0000-0000-000000000000" }),
      city({ name: "" }),
      city({ state_code: 1 }),
      city({ country_code: "MEX" }),
      city({ timezone: undefined }),
    ])
      expect(() => parseCities([value])).toThrow();
    expect(() => parseCities([city(), city()])).toThrow();
    expect(() => parseCities({ items: [] })).toThrow();
    expect(() => parseCities(Array.from({ length: 1_001 }, (_, index) => city({ id: syntheticUuid(index + 1) })))).toThrow();
  });

  it("reads the service areas of the city asked for, and keeps only ACTIVE ones to offer", () => {
    const areas = parseServiceAreas([area(1), area(2, { status: "INACTIVE" }), area(3)], cityId);
    expect(areas.map((item) => item.name)).toEqual(["Área 1", "Área 2", "Área 3"]);
    expect(activeServiceAreas(areas).map((item) => item.name)).toEqual(["Área 1", "Área 3"]);
  });

  it("fails closed on an area of another city, a repeated area or a status outside the contract", () => {
    expect(() => parseServiceAreas([area(1, { city_id: syntheticUuid(0x901) })], cityId)).toThrow();
    expect(() => parseServiceAreas([area(1), area(1)], cityId)).toThrow();
    expect(() => parseServiceAreas([area(1, { status: "RETIRED" })], cityId)).toThrow();
    expect(() => parseServiceAreas([area(1, { polygon: "…" })], cityId)).toThrow();
  });
});
