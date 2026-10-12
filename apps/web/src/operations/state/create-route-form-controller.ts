import { TenantApiError } from "../api/tenant-request";
import { activeServiceAreas, type City, type ServiceArea } from "../contracts/locations";
import {
  sortByReference,
  type OrganizationDriver,
  type OrganizationDriverPage,
} from "../contracts/organization-driver";
import type { OperationsSession } from "../session/operations-session";
import { ExternalStore } from "./external-store";
import { describeFailure } from "./tenant-error-messages";

/**
 * UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11: the selectors of "Crear ruta" in Rutas (`/ops/routes`), which no longer
 * asks to paste internal ids. Repartidor lists the ACTIVE drivers of listOrganizationDrivers (every page; createRoute
 * takes an OWN ACTIVE driver), Ciudad lists listCities and Área de servicio lists the ACTIVE areas listServiceAreas
 * returns for the chosen city, plus "Toda la ciudad" (no area). Each list has its loading, empty and error state;
 * only listed values can be chosen, and the form is sent only with a driver and a city. createRoute itself stays the
 * route planner's. Everything lives in memory: a session or organization change starts from empty lists.
 *
 * UI-PHASE3-ROUTES-NO-IDS-2026-10-12: the same read of listOrganizationDrivers also names the driver of the route
 * detail by its DRV- reference, whatever its status (`driverReferences`); a driver that read did not return is
 * "Repartidor no disponible", never an id.
 */
export const routeFormPath = "/ops/routes";

/** listOrganizationDrivers pages (100 drivers each) read before the list is cut; the pilot fleets fit in one. */
export const maximumDriverPages = 10;

export const routeFormDriversFailedMessage = "No fue posible cargar los repartidores.";
export const routeFormCitiesFailedMessage = "No fue posible cargar las ciudades.";
export const routeFormAreasFailedMessage = "No fue posible cargar las áreas de servicio de esa ciudad.";

export type RouteFormList<T> =
  /** Áreas only: no city chosen yet. */
  | { readonly kind: "idle" }
  | { readonly kind: "loading" }
  | { readonly kind: "ready"; readonly items: readonly T[] }
  | { readonly kind: "failed"; readonly message: string; readonly stepUpHref: string | null };

export interface CreateRouteFormState {
  /** ACTIVE drivers only, by DRV- reference. */
  readonly drivers: RouteFormList<OrganizationDriver>;
  /** The DRV- reference of every driver the read returned, any status, by driver id; empty until it is read. */
  readonly driverReferences: ReadonlyMap<string, string>;
  /** More drivers exist than {@link maximumDriverPages} pages hold; the form says some are missing. */
  readonly driversTruncated: boolean;
  readonly cities: RouteFormList<City>;
  /** ACTIVE areas of the chosen city only. */
  readonly areas: RouteFormList<ServiceArea>;
  readonly driverId: string | null;
  readonly cityId: string | null;
  /** null is "Toda la ciudad". */
  readonly serviceAreaId: string | null;
}

/** What the form sends to createRoute, besides the date. */
export interface CreateRouteChoice {
  readonly driverId: string;
  readonly cityId: string;
  readonly serviceAreaId: string | null;
}

export interface CreateRouteFormApi {
  listDrivers(cursor: string | null, signal?: AbortSignal): Promise<OrganizationDriverPage>;
  listCities(signal?: AbortSignal): Promise<readonly City[]>;
  listServiceAreas(cityId: string, signal?: AbortSignal): Promise<readonly ServiceArea[]>;
}

export interface CreateRouteFormDependencies {
  readonly readSession: () => OperationsSession | null;
  readonly createApi: (session: OperationsSession) => CreateRouteFormApi;
}

const noReferences: ReadonlyMap<string, string> = new Map();

const initialState: CreateRouteFormState = {
  drivers: { kind: "loading" },
  driverReferences: noReferences,
  driversTruncated: false,
  cities: { kind: "loading" },
  areas: { kind: "idle" },
  driverId: null,
  cityId: null,
  serviceAreaId: null,
};

class RepeatedCursorError extends Error {
  public constructor() {
    super("listOrganizationDrivers repeated a cursor.");
    this.name = "RepeatedCursorError";
  }
}

export class CreateRouteFormController extends ExternalStore<CreateRouteFormState> {
  private api: CreateRouteFormApi | null = null;
  private generation = 0;
  private abort = new AbortController();
  private areasAbort: AbortController | null = null;
  private areasGeneration = 0;

  public constructor(private readonly dependencies: CreateRouteFormDependencies) {
    super(initialState);
  }

  /** Starts (again) for the installed session: drops every list and choice, then reads the drivers and cities. */
  public async start(): Promise<void> {
    this.reset();
    const session = this.dependencies.readSession();
    this.api = session === null ? null : this.dependencies.createApi(session);
    if (this.api === null) {
      const missing = describeFailure(new TenantApiError("unauthorized"), routeFormPath);
      const failed = { kind: "failed", ...missing } as const;
      this.update({ ...initialState, drivers: failed, cities: failed });
      return;
    }
    this.update(initialState);
    await Promise.all([this.loadDrivers(), this.loadCities()]);
  }

  /** Leaves the screen: cancels every read and forgets the lists. */
  public stop(): void {
    this.reset();
    this.api = null;
    this.update(initialState);
  }

