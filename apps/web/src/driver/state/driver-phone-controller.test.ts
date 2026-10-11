import { describe, expect, it } from "vitest";
import { DriverVoiceApiError, type DriverVoiceApi } from "../api/voice-api";
import type { DriverPhoneStatus } from "../contracts/voice";
import { DriverPhoneController, driverPhoneMessages } from "./driver-phone-controller";

type PhoneApi = Pick<DriverVoiceApi, "getPhone" | "registerPhone" | "removePhone">;

const none: DriverPhoneStatus = { voice_calls_enabled: true, registered: false, consent_version: null, consented_at: null };
const stored: DriverPhoneStatus = {
  voice_calls_enabled: true,
  registered: true,
  consent_version: "VOICE-001-CONSENT-V1",
  consented_at: "2026-10-11T16:00:00+00:00",
};

function api(initial: DriverPhoneStatus | Error, next: Array<DriverPhoneStatus | Error> = []): PhoneApi & { sent: string[]; removals: number } {
  const sent: string[] = [];
  const counters = { removals: 0 };
  const take = async () => {
    const value = next.shift();
    if (!value) throw new Error("unexpected call");
    if (value instanceof Error) throw value;
    return value;
  };
  return {
    sent,
    get removals() {
      return counters.removals;
    },
    async getPhone() {
      if (initial instanceof Error) throw initial;
      return initial;
    },
    async registerPhone(digits: string) {
      sent.push(digits);
      return take();
    },
    async removePhone() {
      counters.removals++;
      return take();
    },
  };
}

describe("VOICE-001 Cuenta: the driver's own phone", () => {
  it("collects nothing while the bridge is off", async () => {
    const controller = new DriverPhoneController(api({ ...none, voice_calls_enabled: false }));

    await controller.load(true);

    expect(controller.state.phase).toBe("hidden");
    expect(await controller.register("5511112222", true, true)).toBe(false);
  });

  it("still lets the driver erase a number stored before the bridge was turned off", async () => {
    const fake = api({ ...stored, voice_calls_enabled: false }, [{ ...none, voice_calls_enabled: false }]);
    const controller = new DriverPhoneController(fake);
    await controller.load(true);
    expect(controller.state).toMatchObject({ phase: "ready", voiceCallsEnabled: false, registered: true });

    expect(await controller.register("5511112222", true, true)).toBe(false);
    expect(await controller.remove(true)).toBe(true);

    expect(fake.sent).toEqual([]);
    expect(fake.removals).toBe(1);
    expect(controller.state.phase).toBe("hidden");
  });

  it("validates the number and the consent before sending anything", async () => {
    const fake = api(none);
    const controller = new DriverPhoneController(fake);
    await controller.load(true);

    expect(await controller.register("0511112222", true, true)).toBe(false);
    expect(controller.state.message).toBe(driverPhoneMessages.invalid);
    expect(await controller.register("55 1111 2222", false, true)).toBe(false);
    expect(controller.state.message).toBe(driverPhoneMessages.consentRequired);
    expect(await controller.register("55 1111 2222", true, false)).toBe(false);
    expect(controller.state.message).toBe(driverPhoneMessages.offline);

    expect(fake.sent).toEqual([]);
  });

  it("sends the ten digits once and keeps only that a number is registered", async () => {
    const fake = api(none, [stored]);
    const controller = new DriverPhoneController(fake);
    await controller.load(true);

    expect(await controller.register("+52 55 1111 2222", true, true)).toBe(true);

    expect(fake.sent).toEqual(["5511112222"]);
    expect(controller.state).toEqual({
      phase: "ready",
      voiceCallsEnabled: true,
      registered: true,
      consentedAt: "2026-10-11T16:00:00+00:00",
      message: driverPhoneMessages.saved,
    });
    expect(JSON.stringify(controller.state)).not.toContain("1111");
  });

  it("explains a refused number and an unavailable service", async () => {
    const fake = api(none, [new DriverVoiceApiError("conflict", "INVALID_REQUEST"), new DriverVoiceApiError("unavailable")]);
    const controller = new DriverPhoneController(fake);
    await controller.load(true);

    expect(await controller.register("5511112222", true, true)).toBe(false);
    expect(controller.state).toMatchObject({ phase: "ready", message: driverPhoneMessages.rejected });
    expect(await controller.register("5511112222", true, true)).toBe(false);
    expect(controller.state).toMatchObject({ phase: "ready", message: driverPhoneMessages.unavailable });
  });

  it("removes the number online only", async () => {
    const fake = api(stored, [none]);
    const controller = new DriverPhoneController(fake);
    await controller.load(true);

    expect(await controller.remove(false)).toBe(false);
    expect(controller.state.message).toBe(driverPhoneMessages.offline);
    expect(await controller.remove(true)).toBe(true);
    expect(controller.state).toMatchObject({ phase: "ready", registered: false, message: driverPhoneMessages.removed });
  });

  it("hides the panel for an account that is not a driver and reports other failures", async () => {
    const forbidden = new DriverPhoneController(api(new DriverVoiceApiError("forbidden")));
    await forbidden.load(true);
    expect(forbidden.state.phase).toBe("hidden");

    const failing = new DriverPhoneController(api(new DriverVoiceApiError("unavailable")));
    await failing.load(true);
    expect(failing.state).toMatchObject({ phase: "error", message: driverPhoneMessages.loadFailed });

    const offline = new DriverPhoneController(api(none));
    await offline.load(false);
    expect(offline.state).toMatchObject({ phase: "error", message: driverPhoneMessages.offline });
  });
});
