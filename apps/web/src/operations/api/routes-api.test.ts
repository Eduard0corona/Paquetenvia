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
import { createRoutesApi, type CreateRouteInput } from "./routes-api";
import { TenantApiError } from "./tenant-request";

const base = "https://api.synthetic.test";
const routeId = syntheticUuid(0x801);
const driverId = syntheticUuid(0x601);
const cityId = syntheticUuid(0x900);
const areaId = syntheticUuid(0x950);
const stopA = syntheticUuid(0xa01);
const stopB = syntheticUuid(0xa02);
const key = syntheticKey(7);
const guide = "ORD_AbCdEfGhIjKlMnOpQrStUv";

function route(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: routeId,
    status: "DRAFT",
    version: 2,
    driver_id: driverId,
    city_id: cityId,
    service_area_id: null,
    scheduled_for: "2026-10-12",
    assignment_cost_cents_total: 4_500,
    stop_count: 0,
    ...overrides,
  };
}

function detail(version: number, stops: readonly [string, string][], overrides: Record<string, unknown> = {}) {
  return {
    ...route({ version, stop_count: stops.length }),
    stops: stops.map(([id, order], index) => ({
      id,
      order_id: order,
      sequence: index + 1,
      stop_type: "DELIVERY",
      status: "PENDING",
    })),
    ...overrides,
  };
}

function lastCall(fetchMock: ReturnType<typeof vi.fn>): [URL, RequestInit] {
  return fetchMock.mock.calls.at(-1) as [URL, RequestInit];
}

/** A fetch answering every call with a fresh response (a body can be read once). */
function answering(make: () => Response) {
  return vi.fn<(input: URL, init?: RequestInit) => Promise<Response>>(() => Promise.resolve(make()));
}

function api() {
  return createRoutesApi(base, bearerSession());
}

afterEach(() => vi.unstubAllGlobals());

