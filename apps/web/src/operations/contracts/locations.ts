import { array, boundedString, exactObject, fail, oneOf, uuid } from "./strict-json";

/**
 * UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11: AI-05 listCities (City) and listServiceAreas (ServiceArea), read to name
 * the orders' city of "Crear ruta nueva" in the inbox and to fill the city and service area selectors of "Crear ruta"
 * in Rutas. Ids are keys, never shown; every reader fails closed on the first value outside the contract.
 */
export interface City {
  readonly id: string;
  readonly name: string;
  readonly state_code: string;
  readonly country_code: string;
  readonly timezone: string;
}

export const serviceAreaStatuses = ["ACTIVE", "INACTIVE"] as const;
export type ServiceAreaStatus = (typeof serviceAreaStatuses)[number];

export interface ServiceArea {
  readonly id: string;
  readonly city_id: string;
  readonly name: string;
  readonly status: ServiceAreaStatus;
}

/** Both lists come whole, without pages; anything longer is not this contract. */
const maximumItems = 1_000;

export function parseCity(value: unknown): City {
  const object = exactObject(value, ["id", "name", "state_code", "country_code", "timezone"]);
  return {
    id: uuid(object.id),
    name: boundedString(object.name, 1, 200),
    state_code: boundedString(object.state_code, 1, 32),
    country_code: boundedString(object.country_code, 2, 2),
    timezone: boundedString(object.timezone, 1, 64),
  };
}

/** listCities: the active cities, in the server's order; a repeated city fails the read. */
export function parseCities(value: unknown): readonly City[] {
  const cities = array(value, maximumItems).map(parseCity);
  if (new Set(cities.map((city) => city.id)).size !== cities.length) fail();
  return cities;
}

export function parseServiceArea(value: unknown): ServiceArea {
  const object = exactObject(value, ["id", "city_id", "name", "status"]);
  return {
    id: uuid(object.id),
    city_id: uuid(object.city_id),
    name: boundedString(object.name, 1, 200),
    status: oneOf(object.status, serviceAreaStatuses),
  };
}

/** listServiceAreas for one city: an area of another city or a repeated area fails the read. */
export function parseServiceAreas(value: unknown, cityId: string): readonly ServiceArea[] {
  const areas = array(value, maximumItems).map(parseServiceArea);
  if (areas.some((area) => area.city_id !== cityId)) fail();
  if (new Set(areas.map((area) => area.id)).size !== areas.length) fail();
  return areas;
}

/** "Culiacán, SIN": a city as people read it, by name and state; never its id. */
export function cityLabel(city: City): string {
  return `${city.name}, ${city.state_code}`;
}

/** Only an ACTIVE service area is offered for a route; the server still decides. */
export function activeServiceAreas(areas: readonly ServiceArea[]): readonly ServiceArea[] {
  return areas.filter((area) => area.status === "ACTIVE");
}
