import { describe, expect, it, vi } from "vitest";
import type { ExternalOffersApi } from "../api/external-offers-api";
import type { ExternalOffer } from "../contracts/external-offer";
import { ExternalOffersController } from "./external-offers-controller";

const offer: ExternalOffer = {
  id: "22222222-2222-2222-2222-222222222222",
  order_id: "33333333-3333-3333-3333-333333333333",
  status: "OPEN",
  commission: { currency: "MXN", amount_cents: 5000 },
  expires_at: "2026-08-28T02:00:00Z",
  accepted_by_driver_id: null,
  accepted_at: null,
  version: 1,
};

describe("ExternalOffersController", () => {
  it("dismisses only in local memory without a server mutation", async () => {
    const api = harness();
    const controller = new ExternalOffersController(api);
    let current = { offers: [] as readonly ExternalOffer[] };
    controller.subscribe((state) => { current = state; });
    await controller.start();
    controller.dismiss(offer.id);
    expect(current.offers).toEqual([]);
    expect(api.accept).not.toHaveBeenCalled();
    expect(api.list).toHaveBeenCalledOnce();
  });

  it("suppresses a double click while acceptance is pending", async () => {
    let release!: () => void;
    const api = harness();
    vi.mocked(api.accept).mockImplementation(() => new Promise<void>((resolve) => { release = resolve; }));
    const controller = new ExternalOffersController(api);
    await controller.start();
    const first = controller.accept(offer.id);
    const second = controller.accept(offer.id);
    expect(api.accept).toHaveBeenCalledOnce();
    release();
    await Promise.all([first, second]);
  });
});

function harness(): ExternalOffersApi {
  return {
    list: vi.fn(async () => ({ items: [offer], next_cursor: null })),
    accept: vi.fn(async () => undefined),
  };
}
