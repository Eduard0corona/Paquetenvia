import { describe, expect, it, vi } from "vitest";
import { TenantApiError } from "../api/tenant-request";
import type { City, ServiceArea } from "../contracts/locations";
import type { OrganizationDriver, OrganizationDriverPage } from "../contracts/organization-driver";
import { syntheticUuid } from "../contracts/ui-001-screens.fixtures";
import type { OperationsSession } from "../session/operations-session";
import {
  CreateRouteFormController,
  maximumDriverPages,
  routeFormAreasFailedMessage,
  routeDriverText,
  routeDriverUnavailableText,
  routeFormCitiesFailedMessage,
  routeFormDriversFailedMessage,
  type CreateRouteFormApi,
} from "./create-route-form-controller";

/** UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11: the selectors of "Crear ruta" in Rutas. */
function driver(counter: number, reference: string, overrides: Partial<OrganizationDriver> = {}): OrganizationDriver {
  return {
    driver_id: syntheticUuid(0x600 + counter),
    driver_reference: reference,
    vehicle_type: "MOTORCYCLE",
    status: "ACTIVE",
    active_stop_count: 0,
    last_position_at: null,
    ...overrides,
  };
}

const active = driver(1, "DRV-cccccccc");
const otherActive = driver(2, "DRV-aaaaaaaa", { vehicle_type: "VAN" });
const suspended = driver(3, "DRV-bbbbbbbb", { status: "SUSPENDED" });
const pending = driver(4, "DRV-dddddddd", { status: "PENDING" });

const culiacan: City = { id: syntheticUuid(0x900), name: "Culiacán", state_code: "SIN", country_code: "MX", timezone: "America/Mazatlan" };
const mazatlan: City = { id: syntheticUuid(0x901), name: "Mazatlán", state_code: "SIN", country_code: "MX", timezone: "America/Mazatlan" };

function area(counter: number, cityId: string, status: ServiceArea["status"] = "ACTIVE"): ServiceArea {
  return { id: syntheticUuid(0x950 + counter), city_id: cityId, name: `Área ${counter}`, status };
}

const centro = area(1, culiacan.id);
const retired = area(2, culiacan.id, "INACTIVE");
const norte = area(3, culiacan.id);
const puerto = area(4, mazatlan.id);

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

/** Answers every list per call, recording the calls and their signals. */
class FakeApi implements CreateRouteFormApi {
  public readonly calls: string[] = [];
  public readonly signals: AbortSignal[] = [];
  public driverPages: Record<string, () => Promise<OrganizationDriverPage>> = {
    "": () => Promise.resolve({ items: [active, suspended, otherActive, pending], next_cursor: null }),
  };
  public citiesAnswer: () => Promise<readonly City[]> = () => Promise.resolve([culiacan, mazatlan]);
  public areaAnswers: Record<string, () => Promise<readonly ServiceArea[]>> = {
    [culiacan.id]: () => Promise.resolve([centro, retired, norte]),
    [mazatlan.id]: () => Promise.resolve([puerto]),
  };

  public listDrivers(cursor: string | null, signal?: AbortSignal): Promise<OrganizationDriverPage> {
    this.calls.push(`drivers${cursor === null ? "" : `@${cursor}`}`);
    if (signal !== undefined) this.signals.push(signal);
    const answer = this.driverPages[cursor ?? ""];
    return answer === undefined ? Promise.reject(new TenantApiError("invalid")) : answer();
  }

  public listCities(signal?: AbortSignal): Promise<readonly City[]> {
    this.calls.push("cities");
    if (signal !== undefined) this.signals.push(signal);
    return this.citiesAnswer();
  }

  public listServiceAreas(cityId: string, signal?: AbortSignal): Promise<readonly ServiceArea[]> {
    this.calls.push(`areas ${cityId}`);
    if (signal !== undefined) this.signals.push(signal);
    const answer = this.areaAnswers[cityId];
    return answer === undefined ? Promise.resolve([]) : answer();
  }
}

const session: OperationsSession = { organizationId: syntheticUuid(0xa1), sessionNamespace: "synthetic", getAccessToken: () => "token" };

