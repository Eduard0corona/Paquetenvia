import { describe, expect, it, vi } from "vitest";
import type { OrdersApi } from "../api/orders-api";
import {
  acceptanceVersionsUnavailableMessage,
  type AcceptanceVersions,
} from "../contracts/acceptance-versions";
import { TenantApiError } from "../api/tenant-request";
import { parseCreatedOrder, parseQuote } from "../contracts/create-order";
import { draft, orderResponse, quoteResponse } from "../contracts/create-order.fixtures";
import type { OperationsSession } from "../session/operations-session";
import { CreateOrderController } from "./create-order-controller";
import { PendingSubmissions } from "./pending-submissions";

const orgA = "11111111-1111-4111-8111-111111111111";
const orgB = "22222222-2222-4222-8222-222222222222";
const acceptance = { payerType: "SENDER", accepted: true };
const configuredVersions: AcceptanceVersions = { termsVersion: "terms-1", privacyVersion: "privacy-1" };

function session(organizationId: string): OperationsSession {
  return { organizationId, sessionNamespace: "synthetic", getAccessToken: () => "token" };
}

function setup(
  roles: Record<string, string>,
  api: Partial<OrdersApi> = {},
  acceptanceVersions: AcceptanceVersions | null = configuredVersions,
) {
  let current: OperationsSession | null = session(orgA);
  const orders: OrdersApi = {
    createQuote: vi.fn(async () => parseQuote(quoteResponse())),
    createOrder: vi.fn(async () => parseCreatedOrder(orderResponse())),
    ...api,
  };
  let keys = 0;
  const pending = new PendingSubmissions(() => `key-${String(++keys).padStart(12, "0")}`);
  const controller = new CreateOrderController(
    {
      readSession: () => current,
      createApi: () => orders,
      loadRole: async (active) => roles[active.organizationId] ?? null,
      acceptanceVersions,
      now: () => new Date("2026-09-28T17:00:00Z"),
    },
    pending,
  );
  return {
    controller,
    orders,
    pending,
    switchTo(organizationId: string | null) {
      current = organizationId === null ? null : session(organizationId);
    },
  };
}

describe("create order capability gating", () => {
  it.each(["DISPATCHER", "PLATFORM_ADMIN"])("offers quote and order to %s", async (role) => {
    const { controller } = setup({ [orgA]: role });
    await controller.start();
    expect(controller.getSnapshot()).toMatchObject({ phase: "ready", canQuote: true, canOrder: true });
  });

  it.each(["VIEWER", "FINANCE", "DRIVER", "CUSTOMER_SUPPORT"])("hides the flow from %s", async (role) => {
    const { controller, orders } = setup({ [orgA]: role });
    await controller.start();
    expect(controller.getSnapshot()).toMatchObject({ phase: "access_unavailable", canQuote: false, canOrder: false });
    await controller.requestQuote(draft());
    expect(orders.createQuote).not.toHaveBeenCalled();
  });

  it("treats an unresolvable role as no access", async () => {
    const { controller } = setup({});
    await controller.start();
    expect(controller.getSnapshot().phase).toBe("access_unavailable");
  });

  it("stays without session when none is installed", async () => {
    const { controller, switchTo } = setup({});
    switchTo(null);
    await controller.start();
    expect(controller.getSnapshot().phase).toBe("no_session");
  });
});