  /** Every page of listOrganizationDrivers, one after another; only ACTIVE drivers are kept, by reference. */
  public async loadDrivers(): Promise<void> {
    const api = this.api;
    if (api === null) return;
    const generation = this.generation;
    this.update({ drivers: { kind: "loading" }, driverReferences: noReferences, driversTruncated: false, driverId: null });
    try {
      const drivers: OrganizationDriver[] = [];
      const cursors = new Set<string>();
      let cursor: string | null = null;
      let truncated = false;
      for (let page = 0; ; page += 1) {
        if (page === maximumDriverPages) {
          truncated = true;
          break;
        }
        const result: OrganizationDriverPage = await api.listDrivers(cursor, this.abort.signal);
        if (generation !== this.generation) return;
        drivers.push(...result.items);
        if (result.next_cursor === null) break;
        if (cursors.has(result.next_cursor)) throw new RepeatedCursorError();
        cursors.add(result.next_cursor);
        cursor = result.next_cursor;
      }
      const unique = [...new Map(drivers.map((driver) => [driver.driver_id, driver])).values()];
      this.update({
        drivers: { kind: "ready", items: sortByReference(unique.filter((driver) => driver.status === "ACTIVE")) },
        driverReferences: new Map(unique.map((driver) => [driver.driver_id, driver.driver_reference])),
        driversTruncated: truncated,
      });
    } catch (error: unknown) {
      if (generation === this.generation) this.update({ drivers: failure(error, routeFormDriversFailedMessage) });
    }
  }

  /** listCities, in the server's order. */
  public async loadCities(): Promise<void> {
    const api = this.api;
    if (api === null) return;
    const generation = this.generation;
    this.cancelAreas();
    this.update({ cities: { kind: "loading" }, cityId: null, areas: { kind: "idle" }, serviceAreaId: null });
    try {
      const cities = await api.listCities(this.abort.signal);
      if (generation === this.generation) this.update({ cities: { kind: "ready", items: cities } });
    } catch (error: unknown) {
      if (generation === this.generation) this.update({ cities: failure(error, routeFormCitiesFailedMessage) });
    }
  }

  /** Only a listed ACTIVE driver; anything else clears the choice. */
  public chooseDriver(driverId: string): void {
    const drivers = this.getSnapshot().drivers;
    const listed = drivers.kind === "ready" && drivers.items.some((driver) => driver.driver_id === driverId);
    this.update({ driverId: listed ? driverId : null });
  }

  /** Only a listed city; the area goes back to "Toda la ciudad" and the city's areas are read. */
  public async chooseCity(cityId: string): Promise<void> {
    const cities = this.getSnapshot().cities;
    const listed = cities.kind === "ready" && cities.items.some((city) => city.id === cityId);
    this.cancelAreas();
    this.update({ cityId: listed ? cityId : null, serviceAreaId: null, areas: { kind: "idle" } });
    if (listed) await this.loadAreas();
  }

  /** Only a listed ACTIVE area of the chosen city; "" (or anything else) is "Toda la ciudad". */
  public chooseArea(areaId: string): void {
    const areas = this.getSnapshot().areas;
    const listed = areas.kind === "ready" && areas.items.some((area) => area.id === areaId);
    this.update({ serviceAreaId: listed ? areaId : null });
  }

  /** The ACTIVE service areas of the chosen city; a newer city choice wins over an older answer. */
  public async loadAreas(): Promise<void> {
    const api = this.api;
    const cityId = this.getSnapshot().cityId;
    if (api === null || cityId === null) return;
    this.cancelAreas();
    const generation = this.generation;
    const areasGeneration = ++this.areasGeneration;
    const controller = new AbortController();
    this.areasAbort = controller;
    const current = () =>
      generation === this.generation && areasGeneration === this.areasGeneration && this.getSnapshot().cityId === cityId;
    this.update({ areas: { kind: "loading" }, serviceAreaId: null });
    try {
      const areas = await api.listServiceAreas(cityId, AbortSignal.any([this.abort.signal, controller.signal]));
      if (current()) this.update({ areas: { kind: "ready", items: activeServiceAreas(areas) } });
    } catch (error: unknown) {
      if (current()) this.update({ areas: failure(error, routeFormAreasFailedMessage) });
    }
  }

  /** What createRoute receives, or null until a driver and a city are chosen. */
  public choice(): CreateRouteChoice | null {
    const state = this.getSnapshot();
    if (state.driverId === null || state.cityId === null) return null;
    return { driverId: state.driverId, cityId: state.cityId, serviceAreaId: state.serviceAreaId };
  }

  private cancelAreas(): void {
    this.areasGeneration += 1;
    this.areasAbort?.abort();
    this.areasAbort = null;
  }

  private reset(): void {
    this.generation += 1;
    this.abort.abort();
    this.abort = new AbortController();
    this.cancelAreas();
  }
}

export const routeDriverUnavailableText = "Repartidor no disponible";

/**
 * UI-PHASE3-ROUTES-NO-IDS-2026-10-12: the driver of a route as the detail names it - "Repartidor DRV-0a1b2c3d" from
 * listOrganizationDrivers, any status - or "Repartidor no disponible" when that read failed or did not return the
 * driver (for example an inactive one); never its id.
 */
export function routeDriverText(state: Pick<CreateRouteFormState, "drivers" | "driverReferences">, driverId: string): string {
  if (state.drivers.kind === "loading") return "Cargando repartidor…";
  const reference = state.drivers.kind === "ready" ? state.driverReferences.get(driverId) : undefined;
  return reference === undefined ? routeDriverUnavailableText : `Repartidor ${reference}`;
}

/** Session, role and MFA (with the step-up back to Rutas) in their own words; anything else, the list's message. */
function failure(error: unknown, readFailed: string): { kind: "failed"; message: string; stepUpHref: string | null } {
  if (error instanceof TenantApiError && (error.category === "unauthorized" || error.category === "forbidden"))
    return { kind: "failed", ...describeFailure(error, routeFormPath) };
  return { kind: "failed", message: readFailed, stepUpHref: null };
}