function setup(api = new FakeApi(), current: OperationsSession | null = session) {
  const createApi = vi.fn(() => api);
  const controller = new CreateRouteFormController({ readSession: () => current, createApi });
  return { controller, api, createApi };
}

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

describe("create route form (UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11)", () => {
  it("starts loading both lists and nothing chosen, so nothing can be sent", () => {
    const { controller } = setup();
    expect(controller.getSnapshot()).toMatchObject({
      drivers: { kind: "loading" },
      cities: { kind: "loading" },
      areas: { kind: "idle" },
      driverId: null,
      cityId: null,
      serviceAreaId: null,
    });
    expect(controller.choice()).toBeNull();
  });

  it("lists only ACTIVE drivers, by reference, and the cities in the server's order", async () => {
    const { controller, api } = setup();
    await controller.start();
    expect(api.calls).toEqual(["drivers", "cities"]);
    const state = controller.getSnapshot();
    expect(state.drivers).toEqual({ kind: "ready", items: [otherActive, active] });
    expect(state.driversTruncated).toBe(false);
    expect(state.cities).toEqual({ kind: "ready", items: [culiacan, mazatlan] });
    // A suspended or pending driver cannot be chosen: createRoute takes an OWN ACTIVE driver.
    controller.chooseDriver(suspended.driver_id);
    expect(controller.getSnapshot().driverId).toBeNull();
    controller.chooseDriver(pending.driver_id);
    expect(controller.getSnapshot().driverId).toBeNull();
    controller.chooseDriver(active.driver_id);
    expect(controller.getSnapshot().driverId).toBe(active.driver_id);
  });

  it("reads every page of drivers, one after another, without repeating a driver", async () => {
    const api = new FakeApi();
    const firstPage = deferred<OrganizationDriverPage>();
    api.driverPages = {
      "": () => firstPage.promise,
      "page-2": () => Promise.resolve({ items: [otherActive, active], next_cursor: null }),
    };
    const { controller } = setup(api);
    const starting = controller.start();
    await flush();
    expect(api.calls.filter((call) => call.startsWith("drivers"))).toEqual(["drivers"]);
    firstPage.resolve({ items: [active], next_cursor: "page-2" });
    await starting;
    expect(api.calls.filter((call) => call.startsWith("drivers"))).toEqual(["drivers", "drivers@page-2"]);
    expect(controller.getSnapshot().drivers).toEqual({ kind: "ready", items: [otherActive, active] });
  });

  it("stops at the page limit and says some drivers are missing; a repeated cursor fails the list", async () => {
    const long = new FakeApi();
    long.driverPages = { "": () => Promise.resolve({ items: [active], next_cursor: "p1" }) };
    for (let page = 1; page <= maximumDriverPages; page += 1)
      long.driverPages[`p${page}`] = () => Promise.resolve({ items: [], next_cursor: `p${page + 1}` });
    const longRun = setup(long);
    await longRun.controller.start();
    expect(long.calls.filter((call) => call.startsWith("drivers"))).toHaveLength(maximumDriverPages);
    expect(longRun.controller.getSnapshot()).toMatchObject({ drivers: { kind: "ready", items: [active] }, driversTruncated: true });

    const looping = new FakeApi();
    looping.driverPages = {
      "": () => Promise.resolve({ items: [active], next_cursor: "loop" }),
      loop: () => Promise.resolve({ items: [], next_cursor: "loop" }),
    };
    const loopRun = setup(looping);
    await loopRun.controller.start();
    expect(loopRun.controller.getSnapshot().drivers).toEqual({
      kind: "failed",
      message: routeFormDriversFailedMessage,
      stepUpHref: null,
    });
  });

  it("says when a list cannot be read, in its own words, and reads it again on request", async () => {
    const api = new FakeApi();
    let failing = true;
    api.driverPages = {
      "": () => (failing ? Promise.reject(new TenantApiError("network")) : Promise.resolve({ items: [active], next_cursor: null })),
    };
    api.citiesAnswer = () => (failing ? Promise.reject(new TenantApiError("unavailable")) : Promise.resolve([culiacan]));
    const { controller } = setup(api);
    await controller.start();
    expect(controller.getSnapshot().drivers).toEqual({ kind: "failed", message: routeFormDriversFailedMessage, stepUpHref: null });
    expect(controller.getSnapshot().cities).toEqual({ kind: "failed", message: routeFormCitiesFailedMessage, stepUpHref: null });
    failing = false;
    await controller.loadDrivers();
    await controller.loadCities();
    expect(controller.getSnapshot().drivers).toEqual({ kind: "ready", items: [active] });
    expect(controller.getSnapshot().cities).toEqual({ kind: "ready", items: [culiacan] });
  });

  it("names session, role and MFA refusals, with the step-up back to Rutas", async () => {
    const cases: readonly [TenantApiError, string, string | null][] = [
      [new TenantApiError("unauthorized"), "Tu sesión no es válida; inicia sesión de nuevo.", null],
      [new TenantApiError("forbidden"), "Tu rol no permite esta acción en la organización activa.", null],
      [
        new TenantApiError("forbidden", "MFA_REQUIRED", true),
        "Esta acción requiere verificar tu identidad (MFA).",
        "/login?mfa=required&return_url=%2Fops%2Froutes",
      ],
    ];
    for (const [error, message, stepUpHref] of cases) {
      const api = new FakeApi();
      api.driverPages = { "": () => Promise.reject(error) };
      api.citiesAnswer = () => Promise.reject(error);
      const { controller } = setup(api);
      await controller.start();
      expect(controller.getSnapshot().drivers).toEqual({ kind: "failed", message, stepUpHref });
      expect(controller.getSnapshot().cities).toEqual({ kind: "failed", message, stepUpHref });
    }
  });

  it("reads the ACTIVE areas of the chosen city, with Toda la ciudad (no area) chosen", async () => {
    const { controller, api } = setup();
    await controller.start();
    await controller.chooseCity(culiacan.id);
    expect(api.calls).toContain(`areas ${culiacan.id}`);
    expect(controller.getSnapshot()).toMatchObject({
      cityId: culiacan.id,
      areas: { kind: "ready", items: [centro, norte] },
      serviceAreaId: null,
    });
    // An INACTIVE area, an area of another city or an unknown value is "Toda la ciudad".
    for (const value of [retired.id, puerto.id, "not-an-id"]) {
      controller.chooseArea(value);
      expect(controller.getSnapshot().serviceAreaId).toBeNull();
    }
    controller.chooseArea(norte.id);
    expect(controller.getSnapshot().serviceAreaId).toBe(norte.id);
    controller.chooseArea("");
    expect(controller.getSnapshot().serviceAreaId).toBeNull();
  });

  it("drops the area when the city changes and ignores the late areas of an earlier city", async () => {
    const api = new FakeApi();
    const slow = deferred<readonly ServiceArea[]>();
    api.areaAnswers = { [culiacan.id]: () => slow.promise, [mazatlan.id]: () => Promise.resolve([puerto]) };
    const { controller } = setup(api);
    await controller.start();
    const first = controller.chooseCity(culiacan.id);
    expect(controller.getSnapshot().areas).toEqual({ kind: "loading" });
    await controller.chooseCity(mazatlan.id);
    expect(api.signals.at(-2)?.aborted).toBe(true);
    slow.resolve([centro]);
    await first;
    expect(controller.getSnapshot()).toMatchObject({ cityId: mazatlan.id, areas: { kind: "ready", items: [puerto] } });
    controller.chooseArea(puerto.id);
    await controller.chooseCity(culiacan.id);
    expect(controller.getSnapshot().serviceAreaId).toBeNull();
    // A city that is not listed is no choice at all.
    await controller.chooseCity(syntheticUuid(0x9ff));
    expect(controller.getSnapshot()).toMatchObject({ cityId: null, areas: { kind: "idle" } });
  });

  it("says when the areas cannot be read, keeps Toda la ciudad and reads them again on request", async () => {
    const api = new FakeApi();
    let failing = true;
    api.areaAnswers = {
      [culiacan.id]: () => (failing ? Promise.reject(new TenantApiError("network")) : Promise.resolve([centro])),
    };
    const { controller } = setup(api);
    await controller.start();
    await controller.chooseCity(culiacan.id);
    expect(controller.getSnapshot().areas).toEqual({ kind: "failed", message: routeFormAreasFailedMessage, stepUpHref: null });
    expect(controller.getSnapshot().serviceAreaId).toBeNull();
    failing = false;
    await controller.loadAreas();
    expect(controller.getSnapshot().areas).toEqual({ kind: "ready", items: [centro] });
  });

  it("is sent only with a driver and a city, with the area or none", async () => {
    const { controller } = setup();
    await controller.start();
    expect(controller.choice()).toBeNull();
    controller.chooseDriver(active.driver_id);
    expect(controller.choice()).toBeNull();
    await controller.chooseCity(culiacan.id);
    expect(controller.choice()).toEqual({ driverId: active.driver_id, cityId: culiacan.id, serviceAreaId: null });
    controller.chooseArea(centro.id);
    expect(controller.choice()).toEqual({ driverId: active.driver_id, cityId: culiacan.id, serviceAreaId: centro.id });
  });

  it("starts from empty lists for a new session and cancels the reads in flight when it stops", async () => {
    const api = new FakeApi();
    const { controller, createApi } = setup(api);
    await controller.start();
    controller.chooseDriver(active.driver_id);
    await controller.chooseCity(culiacan.id);
    await controller.start();
    expect(createApi).toHaveBeenCalledTimes(2);
    expect(controller.getSnapshot()).toMatchObject({ driverId: null, cityId: null, serviceAreaId: null, areas: { kind: "idle" } });

    const slow = deferred<OrganizationDriverPage>();
    api.driverPages = { "": () => slow.promise };
    const starting = controller.start();
    controller.stop();
    expect(api.signals.some((signal) => signal.aborted)).toBe(true);
    slow.resolve({ items: [active], next_cursor: null });
    await starting;
    expect(controller.getSnapshot().drivers).toEqual({ kind: "loading" });
  });

  it("without a session, reads nothing and says so", async () => {
    const { controller, api, createApi } = setup(new FakeApi(), null);
    await controller.start();
    expect(createApi).not.toHaveBeenCalled();
    expect(api.calls).toEqual([]);
    expect(controller.getSnapshot().drivers).toEqual({
      kind: "failed",
      message: "Tu sesión no es válida; inicia sesión de nuevo.",
      stepUpHref: null,
    });
    expect(controller.choice()).toBeNull();
  });
});

