import { afterEach, describe, expect, it, vi } from "vitest";
import {
  bearerSession,
  jsonResponse,
  orderId,
  problem,
  syntheticKey,
  syntheticUuid,
} from "../contracts/ui-001-screens.fixtures";
import { createAssignmentApi } from "./assignment-api";
import { TenantApiError } from "./tenant-request";

const driverId = syntheticUuid(0x601);
const key = syntheticKey(7);

function driver(): Record<string, unknown> {
  return {
    driver_id: driverId,
    driver_reference: "DRV-0a1b2c3d",
    vehicle_type: "MOTORCYCLE",
    eligible: true,
    ineligibility_reasons: [],
    active_assignment_count: 1,
  };
}

function assignment(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: syntheticUuid(0x701),
    order_id: orderId,
    driver_id: driverId,
    route_id: null,
    status: "ACCEPTED",
    cost: { currency: "MXN", amount_cents: 4500 },
    ...overrides,
  };
}

function lastCall(fetchMock: ReturnType<typeof vi.fn>): [URL, RequestInit] {
  return fetchMock.mock.calls.at(-1) as [URL, RequestInit];
}

afterEach(() => vi.unstubAllGlobals());

describe("assignment api (UI-PHASE2-DRIVER-PICKER-2026-10-05)", () => {
  it("lists the assignable drivers of the order in the active organization", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(200, { items: [driver()], next_cursor: null }));
    vi.stubGlobal("fetch", fetchMock);
    const result = await createAssignmentApi("https://api.synthetic.test", bearerSession()).listAssignableDrivers(orderId);
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe(`/api/v1/orders/${orderId}/assignable-drivers`);
    expect(url.search).toBe("");
    expect(init.method).toBe("GET");
    expect((init.headers as Record<string, string>)["X-Organization-Id"]).toBe(bearerSession().organizationId);
    expect(result.items[0].driver_reference).toBe("DRV-0a1b2c3d");
  });

  it("sends the cursor and nothing else", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(200, { items: [], next_cursor: null }));
    vi.stubGlobal("fetch", fetchMock);
    await createAssignmentApi("https://api.synthetic.test", bearerSession()).listAssignableDrivers(
      orderId,
      "AdceAAAAAEAAgAAAAAAAAAI",
    );
    expect(lastCall(fetchMock)[0].search).toBe("?cursor=AdceAAAAAEAAgAAAAAAAAAI");
  });

  it("fails closed on a response outside the contract", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(jsonResponse(200, { items: [{ ...driver(), phone: "6671234567" }], next_cursor: null })),
    );
    await expect(
      createAssignmentApi("https://api.synthetic.test", bearerSession()).listAssignableDrivers(orderId),
    ).rejects.toMatchObject({ category: "invalid" });
  });

  it("assigns an OWN driver with integer cents, no route and the Idempotency-Key", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(201, assignment()));
    vi.stubGlobal("fetch", fetchMock);
    const result = await createAssignmentApi("https://api.synthetic.test", bearerSession()).assignDriver(
      orderId,
      driverId,
      4500,
      key,
    );
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe(`/api/v1/orders/${orderId}/assignments`);
    expect(init.method).toBe("POST");
    const headers = init.headers as Record<string, string>;
    expect(headers["Idempotency-Key"]).toBe(key);
    expect(headers["Content-Type"]).toBe("application/json");
    expect(JSON.parse(String(init.body))).toEqual({
      driver_id: driverId,
      assignment_type: "OWN",
      cost_cents: 4500,
      route_id: null,
    });
    expect(result.status).toBe("ACCEPTED");
  });

  it("refuses an answer for another order, driver or cost", async () => {
    for (const overrides of [
      { order_id: syntheticUuid(0x102) },
      { driver_id: syntheticUuid(0x602) },
      { cost: { currency: "MXN", amount_cents: 4501 } },
    ]) {
      vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(201, assignment(overrides))));
      await expect(
        createAssignmentApi("https://api.synthetic.test", bearerSession()).assignDriver(orderId, driverId, 4500, key),
      ).rejects.toMatchObject({ category: "invalid" });
    }
  });

  it("refuses ids and costs outside the contract without calling the API", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const api = createAssignmentApi("https://api.synthetic.test", bearerSession());
    await expect(api.listAssignableDrivers("not-an-id")).rejects.toBeInstanceOf(TenantApiError);
    await expect(api.assignDriver(orderId, "not-an-id", 4500, key)).rejects.toBeInstanceOf(TenantApiError);
    await expect(api.assignDriver(orderId, driverId, 45.5, key)).rejects.toBeInstanceOf(TenantApiError);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("keeps the stable conflict code and the MFA requirement", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(409, "DRIVER_INELIGIBLE")));
    await expect(
      createAssignmentApi("https://api.synthetic.test", bearerSession()).assignDriver(orderId, driverId, 4500, key),
    ).rejects.toMatchObject({ category: "conflict", code: "DRIVER_INELIGIBLE" });
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(403, "MFA_REQUIRED")));
    await expect(
      createAssignmentApi("https://api.synthetic.test", bearerSession()).listAssignableDrivers(orderId),
    ).rejects.toMatchObject({ category: "forbidden", mfaRequired: true });
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(404)));
    await expect(
      createAssignmentApi("https://api.synthetic.test", bearerSession()).listAssignableDrivers(orderId),
    ).rejects.toMatchObject({ category: "not_found" });
  });
});
