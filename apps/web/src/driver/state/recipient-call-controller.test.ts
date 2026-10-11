import { describe, expect, it, vi } from "vitest";
import { DriverVoiceApiError, type DriverVoiceApi } from "../api/voice-api";
import {
  recipientCallOfflineMessage,
  recipientCallPlacedMessage,
  type RecipientCallAvailability,
  type RecipientCallRequest,
} from "../contracts/voice";
import {
  isRecipientCallEligible,
  recipientCallNetworkMessage,
  recipientCallUnavailableMessage,
  recipientCallUnconfirmedMessage,
  RecipientCallController,
} from "./recipient-call-controller";

const orderId = "33333333-3333-4333-8333-333333333333";
type CallApi = Pick<DriverVoiceApi, "getRecipientCallAvailability" | "requestRecipientCall">;

function api(
  availability: RecipientCallAvailability | Error = { available: true, reason: null },
  requests: Array<RecipientCallRequest | Error> = [],
): CallApi & { keys: string[]; checks: number } {
  const keys: string[] = [];
  const state = { checks: 0 };
  return {
    keys,
    get checks() {
      return state.checks;
    },
    async getRecipientCallAvailability(id: string) {
      expect(id).toBe(orderId);
      state.checks++;
      if (availability instanceof Error) throw availability;
      return availability;
    },
    async requestRecipientCall(id: string, key: string) {
      expect(id).toBe(orderId);
      keys.push(key);
      const next = requests.shift();
      if (!next) throw new Error("unexpected call");
      if (next instanceof Error) throw next;
      return next;
    },
  };
}

function keys(): () => string {
  let counter = 0;
  return () => `voice-001-web-key-${String(++counter).padStart(4, "0")}`;
}

