import { afterEach, describe, expect, it, vi } from "vitest";
import { nextStepActions } from "../contracts/order-transitions";
import {
  bearerSession,
  jsonResponse,
  orderId,
  pendingOrderResponse,
  problem,
  syntheticKey,
} from "../contracts/ui-001-screens.fixtures";
import { createOrderActionsApi } from "./order-actions-api";

const publicId = "ORD_abcdefghij-_0123456789";
const key = syntheticKey(9);
const [cancel] = nextStepActions([{ target_status: "CANCELLED", required_metadata: [] }]);
const [confirm] = nextStepActions([
  { target_status: "CONFIRMED", required_metadata: ["restricted_goods_acknowledged"] },
]);

function lastCall(fetchMock: ReturnType<typeof vi.fn>): [URL, RequestInit] {
  return fetchMock.mock.calls.at(-1) as [URL, RequestInit];
}

function api() {
  return createOrderActionsApi("https://api.synthetic.test", bearerSession());
}

afterEach(() => vi.unstubAllGlobals());

describe("order actions api (UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05)", () => {
  it("searches listOrders by the exact tracking number only", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse(200, { items: [pendingOrderResponse(orderId, publicId)], next_cursor: null }));
    vi.stubGlobal("fetch", fetchMock);
    await expect(api().findOrderIdByPublicId(`  ${publicId} `)).resolves.toBe(orderId);
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe("/api/v1/orders");
    expect([...url.searchParams.keys()]).toEqual(["public_id"]);
    expect(url.searchParams.get("public_id")).toBe(publicId);
    expect(init.method).toBe("GET");
    expect((init.headers as Record<string, string>)["X-Organization-Id"]).toBe(bearerSession().organizationId);
  });

  it("answers an empty page with null and never sends a value that is not a tracking number", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(200, { items: [], next_cursor: null }));
    vi.stubGlobal("fetch", fetchMock);
    await expect(api().findOrderIdByPublicId(publicId)).resolves.toBeNull();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await expect(api().findOrderIdByPublicId("Juan Pérez")).resolves.toBeNull();
    await expect(api().findOrderIdByPublicId("ord_abcdefghij-_0123456789")).resolves.toBeNull();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it.each([
    [{ items: [pendingOrderResponse(orderId, "ORD_ZZZZZZZZZZZZZZZZZZZZZZ")], next_cursor: null }],
    [{ items: [pendingOrderResponse(orderId, publicId), pendingOrderResponse(orderId, publicId)], next_cursor: null }],
    [{ items: [{ ...pendingOrderResponse(orderId, publicId), recipient_phone: "6671234567" }], next_cursor: null }],
    [{ items: [], next_cursor: "abc" }],
  ])("fails closed on a search response outside the contract", async (body) => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(200, body)));
    await expect(api().findOrderIdByPublicId(publicId)).rejects.toMatchObject({ category: "invalid" });
  });

  it("sends transitionOrder with the reason, the read version and the Idempotency-Key", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse(200, { ...pendingOrderResponse(orderId, publicId), status: "CANCELLED", version: 5 }));
    vi.stubGlobal("fetch", fetchMock);
    const result = await api().transitionOrder(orderId, cancel, " Cliente canceló ", 4, false, key);
    expect(result).toEqual({ id: orderId, status: "CANCELLED", version: 5 });
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe(`/api/v1/orders/${orderId}/transitions`);
    expect(init.method).toBe("POST");
    expect((init.headers as Record<string, string>)["Idempotency-Key"]).toBe(key);
    expect(JSON.parse(init.body as string)).toEqual({
      target_status: "CANCELLED",
      reason: "Cliente canceló",
      expected_version: 4,
    });
  });

  it("sends the restricted-goods acknowledgement only when the transition requires it", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse(200, { ...pendingOrderResponse(orderId, publicId), status: "CONFIRMED", version: 2 }));
    vi.stubGlobal("fetch", fetchMock);
    await api().transitionOrder(orderId, confirm, "Revisado", 1, true, key);
    expect(JSON.parse(lastCall(fetchMock)[1].body as string).metadata).toEqual({ restricted_goods_acknowledged: true });
    await expect(api().transitionOrder(orderId, confirm, "Revisado", 1, false, key)).rejects.toMatchObject({
      category: "invalid",
    });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("classifies a 409 as a conflict and rejects a response for another order or status", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(409)));
    await expect(api().transitionOrder(orderId, cancel, "Motivo", 1, false, key)).rejects.toMatchObject({
      category: "conflict",
    });
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(jsonResponse(200, { ...pendingOrderResponse(orderId, publicId), status: "CONFIRMED" })),
    );
    await expect(api().transitionOrder(orderId, cancel, "Motivo", 1, false, key)).rejects.toMatchObject({
      category: "invalid",
    });
  });

  it("keeps the 409 rule code only when it is a well-formed problem code (ORD-002-GUARD-CODES-2026-10-05)", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(409, "PICKUP_PROOF_REQUIRED")));
    await expect(api().transitionOrder(orderId, cancel, "Motivo", 1, false, key)).rejects.toMatchObject({
      category: "conflict",
      code: "PICKUP_PROOF_REQUIRED",
    });
    for (const code of ["pickup proof", "<b>X</b>", "A".repeat(65)]) {
      vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(409, code)));
      await expect(api().transitionOrder(orderId, cancel, "Motivo", 1, false, key)).rejects.toMatchObject({
        category: "conflict",
        code: null,
      });
    }
  });
});
