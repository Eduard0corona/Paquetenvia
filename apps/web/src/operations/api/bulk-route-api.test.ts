import { afterEach, describe, expect, it, vi } from "vitest";
import {
  bearerSession,
  jsonResponse,
  orderId,
  pendingOrderResponse,
  problem,
  syntheticKey,
  syntheticUuid,
} from "../contracts/ui-001-screens.fixtures";
import { createBulkRouteApi } from "./bulk-route-api";
import { TenantApiError } from "./tenant-request";

const driverId = syntheticUuid(0x601);
const routeId = syntheticUuid(0x801);
const key = syntheticKey(9);
const base = "https://api.synthetic.test";

function route(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: routeId,
    status: "DRAFT",
    version: 4,
    driver_id: driverId,
    city_id: syntheticUuid(0x900),
    service_area_id: null,
    scheduled_for: "2026-10-12",
    assignment_cost_cents_total: 4_500,
    stop_count: 1,
    ...overrides,
  };
}

function detail(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    ...route({ version: 5, stop_count: 2 }),
    stops: [
      { id: syntheticUuid(0xa01), order_id: syntheticUuid(0x199), sequence: 1, stop_type: "DELIVERY", status: "PENDING" },
      { id: syntheticUuid(0xa02), order_id: orderId, sequence: 2, stop_type: "DELIVERY", status: "PENDING" },
    ],
    ...overrides,
  };
}

function lastCall(fetchMock: ReturnType<typeof vi.fn>): [URL, RequestInit] {
  return fetchMock.mock.calls.at(-1) as [URL, RequestInit];
}

afterEach(() => vi.unstubAllGlobals());