describe("VOICE-001 Llamar al destinatario", () => {
  it("is offered only on a delivery stop being delivered", () => {
    expect(isRecipientCallEligible("DELIVERY", "DELIVERING")).toBe(true);
    for (const [type, status] of [
      ["PICKUP", "DELIVERING"],
      ["RETURN", "DELIVERING"],
      ["DELIVERY", "IN_TRANSIT"],
      ["DELIVERY", "PICKED_UP"],
      ["DELIVERY", "FAILED_ATTEMPT"],
    ]) {
      expect(isRecipientCallEligible(type, status)).toBe(false);
    }
  });

  it("asks nothing for other stops and hides itself", async () => {
    const fake = api();
    const controller = new RecipientCallController(fake, orderId, keys());

    await controller.update(false, true);

    expect(controller.state).toEqual({ phase: "hidden", message: null });
    expect(fake.checks).toBe(0);
  });

  it("is shown only when the server says it is available", async () => {
    const fake = api({ available: true, reason: null });
    const controller = new RecipientCallController(fake, orderId, keys());

    await controller.update(true, true);

    expect(controller.state.phase).toBe("available");
  });

  it.each([
    ["VOICE_CALLS_DISABLED", "hidden"],
    ["ORDER_STATE_NOT_ALLOWED", "hidden"],
    ["RECIPIENT_PHONE_UNAVAILABLE", "hidden"],
    ["DRIVER_PHONE_REQUIRED", "hint"],
    ["RATE_LIMITED", "hint"],
  ] as const)("answers %s with %s", async (reason, phase) => {
    const controller = new RecipientCallController(api({ available: false, reason }), orderId, keys());

    await controller.update(true, true);

    expect(controller.state.phase).toBe(phase);
    if (reason === "DRIVER_PHONE_REQUIRED") expect(controller.state.message).toContain("Cuenta");
  });

  it("is disabled offline with the explanation and never queues a call", async () => {
    const fake = api();
    const controller = new RecipientCallController(fake, orderId, keys());

    await controller.update(true, false);
    expect(controller.state).toEqual({ phase: "offline", message: recipientCallOfflineMessage });
    expect(fake.checks).toBe(0);

    await controller.update(true, true);
    expect(controller.state.phase).toBe("available");
    await controller.call(false);

    expect(controller.state).toEqual({ phase: "offline", message: recipientCallOfflineMessage });
    expect(fake.keys).toEqual([]);
  });

  it("stays hidden offline once the server said the call cannot exist here", async () => {
    const controller = new RecipientCallController(api({ available: false, reason: "VOICE_CALLS_DISABLED" }), orderId, keys());

    await controller.update(true, true);
    await controller.update(true, false);

    expect(controller.state.phase).toBe("hidden");
  });

  it("says the call is on its way and uses a new key for every tap", async () => {
    const fake = api({ available: true, reason: null }, [
      { call_request_id: "44444444-4444-4444-8444-444444444444", status: "PLACED" },
      { call_request_id: "55555555-5555-4555-8555-555555555555", status: "REQUESTED" },
    ]);
    const controller = new RecipientCallController(fake, orderId, keys());
    await controller.update(true, true);
    const phases: string[] = [];
    controller.subscribe((state) => phases.push(state.phase));

    await controller.call(true);
    expect(controller.state).toEqual({ phase: "available", message: recipientCallPlacedMessage });
    await controller.call(true);
    expect(controller.state).toEqual({ phase: "available", message: recipientCallPlacedMessage });

    expect(fake.keys).toEqual(["voice-001-web-key-0001", "voice-001-web-key-0002"]);
    expect(phases).toEqual(["available", "calling", "available", "calling", "available"]);
  });

  it("explains an unconfirmed call", async () => {
    const controller = new RecipientCallController(
      api({ available: true, reason: null }, [{ call_request_id: "44444444-4444-4444-8444-444444444444", status: "UNCONFIRMED" }]),
      orderId,
      keys(),
    );
    await controller.update(true, true);

    await controller.call(true);

    expect(controller.state).toEqual({ phase: "available", message: recipientCallUnconfirmedMessage });
  });

  it("retries the same tap with the same key only when no answer arrived", async () => {
    const fake = api({ available: true, reason: null }, [
      new DriverVoiceApiError("network"),
      { call_request_id: "44444444-4444-4444-8444-444444444444", status: "PLACED" },
      new DriverVoiceApiError("unavailable"),
      { call_request_id: "55555555-5555-4555-8555-555555555555", status: "PLACED" },
    ]);
    const controller = new RecipientCallController(fake, orderId, keys());
    await controller.update(true, true);

    await controller.call(true);
    expect(controller.state).toEqual({ phase: "available", message: recipientCallNetworkMessage });
    await controller.call(true);
    await controller.call(true);
    expect(controller.state).toEqual({ phase: "available", message: recipientCallUnavailableMessage });
    await controller.call(true);

    expect(fake.keys).toEqual([
      "voice-001-web-key-0001",
      "voice-001-web-key-0001",
      "voice-001-web-key-0002",
      "voice-001-web-key-0003",
    ]);
  });

  it.each([
    ["DRIVER_PHONE_REQUIRED", "hint", "Cuenta"],
    ["DRIVER_PHONE_REJECTED", "hint", "Cuenta"],
    ["RECIPIENT_PHONE_UNAVAILABLE", "hint", "número de contacto"],
    ["ORDER_STATE_NOT_ALLOWED", "hint", "mientras"],
    ["IDEMPOTENCY_CONFLICT", "available", "Intenta de nuevo"],
  ] as const)("turns the 409 %s into a %s", async (code, phase, text) => {
    const controller = new RecipientCallController(
      api({ available: true, reason: null }, [new DriverVoiceApiError("conflict", code)]),
      orderId,
      keys(),
    );
    await controller.update(true, true);

    await controller.call(true);

    expect(controller.state.phase).toBe(phase);
    expect(controller.state.message).toContain(text);
  });

  it("tells how long to wait after too many calls", async () => {
    const controller = new RecipientCallController(
      api({ available: true, reason: null }, [new DriverVoiceApiError("rate-limited", null, 600)]),
      orderId,
      keys(),
    );
    await controller.update(true, true);

    await controller.call(true);

    expect(controller.state).toEqual({
      phase: "hint",
      message: "Ya pediste varias llamadas para esta entrega. Intenta de nuevo en 10 minutos.",
    });
  });

  it("hides itself when the stop is no longer the driver's and ignores a late answer after dispose", async () => {
    const controller = new RecipientCallController(
      api({ available: true, reason: null }, [new DriverVoiceApiError("not-found")]),
      orderId,
      keys(),
    );
    await controller.update(true, true);
    await controller.call(true);
    expect(controller.state.phase).toBe("hidden");

    const listener = vi.fn();
    const late = new RecipientCallController(api(), orderId, keys());
    late.subscribe(listener);
    const pending = late.update(true, true);
    late.dispose();
    await pending;
    expect(listener).toHaveBeenCalledTimes(2);
    expect(late.state.phase).toBe("checking");
  });
});