describe("the driver of the route detail (UI-PHASE3-ROUTES-NO-IDS-2026-10-12)", () => {
  it("names a driver of any status by its DRV- reference from the same read", async () => {
    const { controller } = setup();
    expect(routeDriverText(controller.getSnapshot(), active.driver_id)).toBe("Cargando repartidor…");
    await controller.start();
    const state = controller.getSnapshot();
    expect(state.driverReferences).toEqual(
      new Map([active, suspended, otherActive, pending].map((item) => [item.driver_id, item.driver_reference])),
    );
    expect(routeDriverText(state, active.driver_id)).toBe("Repartidor DRV-cccccccc");
    // A SUSPENDED or PENDING driver cannot be chosen for a new route but still names its routes.
    expect(routeDriverText(state, suspended.driver_id)).toBe("Repartidor DRV-bbbbbbbb");
    expect(routeDriverText(state, pending.driver_id)).toBe("Repartidor DRV-dddddddd");
    expect(state.drivers).toEqual({ kind: "ready", items: [otherActive, active] });
  });

  it("says Repartidor no disponible, never the id, for a driver the read did not return or a failed read", async () => {
    const { controller, api } = setup();
    await controller.start();
    const missing = syntheticUuid(0x6ff);
    expect(routeDriverText(controller.getSnapshot(), missing)).toBe(routeDriverUnavailableText);
    api.driverPages = { "": () => Promise.reject(new TenantApiError("unavailable")) };
    await controller.loadDrivers();
    expect(controller.getSnapshot().driverReferences.size).toBe(0);
    expect(routeDriverText(controller.getSnapshot(), active.driver_id)).toBe("Repartidor no disponible");
    for (const text of [routeDriverText(controller.getSnapshot(), active.driver_id), routeDriverText(controller.getSnapshot(), missing)])
      expect(text).not.toMatch(/[0-9a-f]{8}-/);
    controller.stop();
    expect(controller.getSnapshot().driverReferences.size).toBe(0);
  });
});