describe("bulk route api (UI-PHASE3-INBOX-BULK-ADD-TO-ROUTE-2026-10-11)", () => {
  it("lists only the DRAFT routes of the driver in the active organization", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(200, { items: [route()], next_cursor: "QUJDRA==" }));
    vi.stubGlobal("fetch", fetchMock);
    const page = await createBulkRouteApi(base, bearerSession()).listDraftRoutes(driverId, null);
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe("/api/v1/routes");
    expect([...url.searchParams.entries()]).toEqual([
      ["status", "DRAFT"],
      ["driver_id", driverId],
    ]);
    expect(init.method).toBe("GET");
    expect((init.headers as Record<string, string>)["X-Organization-Id"]).toBe(bearerSession().organizationId);
    expect(page.items.map((item) => item.id)).toEqual([routeId]);
    expect(page.next_cursor).toBe("QUJDRA==");
  });

  it("sends the cursor of the previous page", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(200, { items: [], next_cursor: null }));
    vi.stubGlobal("fetch", fetchMock);
    await createBulkRouteApi(base, bearerSession()).listDraftRoutes(driverId, "MjAyNi0xMC0xMVQx+/A=");
    expect(lastCall(fetchMock)[0].searchParams.get("cursor")).toBe("MjAyNi0xMC0xMVQx+/A=");
  });

  it("fails closed when the answer holds a route it did not ask for", async () => {
    for (const item of [
      route({ status: "PLANNED" }),
      route({ driver_id: syntheticUuid(0x602) }),
      route({ scheduled_for: "2026-02-30" }),
      { ...route(), name: "Ruta norte" },
    ]) {
      vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(200, { items: [item], next_cursor: null })));
      await expect(createBulkRouteApi(base, bearerSession()).listDraftRoutes(driverId, null)).rejects.toMatchObject({
        category: "invalid",
      });
    }
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(200, { items: [], next_cursor: "not a cursor" })));
    await expect(createBulkRouteApi(base, bearerSession()).listDraftRoutes(driverId, null)).rejects.toMatchObject({
      category: "invalid",
    });
  });

  it("reads one route to check it and refuses an answer for another route", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(200, detail({ status: "PLANNED" })));
    vi.stubGlobal("fetch", fetchMock);
    // Whatever its status: the controller decides whether it is still the DRAFT route it used.
    const result = await createBulkRouteApi(base, bearerSession()).getRoute(routeId);
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe(`/api/v1/routes/${routeId}`);
    expect(url.search).toBe("");
    expect(init.method).toBe("GET");
    expect((init.headers as Record<string, string>)["X-Organization-Id"]).toBe(bearerSession().organizationId);
    expect(result.status).toBe("PLANNED");
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(200, detail({ id: syntheticUuid(0x802) }))));
    await expect(createBulkRouteApi(base, bearerSession()).getRoute(routeId)).rejects.toMatchObject({
      category: "invalid",
    });
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(404)));
    await expect(createBulkRouteApi(base, bearerSession()).getRoute(routeId)).rejects.toMatchObject({
      category: "not_found",
    });
  });

  it("adds one stop with expected_version and the Idempotency-Key and checks the authoritative answer", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(201, detail()));
    vi.stubGlobal("fetch", fetchMock);
    const result = await createBulkRouteApi(base, bearerSession()).addRouteStop(routeId, orderId, 4, key);
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe(`/api/v1/routes/${routeId}/stops`);
    expect(init.method).toBe("POST");
    const headers = init.headers as Record<string, string>;
    expect(headers["Idempotency-Key"]).toBe(key);
    expect(headers["Content-Type"]).toBe("application/json");
    expect(JSON.parse(String(init.body))).toEqual({ order_id: orderId, expected_version: 4 });
    expect(result.version).toBe(5);
    expect(result.stop_count).toBe(2);
  });

  it("refuses an answer for another route, another version or without the order", async () => {
    for (const overrides of [
      { id: syntheticUuid(0x802) },
      { status: "PLANNED" },
      { version: 6 },
      { stops: [{ id: syntheticUuid(0xa01), order_id: syntheticUuid(0x199), sequence: 1, stop_type: "DELIVERY", status: "PENDING" }], stop_count: 1 },
    ]) {
      vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(201, detail(overrides))));
      await expect(
        createBulkRouteApi(base, bearerSession()).addRouteStop(routeId, orderId, 4, key),
      ).rejects.toMatchObject({ category: "invalid" });
    }
  });

  it("keeps the problem code of a conflict and flags the MFA step-up", async () => {
    const api = createBulkRouteApi(base, bearerSession());
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(409, "VERSION_CONFLICT")));
    await expect(api.addRouteStop(routeId, orderId, 4, key)).rejects.toMatchObject({
      category: "conflict",
      code: "VERSION_CONFLICT",
    });
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(409, "DRIVER_INELIGIBLE")));
    await expect(api.addRouteStop(routeId, orderId, 4, key)).rejects.toMatchObject({ code: "DRIVER_INELIGIBLE" });
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(403, "MFA_REQUIRED")));
    await expect(api.addRouteStop(routeId, orderId, 4, key)).rejects.toMatchObject({
      category: "forbidden",
      mfaRequired: true,
    });
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(404)));
    await expect(api.addRouteStop(routeId, orderId, 4, key)).rejects.toMatchObject({ category: "not_found" });
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new TypeError("offline")));
    await expect(api.addRouteStop(routeId, orderId, 4, key)).rejects.toMatchObject({ category: "network" });
  });

  it("refuses ids, versions and cursors outside the contract without calling the API", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const api = createBulkRouteApi(base, bearerSession());
    await expect(api.listDraftRoutes("not-an-id", null)).rejects.toBeInstanceOf(TenantApiError);
    await expect(api.listDraftRoutes(driverId, "a cursor?")).rejects.toBeInstanceOf(TenantApiError);
    await expect(api.getRoute("not-an-id")).rejects.toBeInstanceOf(TenantApiError);
    await expect(api.addRouteStop("not-an-id", orderId, 1, key)).rejects.toBeInstanceOf(TenantApiError);
    await expect(api.addRouteStop(routeId, "not-an-id", 1, key)).rejects.toBeInstanceOf(TenantApiError);
    for (const version of [0, -1, 1.5, Number.MAX_SAFE_INTEGER + 1])
      await expect(api.addRouteStop(routeId, orderId, version, key)).rejects.toBeInstanceOf(TenantApiError);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});

