import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import {
  driverPhoneConsentText,
  driverPhoneConsentVersion,
  normalizeDriverPhone,
  parseDriverPhoneStatus,
  parseRecipientCallAvailability,
  parseRecipientCallRequest,
  recipientCallConflictCodes,
  recipientCallConflictMessage,
  recipientCallLabel,
  recipientCallOfflineMessage,
  recipientCallPlacedMessage,
  recipientCallReasonHint,
  recipientCallReasons,
  VoiceContractError,
} from "./voice";

const repository = (path: string) => readFileSync(resolve(process.cwd(), "../..", path), "utf8");
const uiContracts = repository("docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml").replace(/\s+/g, " ");
const openApi = repository("docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml");

describe("VOICE-001 contracts", () => {
  it("shows exactly the AI-07 texts and consent", () => {
    expect(uiContracts).toContain(`"${driverPhoneConsentText}" (consent_version ${driverPhoneConsentVersion})`);
    expect(uiContracts).toContain(`"${recipientCallLabel}"`);
    expect(uiContracts).toContain(`"${recipientCallPlacedMessage}"`);
    expect(uiContracts).toContain(`"${recipientCallOfflineMessage}"`);
    expect(uiContracts).toContain("masked phone or relay");
  });

  it("names the consent version the server requires", () => {
    const server = repository("src/Modules/Drivers/Drivers.Application/Voice/DriverPhoneContracts.cs");
    expect(server).toContain(`public const string CurrentVersion = "${driverPhoneConsentVersion}";`);
    expect(openApi).toContain(`const: ${driverPhoneConsentVersion}`);
  });

  it("mirrors the AI-05 closed reasons and conflict codes", () => {
    const availability = slice(openApi, "    RecipientCallAvailability:", "    RecipientCallRequest:");
    for (const reason of recipientCallReasons) expect(availability).toContain(`- ${reason}`);
    const conflict = slice(openApi, "    RecipientCallConflictProblem:", "    DriverPhoneInvalidRequestProblem:");
    for (const code of recipientCallConflictCodes) expect(conflict).toContain(`- ${code}`);
  });

  it("parses availability with a reason only when not available", () => {
    expect(parseRecipientCallAvailability({ available: true, reason: null })).toEqual({ available: true, reason: null });
    expect(parseRecipientCallAvailability({ available: false, reason: "DRIVER_PHONE_REQUIRED" }))
      .toEqual({ available: false, reason: "DRIVER_PHONE_REQUIRED" });
    for (const invalid of [
      { available: true, reason: "RATE_LIMITED" },
      { available: false, reason: null },
      { available: false, reason: "SOMETHING_ELSE" },
      { available: "yes", reason: null },
      { available: true, reason: null, phone: "5511112222" },
      { available: true },
      null,
      [],
    ]) {
      expect(() => parseRecipientCallAvailability(invalid)).toThrow(VoiceContractError);
    }
  });

  it("parses a call request that never carries a number", () => {
    const id = "e4000000-0000-4000-8000-000000000001";
    expect(parseRecipientCallRequest({ call_request_id: id, status: "PLACED" })).toEqual({ call_request_id: id, status: "PLACED" });
    for (const invalid of [
      { call_request_id: id, status: "FAILED" },
      { call_request_id: "00000000-0000-0000-0000-000000000000", status: "PLACED" },
      { call_request_id: id.toUpperCase(), status: "PLACED" },
      { call_request_id: id, status: "PLACED", recipient_phone: "+523312345678" },
      { call_request_id: id, status: "PLACED", driver_phone: "+525511112222" },
    ]) {
      expect(() => parseRecipientCallRequest(invalid)).toThrow(VoiceContractError);
    }
  });

  it("parses the phone status without ever accepting a number", () => {
    expect(parseDriverPhoneStatus({ voice_calls_enabled: true, registered: false, consent_version: null, consented_at: null }))
      .toEqual({ voice_calls_enabled: true, registered: false, consent_version: null, consented_at: null });
    expect(parseDriverPhoneStatus({
      voice_calls_enabled: true,
      registered: true,
      consent_version: driverPhoneConsentVersion,
      consented_at: "2026-10-11T16:00:00+00:00",
    }).registered).toBe(true);
    for (const invalid of [
      { voice_calls_enabled: true, registered: true, consent_version: null, consented_at: null },
      { voice_calls_enabled: true, registered: false, consent_version: driverPhoneConsentVersion, consented_at: null },
      { voice_calls_enabled: true, registered: true, consent_version: "OTHER", consented_at: "2026-10-11T16:00:00Z" },
      { voice_calls_enabled: true, registered: true, consent_version: driverPhoneConsentVersion, consented_at: "ayer" },
      { voice_calls_enabled: true, registered: false, consent_version: null, consented_at: null, phone: "5511112222" },
    ]) {
      expect(() => parseDriverPhoneStatus(invalid)).toThrow(VoiceContractError);
    }
  });

  it("normalizes the driver phone exactly as the server does", () => {
    expect(normalizeDriverPhone("5511112222")).toBe("5511112222");
    expect(normalizeDriverPhone("+52 55 1111 2222")).toBe("5511112222");
    expect(normalizeDriverPhone(" 33-1234-5678")).toBe("3312345678");
    for (const invalid of ["", "0511112222", "1511112222", "551111222", "55111122223", "+1 555 111 2222", "55 1111 2222 ext", "+52 (55) 1111 2222", "x".repeat(33)]) {
      expect(normalizeDriverPhone(invalid)).toBeNull();
    }
  });

  it("explains refusals without a number", () => {
    expect(recipientCallReasonHint("DRIVER_PHONE_REQUIRED")).toContain("Cuenta");
    expect(recipientCallReasonHint("RATE_LIMITED")).not.toBeNull();
    expect(recipientCallReasonHint("VOICE_CALLS_DISABLED")).toBeNull();
    expect(recipientCallReasonHint("ORDER_STATE_NOT_ALLOWED")).toBeNull();
    expect(recipientCallReasonHint("RECIPIENT_PHONE_UNAVAILABLE")).toBeNull();
    for (const code of recipientCallConflictCodes) {
      expect(recipientCallConflictMessage(code)).not.toMatch(/\d/);
      expect(recipientCallConflictMessage(code).toLowerCase()).not.toContain("teléfono");
    }
  });
});

function slice(source: string, start: string, end: string): string {
  const from = source.indexOf(start);
  const to = source.indexOf(end, from + start.length);
  expect(from).toBeGreaterThanOrEqual(0);
  expect(to).toBeGreaterThan(from);
  return source.slice(from, to);
}
