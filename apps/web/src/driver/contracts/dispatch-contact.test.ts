import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import {
  DispatchContactContractError,
  dispatchPhoneE164Pattern,
  driverDispatchCallHref,
  driverDispatchCallLabel,
  parseDispatchPhone,
  parseMyDispatchContact,
} from "./dispatch-contact";

/**
 * DRV-DISPATCH-CONTACT-2026-10-12: the answer of getMyDispatchContact is the
 * closed object {"phone_e164": "+52…" | null}; the app dials it with a tel:
 * link labelled "Llamar a despacho". The numbers are synthetic.
 */
const syntheticNumber = "+522000000001";
const openApi = readFileSync(
  resolve(process.cwd(), "../../docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml"),
  "utf8",
);

describe("getMyDispatchContact answer", () => {
  it("reads a number of the AI-05 form, or null when none is configured", () => {
    expect(parseMyDispatchContact({ phone_e164: syntheticNumber })).toBe(syntheticNumber);
    expect(parseMyDispatchContact({ phone_e164: "+529990000000" })).toBe("+529990000000");
    expect(parseMyDispatchContact({ phone_e164: null })).toBeNull();
  });

  it.each<[string, unknown]>([
    ["no object", null],
    ["a list", [syntheticNumber]],
    ["a bare number", syntheticNumber],
    ["no member", {}],
    ["another member too", { phone_e164: syntheticNumber, label: "Despacho" }],
    ["another name", { phone: syntheticNumber }],
    ["a number value", { phone_e164: 522000000001 }],
    ["no country code", { phone_e164: "2000000001" }],
    ["52 without +", { phone_e164: "522000000001" }],
    ["another country", { phone_e164: "+12025550100" }],
    ["a first digit 0", { phone_e164: "+520000000001" }],
    ["a first digit 1", { phone_e164: "+521000000001" }],
    ["nine digits", { phone_e164: "+52200000000" }],
    ["eleven digits", { phone_e164: "+5220000000011" }],
    ["spaces", { phone_e164: "+52 200 000 0001" }],
    ["hyphens", { phone_e164: "+52-200-000-0001" }],
    ["a trailing line break", { phone_e164: `${syntheticNumber}\n` }],
    ["non-ASCII digits", { phone_e164: "+5220000000٠1" }],
    ["an empty text", { phone_e164: "" }],
  ])("refuses %s", (_name, value) => {
    expect(() => parseMyDispatchContact(value)).toThrow(DispatchContactContractError);
  });

  it("dials the number with a tel: link labelled as approved", () => {
    expect(driverDispatchCallHref(syntheticNumber)).toBe("tel:+522000000001");
    expect(driverDispatchCallLabel).toBe("Llamar a despacho");
    expect(() => driverDispatchCallHref("tel:+522000000001")).toThrow(DispatchContactContractError);
    expect(() => driverDispatchCallHref("+52 2000000001")).toThrow(DispatchContactContractError);
    expect(parseDispatchPhone(null)).toBeNull();
  });

  it("matches the AI-05 operation and its closed schema", () => {
    const operation = sliceBetween(openApi, "  /driver/me/dispatch-contact:", "  /voice/twilio/call-status:");
    expect(operation).toContain("operationId: getMyDispatchContact");
    expect(operation).toContain("DRV-DISPATCH-CONTACT-2026-10-12");
    expect(operation).toContain("$ref: '#/components/parameters/OrganizationContext'");
    expect(operation).toContain("$ref: '#/components/schemas/DriverDispatchContact'");
    expect(operation).toContain("Cache-Control no-store");
    const schema = sliceBetween(openApi, "    DriverDispatchContact:", "    RecipientCallAvailability:");
    expect(schema).toContain("additionalProperties: false");
    expect(schema).toContain("- phone_e164");
    expect(schema).toContain("- 'null'");
    // The web and the contract accept exactly the same numbers.
    expect(schema).toContain(`pattern: '${dispatchPhoneE164Pattern.source}'`);
  });
});

function sliceBetween(text: string, start: string, end: string): string {
  const from = text.indexOf(start);
  const to = text.indexOf(end, from + start.length);
  if (from < 0 || to < 0) throw new Error(`Missing section ${start}`);
  return text.slice(from, to);
}