describe("quote to order", () => {
  it("creates the quote, then the order with the observed acceptance", async () => {
    const { controller, orders } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    await controller.requestQuote(draft());
    expect(controller.getSnapshot().quote?.total.amount_cents).toBe(9_280);
    await controller.confirmOrder(acceptance);
    expect(orders.createOrder).toHaveBeenCalledWith(
      {
        quote_id: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
        payer_type: "SENDER",
        acceptance: {
          terms_version: "terms-1",
          privacy_version: "privacy-1",
          accepted_at: "2026-09-28T17:00:00.000Z",
          acceptance_channel: "ASSISTED",
        },
      },
      "key-000000000002",
      expect.any(AbortSignal),
    );
    expect(controller.getSnapshot()).toMatchObject({ quote: null, order: { public_id: "PQ-000123" } });
  });

  it("sends the declared COD in cents and shows it with the created order (D6-COD-EXPECTED)", async () => {
    const { controller, orders } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    await controller.requestQuote(draft());
    await controller.confirmOrder({ ...acceptance, codAmount: "150.50" });
    const body = vi.mocked(orders.createOrder).mock.calls[0]![0];
    expect(body.cod_expected_cents).toBe(15_050);
    expect(controller.getSnapshot()).toMatchObject({ order: { public_id: "PQ-000123" }, orderCodExpectedCents: 15_050 });
    controller.reset();
    expect(controller.getSnapshot().orderCodExpectedCents).toBeNull();
  });

  it("uses a new idempotency key when only the COD changes after a retryable failure", async () => {
    const createOrder = vi
      .fn()
      .mockRejectedValueOnce(new TenantApiError("network"))
      .mockResolvedValueOnce(parseCreatedOrder(orderResponse()));
    const { controller } = setup({ [orgA]: "DISPATCHER" }, { createOrder });
    await controller.start();
    await controller.requestQuote(draft());
    await controller.confirmOrder({ ...acceptance, codAmount: "100" });
    await controller.confirmOrder({ ...acceptance, codAmount: "101" });
    expect(createOrder.mock.calls[0][0].cod_expected_cents).toBe(10_000);
    expect(createOrder.mock.calls[1][0].cod_expected_cents).toBe(10_100);
    expect(createOrder.mock.calls[0][1]).not.toBe(createOrder.mock.calls[1][1]);
  });

  it("does not call createOrder with an invalid COD", async () => {
    const { controller, orders } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    await controller.requestQuote(draft());
    await controller.confirmOrder({ ...acceptance, codAmount: "150.505" });
    expect(orders.createOrder).not.toHaveBeenCalled();
    expect(controller.getSnapshot().errors.join(" ")).toContain("cobro contra entrega");
  });

  it("takes versions from server config and the channel as ASSISTED, never from the form", async () => {
    const { controller, orders } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    await controller.requestQuote(draft());
    // A tampered form submission carrying its own versions and channel is ignored.
    const forged = {
      ...acceptance,
      termsVersion: "forged-terms",
      privacyVersion: "forged-privacy",
      acceptanceChannel: "WEB",
      acceptance: { terms_version: "forged", acceptance_channel: "API" },
    };
    await controller.confirmOrder(forged);
    const body = vi.mocked(orders.createOrder).mock.calls[0]![0];
    expect(body.acceptance).toEqual({
      terms_version: "terms-1",
      privacy_version: "privacy-1",
      accepted_at: "2026-09-28T17:00:00.000Z",
      acceptance_channel: "ASSISTED",
    });
  });

  it("blocks confirmation when the configured versions are missing", async () => {
    const { controller, orders } = setup({ [orgA]: "DISPATCHER" }, {}, null);
    await controller.start();
    await controller.requestQuote(draft());
    await controller.confirmOrder(acceptance);
    expect(orders.createOrder).not.toHaveBeenCalled();
    expect(controller.getSnapshot().errors).toContain(acceptanceVersionsUnavailableMessage);
  });

  it("does not call createOrder while the low price guard blocks", async () => {
    const { controller, orders } = setup(
      { [orgA]: "DISPATCHER" },
      {
        createQuote: vi.fn(async () => parseQuote(quoteResponse({
          net: { currency: "MXN", amount_cents: 4_483 },
          tax: { currency: "MXN", amount_cents: 717 },
          total: { currency: "MXN", amount_cents: 5_200 },
        }))),
      },
    );
    await controller.start();
    await controller.requestQuote(draft());
    await controller.confirmOrder(acceptance);
    expect(orders.createOrder).not.toHaveBeenCalled();
    expect(controller.getSnapshot().errors.join(" ")).toContain("52 MXN");
  });

  it("retries a failed submission with the same key and payload", async () => {
    const createQuote = vi
      .fn()
      .mockRejectedValueOnce(new TenantApiError("network"))
      .mockResolvedValueOnce(parseQuote(quoteResponse()));
    const { controller } = setup({ [orgA]: "DISPATCHER" }, { createQuote });
    await controller.start();
    await controller.requestQuote(draft());
    await controller.requestQuote(draft());
    expect(createQuote.mock.calls[0][1]).toBe(createQuote.mock.calls[1][1]);
    expect(createQuote.mock.calls[0][0]).toEqual(createQuote.mock.calls[1][0]);
  });

  it("uses a new key once the server gave a definitive answer", async () => {
    const createQuote = vi
      .fn()
      .mockRejectedValueOnce(new TenantApiError("invalid"))
      .mockResolvedValueOnce(parseQuote(quoteResponse()));
    const { controller } = setup({ [orgA]: "DISPATCHER" }, { createQuote });
    await controller.start();
    await controller.requestQuote(draft());
    expect(controller.getSnapshot().message).toContain("cobertura");
    await controller.requestQuote(draft());
    expect(createQuote.mock.calls[0][1]).not.toBe(createQuote.mock.calls[1][1]);
  });

  it("offers the MFA step-up when the API answers MFA_REQUIRED", async () => {
    const { controller } = setup(
      { [orgA]: "PLATFORM_ADMIN" },
      { createQuote: vi.fn().mockRejectedValue(new TenantApiError("forbidden", "MFA_REQUIRED", true)) },
    );
    await controller.start();
    await controller.requestQuote(draft());
    expect(controller.getSnapshot().stepUpHref).toBe(
      "/login?mfa=required&return_url=%2Fops%2Forders%2Fnew",
    );
  });
});