describe("Crear ruta nueva api (UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11)", () => {
  const cityId = syntheticUuid(0x900);
  const body = { driverId, cityId, scheduledFor: "2026-10-11" };

  function created(overrides: Record<string, unknown> = {}): Record<string, unknown> {
    return route({ version: 1, scheduled_for: "2026-10-11", assignment_cost_cents_total: 0, stop_count: 0, ...overrides });
  }

  it("creates the route with the driver, the city, no area and the date, under the Idempotency-Key", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(201, created()));
    vi.stubGlobal("fetch", fetchMock);
    const result = await createBulkRouteApi(base, bearerSession()).createRoute(body, key);
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe("/api/v1/routes");
    expect(url.search).toBe("");
    expect(init.method).toBe("POST");
    const headers = init.headers as Record<string, string>;
    expect(headers["Idempotency-Key"]).toBe(key);
    expect(headers["X-Organization-Id"]).toBe(bearerSession().organizationId);
    expect(headers["Content-Type"]).toBe("application/json");
    expect(JSON.parse(String(init.body))).toEqual({
      driver_id: driverId,
      city_id: cityId,
      service_area_id: null,
      scheduled_for: "2026-10-11",
    });
    expect(result).toEqual(created());
  });

  it("refuses an answer that is not the route asked for, as a new DRAFT route", async () => {
    for (const [status, answer] of [
      [200, created()],
      [201, created({ status: "PLANNED" })],
      [201, created({ version: 2 })],
      [201, created({ driver_id: syntheticUuid(0x602) })],
      [201, created({ city_id: syntheticUuid(0x901) })],
      [201, created({ service_area_id: syntheticUuid(0x950) })],
      [201, created({ scheduled_for: "2026-10-12" })],
      [201, created({ stop_count: 1 })],
      [201, created({ assignment_cost_cents_total: 4_500 })],
      [201, { ...created(), name: "Ruta norte" }],
    ] as const) {
      vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(status, answer)));
      await expect(createBulkRouteApi(base, bearerSession()).createRoute(body, key)).rejects.toMatchObject({
        category: "invalid",
      });
    }
  });

  it("keeps the RoutingConflict code, the MFA flag and the retryable failures", async () => {
    const api = createBulkRouteApi(base, bearerSession());
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(409, "DRIVER_INELIGIBLE")));
    await expect(api.createRoute(body, key)).rejects.toMatchObject({ category: "conflict", code: "DRIVER_INELIGIBLE" });
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(409, "CONFLICT")));
    await expect(api.createRoute(body, key)).rejects.toMatchObject({ category: "conflict", code: "CONFLICT" });
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(403, "MFA_REQUIRED")));
    await expect(api.createRoute(body, key)).rejects.toMatchObject({ category: "forbidden", mfaRequired: true });
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(401)));
    await expect(api.createRoute(body, key)).rejects.toMatchObject({ category: "unauthorized" });
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(404)));
    await expect(api.createRoute(body, key)).rejects.toMatchObject({ category: "not_found" });
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(503)));
    await expect(api.createRoute(body, key)).rejects.toMatchObject({ category: "unavailable", retryable: true });
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new TypeError("offline")));
    await expect(api.createRoute(body, key)).rejects.toMatchObject({ category: "network", retryable: true });
  });

  it("refuses ids and dates outside the contract without calling the API", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const api = createBulkRouteApi(base, bearerSession());
    await expect(api.createRoute({ ...body, driverId: "not-an-id" }, key)).rejects.toBeInstanceOf(TenantApiError);
    await expect(api.createRoute({ ...body, cityId: "not-an-id" }, key)).rejects.toBeInstanceOf(TenantApiError);
    for (const scheduledFor of ["2026-02-30", "11/10/2026", ""])
      await expect(api.createRoute({ ...body, scheduledFor }, key)).rejects.toBeInstanceOf(TenantApiError);
    await expect(api.getOrder("not-an-id")).rejects.toBeDefined();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("reads an order's city with the existing getOrder client and refuses an answer for another order", async () => {
    const detail = { ...pendingOrderResponse(orderId, "ORD_0000000000000000000001"), timeline: [] };
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(200, detail));
    vi.stubGlobal("fetch", fetchMock);
    const order = await createBulkRouteApi(base, bearerSession()).getOrder(orderId);
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe(`/api/v1/orders/${orderId}`);
    expect(init.method).toBe("GET");
    expect((init.headers as Record<string, string>)["X-Organization-Id"]).toBe(bearerSession().organizationId);
    // pendingOrderResponse places every order in syntheticUuid(0x701).
    expect(order.city_id).toBe(syntheticUuid(0x701));
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(200, { ...detail, id: syntheticUuid(0x199) })));
    await expect(createBulkRouteApi(base, bearerSession()).getOrder(orderId)).rejects.toMatchObject({ category: "invalid" });
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(404)));
    await expect(createBulkRouteApi(base, bearerSession()).getOrder(orderId)).rejects.toMatchObject({
      category: "not_found",
    });
  });

  it("names the city with listCities", async () => {
    const city = { id: cityId, name: "Culiacán", state_code: "SIN", country_code: "MX", timezone: "America/Mazatlan" };
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(200, [city]));
    vi.stubGlobal("fetch", fetchMock);
    expect(await createBulkRouteApi(base, bearerSession()).listCities()).toEqual([city]);
    expect(lastCall(fetchMock)[0].pathname).toBe("/api/v1/cities");
  });
});
