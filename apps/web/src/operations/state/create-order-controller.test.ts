import { describe, expect, it, vi } from "vitest";
import type { OrderActionsApi } from "../api/order-actions-api";
import type { OrdersApi } from "../api/orders-api";
import {
  acceptanceVersionsUnavailableMessage,
  type AcceptanceVersions,
} from "../contracts/acceptance-versions";
import { TenantApiError } from "../api/tenant-request";
import {
  codAmountAboveCapMessage,
  confirmationBlockerLabels,
  lowPriceAuthorizationNotNeededMessage,
  parseCreatedOrder,
  parseQuote,
  restrictedGoodsRequiredMessage,
} from "../contracts/create-order";
import { draft, orderResponse, quoteResponse } from "../contracts/create-order.fixtures";
import { transitionRejectionMessages } from "../contracts/order-transitions";
import {
  quoteRequiredMessage,
  wizardConfirmationReason,
  type OrderWizardDraft,
} from "../contracts/order-wizard";
import { serviceWindowMessages } from "../contracts/service-window";
import type { OperationsSession } from "../session/operations-session";
import {
  CreateOrderController,
  orderNotCreatedMessage,
  priceChangedWhileQuotingMessage,
} from "./create-order-controller";
import { PendingSubmissions } from "./pending-submissions";

const orgA = "11111111-1111-4111-8111-111111111111";
const orgB = "22222222-2222-4222-8222-222222222222";
const orderId = "ffffffff-ffff-4fff-8fff-ffffffffffff";
const configuredVersions: AcceptanceVersions = { termsVersion: "terms-1", privacyVersion: "privacy-1" };
const origin = draft().origin;
const destination = draft().destination;
const firstPackage = draft().packages[0];

function session(organizationId: string): OperationsSession {
  return { organizationId, sessionNamespace: "synthetic", getAccessToken: () => "token" };
}

type TransitionOrder = OrderActionsApi["transitionOrder"];

function setup(
  roles: Record<string, string>,
  api: Partial<OrdersApi> = {},
  options: {
    readonly acceptanceVersions?: AcceptanceVersions | null;
    readonly transitionOrder?: TransitionOrder;
    readonly now?: () => Date;
  } = {},
) {
  let current: OperationsSession | null = session(orgA);
  const calls: string[] = [];
  const orders: OrdersApi = {
    createQuote: vi.fn(async () => parseQuote(quoteResponse())),
    createOrder: vi.fn(async () => {
      calls.push("createOrder");
      return parseCreatedOrder(orderResponse());
    }),
    ...api,
  };
  const transitionOrder = vi.fn<TransitionOrder>(
    options.transitionOrder ??
      (async (id, action) => {
        calls.push("transitionOrder");
        return { id, status: action.target, version: 2 };
      }),
  );
  let keys = 0;
  const pending = new PendingSubmissions(() => `key-${String(++keys).padStart(12, "0")}`);
  const controller = new CreateOrderController(
    {
      readSession: () => current,
      createApi: () => orders,
      createActionsApi: () => ({ transitionOrder }),
      loadRole: async (active) => roles[active.organizationId] ?? null,
      acceptanceVersions: options.acceptanceVersions === undefined ? configuredVersions : options.acceptanceVersions,
      now: options.now ?? (() => new Date("2026-09-28T17:00:00Z")),
    },
    pending,
  );
  return {
    controller,
    orders,
    transitionOrder,
    pending,
    calls,
    switchTo(organizationId: string | null) {
      current = organizationId === null ? null : session(organizationId);
    },
  };
}

/** Fills steps 1 to 3 with valid synthetic data. */
function fillShipment(controller: CreateOrderController, extra: Partial<OrderWizardDraft> = {}) {
  controller.updateAddress("origin", origin);
  controller.updateAddress("destination", destination);
  controller.updatePackage(0, firstPackage);
  controller.updateDraft({ serviceType: "SAME_DAY", ...extra });
}

/** Walks the wizard to step 4 with a calculated price and the acceptance ticked. */
async function reachConfirm(controller: CreateOrderController, extra: Partial<OrderWizardDraft> = {}) {
  fillShipment(controller, extra);
  controller.next();
  controller.next();
  await controller.requestQuote();
  controller.next();
  controller.updateDraft({ payerType: "SENDER", accepted: true, restrictedGoodsAcknowledged: true });
  expect(controller.getSnapshot().step).toBe("confirm");
}