describe("tenant switch", () => {
  it("clears the quote, the order and pending keys, and ignores late answers", async () => {
    let release: (value: unknown) => void = () => undefined;
    const createQuote = vi.fn(
      () => new Promise((resolve) => { release = resolve; }),
    ) as unknown as OrdersApi["createQuote"];
    const env = setup({ [orgA]: "DISPATCHER", [orgB]: "VIEWER" }, { createQuote });
    await env.controller.start();
    const inFlight = env.controller.requestQuote(draft());
    expect(env.pending.size).toBe(1);
    const formKey = env.controller.getSnapshot().formKey;

    env.switchTo(orgB);
    await env.controller.start();
    release(parseQuote(quoteResponse()));
    await inFlight;

    expect(env.pending.size).toBe(0);
    expect(env.controller.getSnapshot()).toMatchObject({
      phase: "access_unavailable",
      role: "VIEWER",
      quote: null,
      order: null,
      busy: false,
      message: null,
    });
    expect(env.controller.getSnapshot().formKey).toBeGreaterThan(formKey);
  });

  it("drops a quote already shown when the tenant changes", async () => {
    const env = setup({ [orgA]: "DISPATCHER", [orgB]: "DISPATCHER" });
    await env.controller.start();
    await env.controller.requestQuote(draft());
    expect(env.controller.getSnapshot().quote).not.toBeNull();
    env.switchTo(orgB);
    await env.controller.start();
    expect(env.controller.getSnapshot()).toMatchObject({ phase: "ready", quote: null });
  });
});

describe("service window (ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02)", () => {
  it("sends the typed window, uses a new key when only the window changes and shows the server window", async () => {
    const window = { from: "2026-09-28T19:00:00+00:00", to: "2026-09-28T21:00:00+00:00" };
    const createOrder = vi
      .fn()
      .mockRejectedValueOnce(new TenantApiError("network"))
      .mockResolvedValueOnce(parseCreatedOrder(orderResponse({ service_window: window })));
    const { controller } = setup({ [orgA]: "DISPATCHER" }, { createOrder });
    await controller.start();
    await controller.requestQuote(draft());
    await controller.confirmOrder({
      ...acceptance,
      serviceWindowFrom: "2026-09-28T12:00",
      serviceWindowTo: "2026-09-28T13:00",
    });
    await controller.confirmOrder({
      ...acceptance,
      serviceWindowFrom: "2026-09-28T12:00",
      serviceWindowTo: "2026-09-28T14:00",
    });
    expect(createOrder.mock.calls[0][0].service_window).toEqual({
      from: "2026-09-28T19:00:00Z",
      to: "2026-09-28T20:00:00Z",
    });
    expect(createOrder.mock.calls[1][0].service_window).toEqual({
      from: "2026-09-28T19:00:00Z",
      to: "2026-09-28T21:00:00Z",
    });
    expect(createOrder.mock.calls[0][1]).not.toBe(createOrder.mock.calls[1][1]);
    expect(controller.getSnapshot().order?.service_window).toEqual(window);
  });

  it("does not call createOrder with a window that already ended", async () => {
    const { controller, orders } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    await controller.requestQuote(draft());
    await controller.confirmOrder({
      ...acceptance,
      serviceWindowFrom: "2026-09-28T07:00",
      serviceWindowTo: "2026-09-28T09:00",
    });
    expect(orders.createOrder).not.toHaveBeenCalled();
    expect(controller.getSnapshot().errors.join(" ")).toContain("ventana de entrega");
  });
});
