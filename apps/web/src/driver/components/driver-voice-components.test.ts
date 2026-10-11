import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

const source = (path: string) => readFileSync(resolve(process.cwd(), "src/driver", path), "utf8");
const recipientCall = source("components/recipient-call.tsx");
const phoneSettings = source("components/driver-phone-settings.tsx");
const account = source("components/driver-account.tsx");
const experience = source("components/driver-stops-experience.tsx");
const voiceSources = [
  recipientCall,
  phoneSettings,
  source("api/voice-api.ts"),
  source("contracts/voice.ts"),
  source("state/recipient-call-controller.ts"),
  source("state/driver-phone-controller.ts"),
];

describe("VOICE-001 driver PWA surfaces", () => {
  it("offers the call on the stop detail, after the next action, for the stop's own order", () => {
    const action = experience.indexOf("<StopAction");
    const call = experience.indexOf("<RecipientCall");
    expect(action).toBeGreaterThan(0);
    expect(call).toBeGreaterThan(action);
    expect(experience).toContain("orderId={stop.order_id}");
    expect(experience).toContain("stopType={stop.stop_type}");
    expect(experience).toContain("confirmedStatus={stop.confirmedStatus}");
    expect(recipientCall).toContain("isRecipientCallEligible(stopType, confirmedStatus)");
    expect(recipientCall).toContain(": recipientCallLabel}");
  });

  it("keeps the call online-only and out of the offline queue, the cache and browser storage", () => {
    for (const text of voiceSources) {
      for (const forbidden of [
        "localStorage",
        "sessionStorage",
        "indexedDB",
        "IndexedDb",
        "driver-offline-queue",
        "DriverOperationsController",
        "enqueue(",
        "caches.",
      ]) {
        expect(text).not.toContain(forbidden);
      }
    }
    expect(recipientCall).toContain('window.addEventListener("offline", refresh)');
    expect(recipientCall).toContain('disabled={state.phase !== "available" || !connected}');
  });

  it("never renders a phone number: the PWA only receives a request id and a status", () => {
    expect(recipientCall).not.toMatch(/recipient_phone|driver_phone|E164|\+52/);
    expect(recipientCall).not.toContain("tel:");
    expect(experience).not.toContain("tel:");
    expect(phoneSettings).not.toContain("{phone}<");
    expect(phoneSettings).toContain("No lo mostramos por seguridad.");
  });

  it("registers the driver's own phone only with the consent text and clears the field afterwards", () => {
    expect(phoneSettings).toContain("{driverPhoneConsentText}");
    expect(phoneSettings).toContain('type="checkbox"');
    expect(phoneSettings).toContain('type="tel"');
    expect(phoneSettings).toContain('autoComplete="off"');
    expect(phoneSettings).toContain('setPhone("");');
    expect(phoneSettings).toContain("setConsent(false);");
    expect(phoneSettings).toContain("Borrar mi celular");
    expect(phoneSettings).toContain("state.voiceCallsEnabled ? (");
  });

  it("opens Cuenta from the header and reads the API only once opened", () => {
    expect(account).toContain("<summary>Cuenta</summary>");
    expect(account).toContain("{accountOpen ? <DriverPhoneSettings session={session} /> : null}");
  });
});