describe("routes api (UI-PHASE3-ROUTES-NO-IDS-2026-10-12)", () => {
  it("lists one page at a time with the tenant header, passing only a listed cursor", async () => {
    const fetchMock = answering(() => jsonResponse(200, { items: [route()], next_cursor: "QUJDRA==" }));
    vi.stubGlobal("fetch", fetchMock);
    const page = await api().list(null);
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe("/api/v1/routes");
    expect(url.search).toBe("");
    expect(init.method).toBe("GET");
    expect((init.headers as Record<string, string>)["X-Organization-Id"]).toBe(bearerSession().organizationId);
    expect(page.next_cursor).toBe("QUJDRA==");
    await api().list("QUJDRA==");
    expect([...lastCall(fetchMock)[0].searchParams.entries()]).toEqual([["cursor", "QUJDRA=="]]);
    // A cursor outside the format is never sent, and an answer with one fails closed.
    await expect(api().list("a b")).rejects.toMatchObject({ category: "invalid" });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    vi.stubGlobal("fetch", answering(() => jsonResponse(200, { items: [], next_cursor: "not a cursor" })));
    await expect(api().list(null)).rejects.toMatchObject({ category: "invalid" });
  });

  it("reads a route and refuses the answer for another one", async () => {
    vi.stubGlobal("fetch", answering(() => jsonResponse(200, detail(2, []))));
    expect((await api().get(routeId)).id).toBe(routeId);
    vi.stubGlobal("fetch", answering(() => jsonResponse(200, detail(2, [], { id: syntheticUuid(0x802) }))));
    await expect(api().get(routeId)).rejects.toMatchObject({ category: "invalid" });
  });

  it("creates the route asked for, with its key, and refuses any other answer", async () => {
    const input: CreateRouteInput = { driverId, cityId, serviceAreaId: areaId, scheduledFor: "2026-10-12" };
    const created = route({ version: 1, service_area_id: areaId, assignment_cost_cents_total: 0 });
    const fetchMock = answering(() => jsonResponse(201, created));
    vi.stubGlobal("fetch", fetchMock);
    expect((await api().create(input, key)).id).toBe(routeId);
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe("/api/v1/routes");
    expect(init.method).toBe("POST");
    expect((init.headers as Record<string, string>)["Idempotency-Key"]).toBe(key);
    expect(JSON.parse(String(init.body))).toEqual({
      driver_id: driverId,
      city_id: cityId,
      service_area_id: areaId,
      scheduled_for: "2026-10-12",
    });
    for (const answer of [
      { ...created, version: 2 },
      { ...created, status: "PLANNED" },
      { ...created, driver_id: syntheticUuid(0x602) },
      { ...created, city_id: syntheticUuid(0x901) },
      { ...created, service_area_id: null },
      { ...created, scheduled_for: null },
      { ...created, stop_count: 1 },
      { ...created, assignment_cost_cents_total: 100 },
    ]) {
      vi.stubGlobal("fetch", answering(() => jsonResponse(201, answer)));
      await expect(api().create(input, key)).rejects.toMatchObject({ category: "invalid" });
    }
    vi.stubGlobal("fetch", answering(() => jsonResponse(200, created)));
    await expect(api().create(input, key)).rejects.toMatchObject({ category: "invalid" });
  });

  it("refuses ids, versions and dates outside the contract without calling the API", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const input: CreateRouteInput = { driverId, cityId, serviceAreaId: null, scheduledFor: null };
    await expect(api().create({ ...input, driverId: "not-an-id" }, key)).rejects.toBeInstanceOf(TenantApiError);
    await expect(api().create({ ...input, serviceAreaId: "not-an-id" }, key)).rejects.toBeInstanceOf(TenantApiError);
    await expect(api().create({ ...input, scheduledFor: "2026-02-30" }, key)).rejects.toBeInstanceOf(TenantApiError);
    await expect(api().get("not-an-id")).rejects.toBeInstanceOf(TenantApiError);
    await expect(api().addStop(routeId, "not-an-id", 2, key)).rejects.toBeInstanceOf(TenantApiError);
    await expect(api().addStop(routeId, orderId, 0, key)).rejects.toBeInstanceOf(TenantApiError);
    await expect(api().removeStop(routeId, "not-an-id", 2, key)).rejects.toBeInstanceOf(TenantApiError);
    await expect(api().reorder(routeId, [stopA, stopA], 2, key)).rejects.toBeInstanceOf(TenantApiError);
    await expect(api().reorder(routeId, [stopA], 1.5, key)).rejects.toBeInstanceOf(TenantApiError);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("adds a stop at the expected version and accepts only the route one version later holding the order", async () => {
    const fetchMock = answering(() => jsonResponse(201, detail(3, [[stopA, orderId]])));
    vi.stubGlobal("fetch", fetchMock);
    expect((await api().addStop(routeId, orderId, 2, key)).version).toBe(3);
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe(`/api/v1/routes/${routeId}/stops`);
    expect(init.method).toBe("POST");
    expect((init.headers as Record<string, string>)["Idempotency-Key"]).toBe(key);
    expect(JSON.parse(String(init.body))).toEqual({ order_id: orderId, expected_version: 2 });
    for (const answer of [detail(4, [[stopA, orderId]]), detail(3, [[stopA, syntheticUuid(0x199)]])]) {
      vi.stubGlobal("fetch", answering(() => jsonResponse(201, answer)));
      await expect(api().addStop(routeId, orderId, 2, key)).rejects.toMatchObject({ category: "invalid" });
    }
  });

  it("removes a stop with DELETE and expected_version, accepting only the route without it", async () => {
    const fetchMock = answering(() => jsonResponse(200, detail(3, [[stopB, orderId]])));
    vi.stubGlobal("fetch", fetchMock);
    await api().removeStop(routeId, stopA, 2, key);
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe(`/api/v1/routes/${routeId}/stops/${stopA}`);
    expect([...url.searchParams.entries()]).toEqual([["expected_version", "2"]]);
    expect(init.method).toBe("DELETE");
    expect(init.body).toBeUndefined();
    expect((init.headers as Record<string, string>)["Idempotency-Key"]).toBe(key);
    vi.stubGlobal("fetch", answering(() => jsonResponse(200, detail(3, [[stopA, orderId]]))));
    await expect(api().removeStop(routeId, stopA, 2, key)).rejects.toMatchObject({ category: "invalid" });
  });

  it("reorders with PUT, every stop and the expected version, accepting only that order one version later", async () => {
    const reordered = detail(3, [[stopB, syntheticUuid(0x198)], [stopA, orderId]]);
    const fetchMock = answering(() => jsonResponse(200, reordered));
    vi.stubGlobal("fetch", fetchMock);
    await api().reorder(routeId, [stopB, stopA], 2, key);
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe(`/api/v1/routes/${routeId}/stops/order`);
    expect(init.method).toBe("PUT");
    expect(init.headers).toMatchObject({ "X-Organization-Id": bearerSession().organizationId, "Idempotency-Key": key });
    expect(JSON.parse(String(init.body))).toEqual({ expected_version: 2, stop_ids: [stopB, stopA] });
    await expect(api().reorder(routeId, [stopA, stopB], 2, key)).rejects.toMatchObject({ category: "invalid" });
  });

  it("keeps the problem code, MFA_REQUIRED and the retryable failures for the screen", async () => {
    vi.stubGlobal("fetch", answering(() => problem(409, "VERSION_CONFLICT")));
    await expect(api().reorder(routeId, [stopA], 2, key)).rejects.toMatchObject({
      category: "conflict",
      code: "VERSION_CONFLICT",
    });
    vi.stubGlobal("fetch", answering(() => problem(409, "DRIVER_INELIGIBLE")));
    await expect(api().addStop(routeId, orderId, 2, key)).rejects.toMatchObject({ category: "conflict", code: "DRIVER_INELIGIBLE" });
    vi.stubGlobal("fetch", answering(() => problem(403, "MFA_REQUIRED")));
    await expect(api().removeStop(routeId, stopA, 2, key)).rejects.toMatchObject({
      category: "forbidden",
      mfaRequired: true,
    });
    vi.stubGlobal("fetch", answering(() => problem(404)));
    await expect(api().get(routeId)).rejects.toMatchObject({ category: "not_found" });
    vi.stubGlobal("fetch", answering(() => problem(503)));
    await expect(api().list(null)).rejects.toMatchObject({ category: "unavailable", retryable: true });
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new TypeError("offline")));
    await expect(api().list(null)).rejects.toMatchObject({ category: "network" });
  });

  it("finds an order by its exact guía with listOrders and never sends a value that cannot be one", async () => {
    const found = { id: orderId, public_id: guide, status: "ASSIGNED", version: 3 };
    const fetchMock = answering(() => jsonResponse(200, { items: [found], next_cursor: null }));
    vi.stubGlobal("fetch", fetchMock);
    expect(await api().findOrderIdByPublicId(guide)).toBe(orderId);
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe("/api/v1/orders");
    expect([...url.searchParams.entries()]).toEqual([["public_id", guide]]);
    expect(init.method).toBe("GET");
    // None is null; more than one or another guía fails closed.
    vi.stubGlobal("fetch", answering(() => jsonResponse(200, { items: [], next_cursor: null })));
    expect(await api().findOrderIdByPublicId(guide)).toBeNull();
    vi.stubGlobal("fetch", answering(() => jsonResponse(200, { items: [found, { ...found, id: syntheticUuid(0x198) }], next_cursor: null })));
    await expect(api().findOrderIdByPublicId(guide)).rejects.toBeInstanceOf(TenantApiError);
    vi.stubGlobal("fetch", answering(() => jsonResponse(200, { items: [{ ...found, public_id: "ORD_ZZZZZZZZZZZZZZZZZZZZZZ" }], next_cursor: null })));
    await expect(api().findOrderIdByPublicId(guide)).rejects.toBeInstanceOf(TenantApiError);
    const notCalled = vi.fn();
    vi.stubGlobal("fetch", notCalled);
    expect(await api().findOrderIdByPublicId("ORD_123")).toBeNull();
    expect(notCalled).not.toHaveBeenCalled();
  });

  it("reads an order's guía with getOrder and refuses another order or a value that is not a guía", async () => {
    const order = { ...pendingOrderResponse(orderId, guide), timeline: [] };
    const fetchMock = answering(() => jsonResponse(200, order));
    vi.stubGlobal("fetch", fetchMock);
    expect(await api().getOrderGuide(orderId)).toBe(guide);
    expect(lastCall(fetchMock)[0].pathname).toBe(`/api/v1/orders/${orderId}`);
    for (const answer of [{ ...order, id: syntheticUuid(0x199) }, { ...order, public_id: "PQ-000123" }, { ...order, public_id: ` ${guide}` }]) {
      vi.stubGlobal("fetch", answering(() => jsonResponse(200, answer)));
      await expect(api().getOrderGuide(orderId)).rejects.toBeDefined();
    }
    vi.stubGlobal("fetch", answering(() => problem(404)));
    await expect(api().getOrderGuide(orderId)).rejects.toBeDefined();
  });
});