const fields = (controller: CreateOrderController) => controller.getSnapshot().fieldErrors.map((error) => error.field);

describe("create order capability gating", () => {
  it.each(["DISPATCHER", "PLATFORM_ADMIN"])("offers quote, order and confirmation to %s", async (role) => {
    const { controller } = setup({ [orgA]: role });
    await controller.start();
    expect(controller.getSnapshot()).toMatchObject({ phase: "ready", canQuote: true, canOrder: true, step: "where" });
  });

  it("tells PLATFORM_ADMIN, never DISPATCHER, that confirming needs MFA", async () => {
    const admin = setup({ [orgA]: "PLATFORM_ADMIN" });
    await admin.controller.start();
    expect(admin.controller.getSnapshot().confirmationNeedsMfa).toBe(true);
    const dispatcher = setup({ [orgA]: "DISPATCHER" });
    await dispatcher.controller.start();
    expect(dispatcher.controller.getSnapshot().confirmationNeedsMfa).toBe(false);
  });

  it.each(["VIEWER", "FINANCE", "DRIVER", "CUSTOMER_SUPPORT"])("hides the flow from %s", async (role) => {
    const { controller, orders } = setup({ [orgA]: role });
    await controller.start();
    expect(controller.getSnapshot()).toMatchObject({ phase: "access_unavailable", canQuote: false, canOrder: false });
    fillShipment(controller);
    await controller.requestQuote();
    await controller.submit();
    expect(orders.createQuote).not.toHaveBeenCalled();
    expect(orders.createOrder).not.toHaveBeenCalled();
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

describe("wizard steps (UI-PHASE3-ORDER-WIZARD-2026-10-10)", () => {
  it("refuses an empty step 1 with a message per field, without calling the API", async () => {
    const { controller, orders } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    controller.next();
    const state = controller.getSnapshot();
    expect(state.step).toBe("where");
    expect(state.validationAttempt).toBe(1);
    expect(fields(controller)).toEqual([
      "origin.addressText",
      "origin.contactName",
      "origin.phone",
      "origin.coordinates",
      "destination.addressText",
      "destination.contactName",
      "destination.phone",
      "destination.coordinates",
    ]);
    expect(orders.createQuote).not.toHaveBeenCalled();
  });

  it("validates only the current step and moves forward and back", async () => {
    const { controller } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    controller.updateAddress("origin", origin);
    controller.updateAddress("destination", destination);
    controller.next();
    // Step 2 starts with an empty package, but moving into it is not refused.
    expect(controller.getSnapshot()).toMatchObject({ step: "what", fieldErrors: [] });
    controller.next();
    expect(controller.getSnapshot().step).toBe("what");
    expect(fields(controller)).toEqual(["packages.0.description", "packages.0.weightGrams", "packages.0.declaredValue"]);
    controller.back();
    expect(controller.getSnapshot()).toMatchObject({ step: "where", fieldErrors: [] });
    controller.next();
    controller.updatePackage(0, firstPackage);
    controller.next();
    expect(controller.getSnapshot().step).toBe("service");
  });

  it("goes back to a completed step from the stepper but never skips ahead", async () => {
    const { controller } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    fillShipment(controller);
    controller.goTo("service");
    expect(controller.getSnapshot().step).toBe("where");
    controller.next();
    controller.next();
    expect(controller.getSnapshot().step).toBe("service");
    controller.goTo("confirm");
    expect(controller.getSnapshot().step).toBe("service");
    controller.goTo("where");
    expect(controller.getSnapshot().step).toBe("where");
  });

  it("clears a message as soon as its field is fixed and keeps the others", async () => {
    const { controller } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    controller.next();
    controller.updateAddress("origin", { phone: "667 123 456" });
    expect(controller.getSnapshot().fieldErrors.find((error) => error.field === "origin.phone")?.message).toContain(
      "10 dígitos",
    );
    controller.updateAddress("origin", { phone: "667 123 4567" });
    expect(fields(controller)).not.toContain("origin.phone");
    expect(fields(controller)).toContain("origin.addressText");
    // A new problem in a field without a shown message waits for the next attempt.
    controller.updateAddress("origin", { references: "x".repeat(501) });
    expect(fields(controller)).not.toContain("origin.references");
    controller.next();
    expect(fields(controller)).toContain("origin.references");
  });

  it("keeps between 1 and 20 packages", async () => {
    const { controller } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    controller.removePackage(0);
    expect(controller.getSnapshot().draft.packages).toHaveLength(1);
    for (let index = 0; index < 25; index += 1) controller.addPackage();
    expect(controller.getSnapshot().draft.packages).toHaveLength(20);
    controller.updatePackage(3, { description: "Tercera caja" });
    controller.removePackage(2);
    expect(controller.getSnapshot().draft.packages).toHaveLength(19);
    expect(controller.getSnapshot().draft.packages[2].description).toBe("Tercera caja");
  });

  it("asks for the price before leaving step 3", async () => {
    const { controller } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    fillShipment(controller);
    controller.next();
    controller.next();
    controller.next();
    expect(controller.getSnapshot().step).toBe("service");
    expect(controller.getSnapshot().fieldErrors).toEqual([{ field: "quote", message: quoteRequiredMessage }]);
  });

  it("refuses the COD above the 20,000 MXN cap and a window over 12 hours in step 3", async () => {
    const { controller } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    fillShipment(controller, {
      codAmount: "20000.01",
      serviceWindowFrom: "2026-09-28T12:00",
      serviceWindowTo: "2026-09-29T01:00",
    });
    controller.next();
    controller.next();
    await controller.requestQuote();
    controller.next();
    expect(controller.getSnapshot().step).toBe("service");
    expect(controller.getSnapshot().fieldErrors).toEqual([
      { field: "codAmount", message: codAmountAboveCapMessage },
      { field: "serviceWindow", message: serviceWindowMessages.span },
    ]);
    controller.updateDraft({ codAmount: "20000", serviceWindowTo: "2026-09-29T00:00" });
    controller.next();
    expect(controller.getSnapshot().step).toBe("confirm");
  });

  it("sends the person back to the step that holds a wrong field when calculating the price", async () => {
    const { controller, orders } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    fillShipment(controller);
    controller.next();
    controller.next();
    expect(controller.getSnapshot().step).toBe("service");
    // The price request re-checks every field it is calculated from, whatever step shows it.
    controller.updateAddress("destination", { phone: "+1 667 123 4567" });
    await controller.requestQuote();
    expect(controller.getSnapshot()).toMatchObject({ step: "where", validationAttempt: 1 });
    expect(fields(controller)).toEqual(["destination.phone"]);
    expect(orders.createQuote).not.toHaveBeenCalled();
  });

  it("refuses step 4 without payer, acceptance and the prohibited-goods confirmation", async () => {
    const { controller, orders } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    fillShipment(controller);
    controller.next();
    controller.next();
    await controller.requestQuote();
    controller.next();
    await controller.submit();
    expect(controller.getSnapshot().step).toBe("confirm");
    expect(fields(controller)).toEqual(["payerType", "accepted", "restrictedGoodsAcknowledged"]);
    expect(controller.getSnapshot().fieldErrors[2].message).toBe(restrictedGoodsRequiredMessage);
    expect(orders.createOrder).not.toHaveBeenCalled();
  });

  it("blocks the order when the configured versions are missing", async () => {
    const { controller, orders } = setup({ [orgA]: "DISPATCHER" }, {}, { acceptanceVersions: null });
    await controller.start();
    await reachConfirm(controller);
    await controller.submit();
    expect(orders.createOrder).not.toHaveBeenCalled();
    expect(controller.getSnapshot().fieldErrors).toEqual([
      { field: "acceptanceVersions", message: acceptanceVersionsUnavailableMessage },
    ]);
  });
});

describe("price (createQuote) inside the wizard", () => {
  it("drops the price when the shipment changes and keeps it for COD, window or payer changes", async () => {
    const { controller } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    fillShipment(controller);
    controller.next();
    controller.next();
    await controller.requestQuote();
    expect(controller.getSnapshot().quote?.total.amount_cents).toBe(9_280);
    controller.updateDraft({ codAmount: "150", serviceWindowFrom: "", payerType: "SENDER" });
    expect(controller.getSnapshot().quote).not.toBeNull();
    // Spaces the request would trim do not change the price either.
    controller.updateAddress("origin", { addressText: ` ${origin.addressText} ` });
    expect(controller.getSnapshot().quote).not.toBeNull();
    controller.updatePackage(0, { weightGrams: "900" });
    expect(controller.getSnapshot().quote).toBeNull();
  });

  it("drops an expired price at Siguiente and asks to calculate it again", async () => {
    let now = new Date("2026-09-28T17:00:00Z");
    const { controller } = setup({ [orgA]: "DISPATCHER" }, {}, { now: () => now });
    await controller.start();
    fillShipment(controller);
    controller.next();
    controller.next();
    await controller.requestQuote();
    now = new Date("2026-09-28T18:00:00Z");
    controller.next();
    expect(controller.getSnapshot()).toMatchObject({ step: "service", quote: null });
    expect(controller.getSnapshot().fieldErrors).toEqual([
      { field: "quote", message: confirmationBlockerLabels.expired },
    ]);
  });

  it("discards a price that arrives after the shipment changed", async () => {
    let release: (value: unknown) => void = () => undefined;
    const createQuote = vi.fn(
      () => new Promise((resolve) => { release = resolve; }),
    ) as unknown as OrdersApi["createQuote"];
    const { controller } = setup({ [orgA]: "DISPATCHER" }, { createQuote });
    await controller.start();
    fillShipment(controller);
    controller.next();
    controller.next();
    const inFlight = controller.requestQuote();
    controller.updateDraft({ serviceType: "URGENT" });
    release(parseQuote(quoteResponse()));
    await inFlight;
    expect(controller.getSnapshot()).toMatchObject({ quote: null, busy: null, message: priceChangedWhileQuotingMessage });
  });

  it("retries a failed price request with the same key and payload", async () => {
    const createQuote = vi
      .fn()
      .mockRejectedValueOnce(new TenantApiError("network"))
      .mockResolvedValueOnce(parseQuote(quoteResponse()));
    const { controller } = setup({ [orgA]: "DISPATCHER" }, { createQuote });
    await controller.start();
    fillShipment(controller);
    controller.next();
    controller.next();
    await controller.requestQuote();
    await controller.requestQuote();
    expect(createQuote.mock.calls[0][1]).toBe(createQuote.mock.calls[1][1]);
    expect(createQuote.mock.calls[0][0]).toEqual(createQuote.mock.calls[1][0]);
  });

  it("uses a new key once the server gave a definitive answer and names Culiacán coverage", async () => {
    const createQuote = vi
      .fn()
      .mockRejectedValueOnce(new TenantApiError("invalid"))
      .mockResolvedValueOnce(parseQuote(quoteResponse()));
    const { controller } = setup({ [orgA]: "DISPATCHER" }, { createQuote });
    await controller.start();
    fillShipment(controller);
    controller.next();
    controller.next();
    await controller.requestQuote();
    expect(controller.getSnapshot().message).toContain("cobertura en Culiacán");
    await controller.requestQuote();
    expect(createQuote.mock.calls[0][1]).not.toBe(createQuote.mock.calls[1][1]);
  });

  it("offers the MFA step-up when the API answers MFA_REQUIRED", async () => {
    const { controller } = setup(
      { [orgA]: "PLATFORM_ADMIN" },
      { createQuote: vi.fn().mockRejectedValue(new TenantApiError("forbidden", "MFA_REQUIRED", true)) },
    );
    await controller.start();
    fillShipment(controller);
    controller.next();
    controller.next();
    await controller.requestQuote();
    expect(controller.getSnapshot().stepUpHref).toBe("/login?mfa=required&return_url=%2Fops%2Forders%2Fnew");
  });
});

describe("create and confirm (owner, 2026-10-10: \"Confirmada (Recommended)\")", () => {
  it("creates the order, then confirms it with the step 4 acknowledgement and the created version", async () => {
    const { controller, orders, transitionOrder, calls } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    await reachConfirm(controller);
    await controller.submit();
    expect(calls).toEqual(["createOrder", "transitionOrder"]);
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
        restricted_goods_acknowledged: true,
      },
      "key-000000000002",
      expect.any(AbortSignal),
    );
    expect(transitionOrder).toHaveBeenCalledTimes(1);
    const [id, action, reason, expectedVersion, acknowledged, key] = transitionOrder.mock.calls[0];
    expect(id).toBe(orderId);
    expect(action).toMatchObject({ target: "CONFIRMED", needsRestrictedGoodsAcknowledgement: true });
    expect(reason).toBe(wizardConfirmationReason);
    expect(reason).toBe("Confirmada al crear la orden");
    expect(expectedVersion).toBe(1);
    expect(acknowledged).toBe(true);
    expect(key).toBe("key-000000000003");
    expect(controller.getSnapshot()).toMatchObject({
      busy: null,
      quote: null,
      outcome: { kind: "confirmed", status: "CONFIRMED", codExpectedCents: 0, order: { public_id: "PQ-000123" } },
    });
  });

  it("sends the declared COD in cents and shows it with the confirmed order (D6-COD-EXPECTED)", async () => {
    const { controller, orders } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    await reachConfirm(controller, { codAmount: "150.50" });
    await controller.submit();
    expect(vi.mocked(orders.createOrder).mock.calls[0]![0].cod_expected_cents).toBe(15_050);
    expect(controller.getSnapshot().outcome).toMatchObject({ kind: "confirmed", codExpectedCents: 15_050 });
    controller.reset();
    expect(controller.getSnapshot()).toMatchObject({ step: "where", outcome: null, quote: null });
    expect(controller.getSnapshot().draft.codAmount).toBe("");
  });

  it("keeps the order in DRAFT and names the unmet rule when the confirmation is refused", async () => {
    const transitionOrder = vi.fn<TransitionOrder>(async () => {
      throw new TenantApiError("conflict", "RESTRICTED_GOODS_ACK_REQUIRED");
    });
    const { controller, orders } = setup({ [orgA]: "DISPATCHER" }, {}, { transitionOrder });
    await controller.start();
    await reachConfirm(controller);
    await controller.submit();
    expect(orders.createOrder).toHaveBeenCalledTimes(1);
    expect(controller.getSnapshot().outcome).toEqual({
      kind: "not_confirmed",
      order: parseCreatedOrder(orderResponse()),
      codExpectedCents: 0,
      certainty: "draft",
      message: transitionRejectionMessages.RESTRICTED_GOODS_ACK_REQUIRED,
      stepUpHref: null,
      retryable: false,
    });
    // Nothing can be retried or resubmitted from here: the order exists.
    await controller.retryConfirmation();
    await controller.submit();
    expect(transitionOrder).toHaveBeenCalledTimes(1);
    expect(orders.createOrder).toHaveBeenCalledTimes(1);
  });

  it("uses the generic transition message for a 409 without a known code", async () => {
    const transitionOrder = vi.fn<TransitionOrder>().mockRejectedValue(new TenantApiError("conflict", "SOMETHING_NEW"));
    const { controller } = setup({ [orgA]: "DISPATCHER" }, {}, { transitionOrder });
    await controller.start();
    await reachConfirm(controller);
    await controller.submit();
    expect(controller.getSnapshot().outcome).toMatchObject({
      kind: "not_confirmed",
      certainty: "draft",
      message: "No se pudo cambiar el estado; la orden cambió o falta un requisito. Actualiza e intenta de nuevo.",
    });
  });

  it("offers the MFA step-up back to the order detail when confirming needs MFA", async () => {
    const transitionOrder = vi
      .fn<TransitionOrder>()
      .mockRejectedValue(new TenantApiError("forbidden", "MFA_REQUIRED", true));
    const { controller } = setup({ [orgA]: "PLATFORM_ADMIN" }, {}, { transitionOrder });
    await controller.start();
    await reachConfirm(controller);
    await controller.submit();
    expect(controller.getSnapshot().outcome).toMatchObject({
      kind: "not_confirmed",
      certainty: "draft",
      retryable: false,
      stepUpHref: `/login?mfa=required&return_url=${encodeURIComponent(`/ops/orders/${orderId}`)}`,
    });
  });

  it("retries only the confirmation, with the same key, after a network failure", async () => {
    const transitionOrder = vi
      .fn<TransitionOrder>()
      .mockRejectedValueOnce(new TenantApiError("network"))
      .mockImplementationOnce(async (id, action) => ({ id, status: action.target, version: 2 }));
    const { controller, orders } = setup({ [orgA]: "DISPATCHER" }, {}, { transitionOrder });
    await controller.start();
    await reachConfirm(controller);
    await controller.submit();
    expect(controller.getSnapshot().outcome).toMatchObject({ kind: "not_confirmed", certainty: "unknown", retryable: true });
    await controller.retryConfirmation();
    expect(orders.createOrder).toHaveBeenCalledTimes(1);
    expect(transitionOrder).toHaveBeenCalledTimes(2);
    expect(transitionOrder.mock.calls[1]).toEqual(transitionOrder.mock.calls[0]);
    expect(controller.getSnapshot().outcome).toMatchObject({ kind: "confirmed", status: "CONFIRMED" });
  });

  it("never reports a confirmation the server did not return", async () => {
    const transitionOrder = vi.fn<TransitionOrder>(async (id) => ({ id, status: "DRAFT", version: 1 }));
    const { controller } = setup({ [orgA]: "DISPATCHER" }, {}, { transitionOrder });
    await controller.start();
    await reachConfirm(controller);
    await controller.submit();
    expect(controller.getSnapshot().outcome).toMatchObject({ kind: "not_confirmed", certainty: "unknown", retryable: false });
  });

  it("does not create a second order when the creation is retried after a network failure", async () => {
    const createOrder = vi
      .fn()
      .mockRejectedValueOnce(new TenantApiError("unavailable"))
      .mockResolvedValueOnce(parseCreatedOrder(orderResponse()));
    const { controller, transitionOrder } = setup({ [orgA]: "DISPATCHER" }, { createOrder });
    await controller.start();
    await reachConfirm(controller);
    await controller.submit();
    expect(controller.getSnapshot()).toMatchObject({ step: "confirm", outcome: null, busy: null });
    expect(controller.getSnapshot().message).toContain("sin duplicarla");
    expect(transitionOrder).not.toHaveBeenCalled();
    await controller.submit();
    // Same Idempotency-Key and the stored body (accepted_at included): the API replays one order.
    expect(createOrder.mock.calls[1][1]).toBe(createOrder.mock.calls[0][1]);
    expect(createOrder.mock.calls[1][0]).toEqual(createOrder.mock.calls[0][0]);
    expect(transitionOrder).toHaveBeenCalledTimes(1);
    expect(controller.getSnapshot().outcome?.kind).toBe("confirmed");
  });

  it("uses a new key when the COD changes after a failed creation", async () => {
    const createOrder = vi
      .fn()
      .mockRejectedValueOnce(new TenantApiError("network"))
      .mockResolvedValueOnce(parseCreatedOrder(orderResponse()));
    const { controller } = setup({ [orgA]: "DISPATCHER" }, { createOrder });
    await controller.start();
    await reachConfirm(controller, { codAmount: "100" });
    await controller.submit();
    controller.back();
    controller.updateDraft({ codAmount: "101" });
    controller.next();
    await controller.submit();
    expect(createOrder.mock.calls[0][0].cod_expected_cents).toBe(10_000);
    expect(createOrder.mock.calls[1][0].cod_expected_cents).toBe(10_100);
    expect(createOrder.mock.calls[0][1]).not.toBe(createOrder.mock.calls[1][1]);
  });

  it("asks for a new price when the server does not create the order", async () => {
    const { controller, transitionOrder } = setup(
      { [orgA]: "DISPATCHER" },
      { createOrder: vi.fn().mockRejectedValue(new TenantApiError("conflict")) },
    );
    await controller.start();
    await reachConfirm(controller);
    await controller.submit();
    expect(controller.getSnapshot()).toMatchObject({
      step: "service",
      quote: null,
      outcome: null,
      message: orderNotCreatedMessage,
    });
    expect(transitionOrder).not.toHaveBeenCalled();
  });

  it("drops the price and returns to step 3 when it expired before confirming", async () => {
    let now = new Date("2026-09-28T17:00:00Z");
    const { controller, orders } = setup({ [orgA]: "DISPATCHER" }, {}, { now: () => now });
    await controller.start();
    await reachConfirm(controller);
    now = new Date("2026-09-28T18:00:01Z");
    await controller.submit();
    expect(orders.createOrder).not.toHaveBeenCalled();
    expect(controller.getSnapshot()).toMatchObject({ step: "service", quote: null });
    expect(controller.getSnapshot().fieldErrors[0].message).toBe(confirmationBlockerLabels.expired);
  });

  it("takes versions from server config and the channel as ASSISTED, never from the draft", async () => {
    const { controller, orders } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    await reachConfirm(controller);
    // A tampered patch carrying its own versions and channel is ignored.
    controller.updateDraft({ termsVersion: "forged", acceptance_channel: "API" } as unknown as Partial<OrderWizardDraft>);
    await controller.submit();
    expect(vi.mocked(orders.createOrder).mock.calls[0]![0].acceptance).toEqual({
      terms_version: "terms-1",
      privacy_version: "privacy-1",
      accepted_at: "2026-09-28T17:00:00.000Z",
      acceptance_channel: "ASSISTED",
    });
  });

  it("ignores a second submit while the first is in flight", async () => {
    let release: (value: unknown) => void = () => undefined;
    const createOrder = vi.fn(
      () => new Promise((resolve) => { release = resolve; }),
    ) as unknown as OrdersApi["createOrder"];
    const { controller, transitionOrder } = setup({ [orgA]: "DISPATCHER" }, { createOrder });
    await controller.start();
    await reachConfirm(controller);
    const first = controller.submit();
    expect(controller.getSnapshot().busy).toBe("order");
    await controller.submit();
    controller.back();
    expect(controller.getSnapshot().step).toBe("confirm");
    release(parseCreatedOrder(orderResponse()));
    await first;
    expect(createOrder).toHaveBeenCalledTimes(1);
    expect(transitionOrder).toHaveBeenCalledTimes(1);
  });
});

describe("tenant switch", () => {
  it("clears the draft, the quote and pending keys, and ignores late answers", async () => {
    let release: (value: unknown) => void = () => undefined;
    const createQuote = vi.fn(
      () => new Promise((resolve) => { release = resolve; }),
    ) as unknown as OrdersApi["createQuote"];
    const env = setup({ [orgA]: "DISPATCHER", [orgB]: "VIEWER" }, { createQuote });
    await env.controller.start();
    fillShipment(env.controller);
    env.controller.next();
    env.controller.next();
    const inFlight = env.controller.requestQuote();
    expect(env.pending.size).toBe(1);

    env.switchTo(orgB);
    await env.controller.start();
    release(parseQuote(quoteResponse()));
    await inFlight;

    expect(env.pending.size).toBe(0);
    expect(env.controller.getSnapshot()).toMatchObject({
      phase: "access_unavailable",
      role: "VIEWER",
      step: "where",
      quote: null,
      outcome: null,
      busy: null,
      message: null,
    });
    expect(env.controller.getSnapshot().draft.origin.addressText).toBe("");
  });

  it("ignores a confirmation that answers after the tenant changed", async () => {
    let release: (value: unknown) => void = () => undefined;
    const transitionOrder = vi.fn<TransitionOrder>(
      () => new Promise((resolve) => { release = resolve as (value: unknown) => void; }),
    );
    const env = setup({ [orgA]: "DISPATCHER", [orgB]: "DISPATCHER" }, {}, { transitionOrder });
    await env.controller.start();
    await reachConfirm(env.controller);
    const inFlight = env.controller.submit();
    await vi.waitFor(() => expect(transitionOrder).toHaveBeenCalled());
    env.switchTo(orgB);
    await env.controller.start();
    release({ id: orderId, status: "CONFIRMED", version: 2 });
    await inFlight;
    expect(env.controller.getSnapshot()).toMatchObject({ phase: "ready", step: "where", outcome: null, busy: null });
  });
});

describe("service window (ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02)", () => {
  it("sends the typed window and shows the server window on the confirmed order", async () => {
    const window = { from: "2026-09-28T19:00:00+00:00", to: "2026-09-28T20:00:00+00:00" };
    const { controller, orders } = setup(
      { [orgA]: "DISPATCHER" },
      { createOrder: vi.fn(async () => parseCreatedOrder(orderResponse({ service_window: window }))) },
    );
    await controller.start();
    await reachConfirm(controller, { serviceWindowFrom: "2026-09-28T12:00", serviceWindowTo: "2026-09-28T13:00" });
    await controller.submit();
    expect(vi.mocked(orders.createOrder).mock.calls[0]![0].service_window).toEqual({
      from: "2026-09-28T19:00:00Z",
      to: "2026-09-28T20:00:00Z",
    });
    expect(controller.getSnapshot().outcome?.order.service_window).toEqual(window);
  });

  it("does not leave step 3 with a window that already ended", async () => {
    const { controller } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    fillShipment(controller, { serviceWindowFrom: "2026-09-28T07:00", serviceWindowTo: "2026-09-28T09:00" });
    controller.next();
    controller.next();
    await controller.requestQuote();
    controller.next();
    expect(controller.getSnapshot().step).toBe("service");
    expect(controller.getSnapshot().fieldErrors).toEqual([
      { field: "serviceWindow", message: serviceWindowMessages.ended },
    ]);
  });
});

describe("low price authorization (LOW-PRICE-MANUAL-AUTH-2026-10-02)", () => {
  const at52 = (overrides: Record<string, unknown> = {}) =>
    parseQuote(quoteResponse({
      net: { currency: "MXN", amount_cents: 4_483 },
      tax: { currency: "MXN", amount_cents: 717 },
      total: { currency: "MXN", amount_cents: 5_200 },
      pricing_tier: "BUSINESS_200_499",
      ...overrides,
    }));

  it.each(["DISPATCHER", "PLATFORM_ADMIN"])("offers the option to %s", async (role) => {
    const { controller } = setup({ [orgA]: role });
    await controller.start();
    expect(controller.getSnapshot().canAuthorizeLowPrice).toBe(true);
  });

  it.each(["VIEWER", "FINANCE", "DRIVER"])("never offers it to %s", async (role) => {
    const { controller } = setup({ [orgA]: role });
    await controller.start();
    expect(controller.getSnapshot().canAuthorizeLowPrice).toBe(false);
  });

  it("holds a 52 MXN price at step 3, then authorizes it with the reason on createQuote", async () => {
    const createQuote = vi
      .fn()
      .mockResolvedValueOnce(at52())
      .mockResolvedValueOnce(at52({
        low_price_authorization: { valid_until: "2026-09-28T18:00:00+00:00", reason: "Cliente ancla" },
      }));
    const { controller, orders } = setup({ [orgA]: "DISPATCHER" }, { createQuote });
    await controller.start();
    fillShipment(controller);
    controller.next();
    controller.next();
    await controller.requestQuote();
    controller.next();
    expect(controller.getSnapshot().step).toBe("service");
    expect(controller.getSnapshot().fieldErrors).toEqual([{ field: "quote", message: confirmationBlockerLabels.low_price }]);
    // The guard keeps the price on screen; authorizing changes the request, so the price is dropped.
    expect(controller.getSnapshot().quote?.total.amount_cents).toBe(5_200);
    controller.updateDraft({ authorizeLowPrice: true });
    expect(controller.getSnapshot().quote).toBeNull();
    await controller.requestQuote();
    expect(fields(controller)).toEqual(["lowPriceReason"]);
    expect(createQuote).toHaveBeenCalledTimes(1);
    controller.updateDraft({ lowPriceReason: " Cliente ancla " });
    await controller.requestQuote();
    expect((createQuote.mock.calls[1] as unknown[])[0]).toMatchObject({ low_price_authorization: { reason: "Cliente ancla" } });
    controller.next();
    controller.updateDraft({ payerType: "SENDER", accepted: true, restrictedGoodsAcknowledged: true });
    await controller.submit();
    const body = (vi.mocked(orders.createOrder).mock.calls[0] as unknown[])[0] as Record<string, unknown>;
    expect(body).not.toHaveProperty("low_price_authorization");
    expect(controller.getSnapshot().outcome?.kind).toBe("confirmed");
  });

  it("explains the uniform 409 of an authorization the price does not need", async () => {
    const { controller } = setup(
      { [orgA]: "DISPATCHER" },
      { createQuote: vi.fn().mockRejectedValue(new TenantApiError("conflict")) },
    );
    await controller.start();
    fillShipment(controller, { authorizeLowPrice: true, lowPriceReason: "Cliente ancla" });
    controller.next();
    controller.next();
    await controller.requestQuote();
    expect(controller.getSnapshot().message).toBe(lowPriceAuthorizationNotNeededMessage);
  });

  it("offers the MFA step-up when a PLATFORM_ADMIN without MFA authorizes", async () => {
    const { controller } = setup(
      { [orgA]: "PLATFORM_ADMIN" },
      { createQuote: vi.fn().mockRejectedValue(new TenantApiError("forbidden", "MFA_REQUIRED", true)) },
    );
    await controller.start();
    fillShipment(controller, { authorizeLowPrice: true, lowPriceReason: "Cliente ancla" });
    controller.next();
    controller.next();
    await controller.requestQuote();
    expect(controller.getSnapshot().stepUpHref).toBe("/login?mfa=required&return_url=%2Fops%2Forders%2Fnew");
  });
});
