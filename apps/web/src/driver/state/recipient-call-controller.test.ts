import { afterEach, describe, expect, it, vi } from "vitest";
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
type Answer<T> = T | Error | Promise<T>;

const available: RecipientCallAvailability = { available: true, reason: null, retry_after_seconds: null };
const notAvailable = (reason: Exclude<RecipientCallAvailability["reason"], null>): RecipientCallAvailability =>
  ({ available: false, reason, retry_after_seconds: null });
const rateLimited = (seconds: number): RecipientCallAvailability =>
  ({ available: false, reason: "RATE_LIMITED", retry_after_seconds: seconds });
const placed: RecipientCallRequest = { call_request_id: "44444444-4444-4444-8444-444444444444", status: "PLACED" };

/**
 * `availability` is the answer of every check, or a queue of answers (the last one repeats); `requests` answers the
 * taps in order.
 */
function api(
  availability: Answer<RecipientCallAvailability> | Array<Answer<RecipientCallAvailability>> = available,
  requests: Array<Answer<RecipientCallRequest>> = [],
): CallApi & { keys: string[]; checks: number } {
  const keys: string[] = [];
  const state = { checks: 0 };
  const answers = Array.isArray(availability) ? availability : [availability];
  return {
    keys,
    get checks() {
      return state.checks;
    },
    async getRecipientCallAvailability(id: string) {
      expect(id).toBe(orderId);
      state.checks++;
      const next = answers.length > 1 ? answers.shift()! : answers[0];
      if (next instanceof Error) throw next;
      return next;
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

function deferred<T>(): { promise: Promise<T>; resolve: (value: T) => void } {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

afterEach(() => {
  vi.useRealTimers();
});

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
    const fake = api(available);
    const controller = new RecipientCallController(fake, orderId, keys());

    await controller.update(true, true);

    expect(controller.state.phase).toBe("available");
  });

  it.each([
    ["VOICE_CALLS_DISABLED", "hidden"],
    ["ORDER_STATE_NOT_ALLOWED", "hidden"],
    ["RECIPIENT_PHONE_UNAVAILABLE", "hidden"],
    ["DRIVER_PHONE_REQUIRED", "hint"],
  ] as const)("answers %s with %s", async (reason, phase) => {
    const controller = new RecipientCallController(api(notAvailable(reason)), orderId, keys());

    await controller.update(true, true);

    expect(controller.state.phase).toBe(phase);
    if (reason === "DRIVER_PHONE_REQUIRED") expect(controller.state.message).toContain("Cuenta");
  });

  it("answers RATE_LIMITED with a hint", async () => {
    vi.useFakeTimers();
    const controller = new RecipientCallController(api(rateLimited(300)), orderId, keys());

    await controller.update(true, true);

    expect(controller.state.phase).toBe("hint");
    controller.dispose();
  });

  it("shows nothing offline until the server confirmed the call at least once", async () => {
    const fake = api();
    const controller = new RecipientCallController(fake, orderId, keys());

    // Offline before any answer: no disabled button for a call that might never work here.
    await controller.update(true, false);
    expect(controller.state).toEqual({ phase: "hidden", message: null });
    expect(fake.checks).toBe(0);

    await controller.update(true, true);
    expect(controller.state.phase).toBe("available");
    await controller.update(true, false);
    expect(controller.state).toEqual({ phase: "offline", message: recipientCallOfflineMessage });

    await controller.update(true, true);
    expect(controller.state.phase).toBe("available");
    expect(fake.checks).toBe(2);
  });

  it("forgets the confirmation when the server says no, keeping only a hint offline", async () => {
    const fake = api([available, notAvailable("DRIVER_PHONE_REQUIRED"), notAvailable("VOICE_CALLS_DISABLED")]);
    const controller = new RecipientCallController(fake, orderId, keys());
    await controller.update(true, true);
    expect(controller.state.phase).toBe("available");

    await controller.update(true, true);
    expect(controller.state.phase).toBe("hint");
    await controller.update(true, false);
    expect(controller.state.phase).toBe("hint");

    await controller.update(true, true);
    expect(controller.state.phase).toBe("hidden");
    await controller.update(true, false);
    expect(controller.state.phase).toBe("hidden");
  });

  it("is disabled offline with the explanation and never queues a call", async () => {
    const fake = api();
    const controller = new RecipientCallController(fake, orderId, keys());

    await controller.update(true, true);
    expect(controller.state.phase).toBe("available");
    await controller.call(false);

    expect(controller.state).toEqual({ phase: "offline", message: recipientCallOfflineMessage });
    expect(fake.keys).toEqual([]);
  });

  it("says the call is on its way and uses a new key for every tap", async () => {
    const fake = api(available, [
      placed,
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
      api(available, [{ call_request_id: "44444444-4444-4444-8444-444444444444", status: "UNCONFIRMED" }]),
      orderId,
      keys(),
    );
    await controller.update(true, true);

    await controller.call(true);

    expect(controller.state).toEqual({ phase: "available", message: recipientCallUnconfirmedMessage });
  });

  it("retries the same tap with the same key only when no answer arrived", async () => {
    const fake = api(available, [
      new DriverVoiceApiError("network"),
      placed,
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
      api(available, [new DriverVoiceApiError("conflict", code)]),
      orderId,
      keys(),
    );
    await controller.update(true, true);

    await controller.call(true);

    expect(controller.state.phase).toBe(phase);
    expect(controller.state.message).toContain(text);
  });

  it("tells how long to wait after too many calls", async () => {
    vi.useFakeTimers();
    const controller = new RecipientCallController(
      api(available, [new DriverVoiceApiError("rate-limited", null, 600)]),
      orderId,
      keys(),
    );
    await controller.update(true, true);

    await controller.call(true);

    expect(controller.state).toEqual({
      phase: "hint",
      message: "Ya pediste varias llamadas para esta entrega. Intenta de nuevo en 10 minutos.",
    });
    controller.dispose();
  });

  it("checks again once when the Retry-After of a refused tap is over", async () => {
    vi.useFakeTimers();
    const fake = api(available, [new DriverVoiceApiError("rate-limited", null, 90)]);
    const controller = new RecipientCallController(fake, orderId, keys());
    await controller.update(true, true);
    await controller.call(true);
    expect(controller.state.phase).toBe("hint");
    expect(fake.checks).toBe(1);

    await vi.advanceTimersByTimeAsync(89_000);
    expect(fake.checks).toBe(1);
    await vi.advanceTimersByTimeAsync(1_000);
    expect(fake.checks).toBe(2);
    expect(controller.state.phase).toBe("available");

    // Only one re-check: nothing polls afterwards.
    await vi.advanceTimersByTimeAsync(3_600_000);
    expect(fake.checks).toBe(2);
  });

  it("checks again when a RATE_LIMITED availability's wait is over, never offline", async () => {
    vi.useFakeTimers();
    const fake = api([rateLimited(120), rateLimited(30), available]);
    const controller = new RecipientCallController(fake, orderId, keys());

    await controller.update(true, true);
    expect(controller.state.phase).toBe("hint");
    await vi.advanceTimersByTimeAsync(120_000);
    expect(fake.checks).toBe(2);
    expect(controller.state.phase).toBe("hint");

    // Gone offline meanwhile: the re-check keeps the hint and reads nothing.
    await controller.update(true, false);
    expect(controller.state.phase).toBe("hint");
    await vi.advanceTimersByTimeAsync(30_000);
    expect(fake.checks).toBe(2);

    await controller.update(true, true);
    expect(fake.checks).toBe(3);
    expect(controller.state.phase).toBe("available");
  });

  it("drops a pending re-check when the stop is no longer offered or the view goes away", async () => {
    vi.useFakeTimers();
    const fake = api([rateLimited(60), rateLimited(60), available]);
    const controller = new RecipientCallController(fake, orderId, keys());
    await controller.update(true, true);
    await controller.update(false, true);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(fake.checks).toBe(1);
    expect(controller.state.phase).toBe("hidden");

    await controller.update(true, true);
    expect(fake.checks).toBe(2);
    controller.dispose();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(fake.checks).toBe(2);
  });

  it("ignores the answer of a tap that arrives after the stop stopped being offered", async () => {
    const answer = deferred<RecipientCallRequest>();
    const fake = api(available, [answer.promise]);
    const controller = new RecipientCallController(fake, orderId, keys());
    await controller.update(true, true);
    const phases: string[] = [];
    controller.subscribe((state) => phases.push(state.phase));

    const tap = controller.call(true);
    expect(controller.state.phase).toBe("calling");
    await controller.update(false, true);
    answer.resolve(placed);
    await tap;

    expect(controller.state).toEqual({ phase: "hidden", message: null });
    expect(phases).toEqual(["available", "calling", "hidden"]);
  });

  it("never interrupts a tap in flight when the connection or the stop data refresh", async () => {
    const answer = deferred<RecipientCallRequest>();
    const fake = api(available, [answer.promise]);
    const controller = new RecipientCallController(fake, orderId, keys());
    await controller.update(true, true);

    const tap = controller.call(true);
    await controller.update(true, false);
    await controller.update(true, true);
    expect(controller.state.phase).toBe("calling");
    answer.resolve(placed);
    await tap;

    expect(controller.state).toEqual({ phase: "available", message: recipientCallPlacedMessage });
    expect(fake.checks).toBe(1);
  });

  it("hides itself when the stop is no longer the driver's and ignores a late answer after dispose", async () => {
    const controller = new RecipientCallController(
      api(available, [new DriverVoiceApiError("not-found")]),
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

    const answer = deferred<RecipientCallRequest>();
    const disposed = new RecipientCallController(api(available, [answer.promise]), orderId, keys());
    await disposed.update(true, true);
    const tap = disposed.call(true);
    disposed.dispose();
    answer.resolve(placed);
    await tap;
    expect(disposed.state.phase).toBe("calling");
  });
});
