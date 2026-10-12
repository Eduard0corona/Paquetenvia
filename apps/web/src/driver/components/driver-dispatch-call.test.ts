import { readFileSync } from "node:fs";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { driverFailedDeliveryReasonsFor } from "../contracts/failed-delivery";
import { orderId } from "../offline/failed-delivery.fixtures";
import { driverCashRecordedReportMessage } from "../offline/rejection-codes";
import {
  driverCashRecordedRefusalMessage,
  driverMessageCallsDispatch,
} from "../state/driver-operations-controller";
import { DriverDispatchCall } from "./driver-dispatch-call";
import {
  FailedDeliveryForm,
  FailedDeliverySection,
  failedDeliveryCopy,
} from "./driver-failed-delivery";

/**
 * DRV-DISPATCH-CONTACT-2026-10-12 (project owner, "Sí, configurable
 * (Recommended)"): "Llamar a despacho" is a 44 px tel: link next to the texts
 * that ask the driver to call dispatch about a recorded cash collection, and
 * only when the organization's number is known; without it the text stays
 * alone. The numbers are synthetic.
 */
const syntheticNumber = "+522000000001";
const read = (path: string) => readFileSync(path, "utf8");
const css = read("src/driver/components/driver-stops.module.css");
const text = (html: string) => html.replace(/<[^>]+>/g, " ").replace(/\s+/g, " ").trim();
const noop = () => undefined;

function link(phoneE164: string | null): string {
  return renderToStaticMarkup(createElement(DriverDispatchCall, { phoneE164 }));
}

function recordedCashSection(dispatchPhone?: string | null): string {
  return renderToStaticMarkup(
    createElement(FailedDeliverySection, {
      availability: { kind: "cod-recorded" },
      orderId,
      dispatchPhone,
      disabled: false,
      onReport: () => Promise.resolve({ stored: true as const }),
      onDiscardCollection: noop,
    }),
  );
}

function form(problem: string | null, dispatchPhone: string | null): string {
  return renderToStaticMarkup(
    createElement(FailedDeliveryForm, {
      orderId,
      reasons: driverFailedDeliveryReasonsFor(true),
      primary: null,
      other: null,
      photoChosen: false,
      disabled: false,
      problem,
      dispatchPhone,
      onChoosePrimary: noop,
      onChooseOther: noop,
      onSend: noop,
      onCancel: noop,
      photoStep: createElement("p", null, "paso de la foto"),
    }),
  );
}

describe("\"Llamar a despacho\"", () => {
  it("is a tel: link to the E.164 number, labelled as approved, without showing the number", () => {
    const html = link(syntheticNumber);
    expect(html).toMatch(/^<a class="[^"]*dispatchCall[^"]*" href="tel:\+522000000001">Llamar a despacho<\/a>$/);
    expect(text(html)).toBe("Llamar a despacho");
  });

  it("is not rendered without a number, or with one of another form", () => {
    expect(link(null)).toBe("");
    expect(link("+52 2000000001")).toBe("");
    expect(link("2000000001")).toBe("");
    expect(link("+12025550100")).toBe("");
  });

  it("is a 44 px target", () => {
    const rule = css.slice(css.indexOf(".dispatchCall {"), css.indexOf("}", css.indexOf(".dispatchCall {")));
    expect(rule).toContain("min-width: 44px;");
    expect(rule).toContain("min-height: 44px;");
    expect(rule).toContain("display: inline-flex;");
    expect(css).toContain(".dispatchCall:focus-visible");
  });
});

describe("\"Llamar a despacho\" next to the recorded-cash texts", () => {
  it("goes after the stop's recorded-cash text when the number is known, which stays as it is", () => {
    const html = recordedCashSection(syntheticNumber);
    expect(text(html)).toBe(`${failedDeliveryCopy.codRecorded} Llamar a despacho`);
    expect(html.indexOf(failedDeliveryCopy.codRecorded)).toBeLessThan(html.indexOf('href="tel:+522000000001"'));
  });

  it("is not there without a number: today's text alone", () => {
    for (const html of [recordedCashSection(null), recordedCashSection()]) {
      expect(text(html)).toBe(failedDeliveryCopy.codRecorded);
      expect(html).not.toContain("tel:");
      expect(html).not.toContain("<a");
    }
  });

  it("goes after the form's refusal when the cash was recorded meanwhile, and only there", () => {
    expect(driverCashRecordedRefusalMessage).toBe(
      "El cobro ya está registrado. Llama a despacho: ellos deciden y coordinan la devolución del cobro.",
    );
    expect(driverMessageCallsDispatch(driverCashRecordedRefusalMessage)).toBe(true);
    const refused = form(driverCashRecordedRefusalMessage, syntheticNumber);
    expect(refused).toContain('href="tel:+522000000001"');
    expect(refused.indexOf(driverCashRecordedRefusalMessage)).toBeLessThan(refused.indexOf("tel:"));

    expect(form(driverCashRecordedRefusalMessage, null)).not.toContain("tel:");
    for (const problem of [
      null,
      "No se pudo guardar el reporte en el teléfono. Vuelve a intentarlo.",
      "Hay un cobro guardado en el teléfono. Si fue un error, descártalo antes de reportar.",
    ]) {
      expect(driverMessageCallsDispatch(problem)).toBe(false);
      expect(form(problem, syntheticNumber)).not.toContain("tel:");
    }
  });

  it("is placed only next to the texts that ask to call dispatch, and receives the session's number", () => {
    const sources = {
      failedDelivery: read("src/driver/components/driver-failed-delivery.tsx"),
      experience: read("src/driver/components/driver-stops-experience.tsx"),
    };
    const uses = (source: string) => source.split("<DriverDispatchCall ").length - 1;
    // The stop's recorded-cash text and the form's refusal.
    expect(uses(sources.failedDelivery)).toBe(2);
    expect(sources.failedDelivery).toMatch(
      /\{failedDeliveryCopy\.codRecorded\}<\/p>\s*<DriverDispatchCall phoneE164=\{dispatchPhone\} \/>/,
    );
    expect(sources.failedDelivery).toMatch(
      /\{driverMessageCallsDispatch\(problem\) \? \(\s*<DriverDispatchCall phoneE164=\{dispatchPhone\} \/>/,
    );
    // The held report's notice (driverFailedReportAttention.callDispatch) and the sync summary's message.
    expect(uses(sources.experience)).toBe(2);
    expect(sources.experience).toMatch(
      /\{reportAttention\?\.callDispatch \? \(\s*<DriverDispatchCall phoneE164=\{dispatchPhone\} \/>/,
    );
    expect(sources.experience).toMatch(
      /\{driverMessageCallsDispatch\(message\) \? \(\s*<DriverDispatchCall phoneE164=\{dispatchPhone\} \/>/,
    );
    expect(sources.experience).toContain("dispatchPhone={dispatchContactState.phoneE164}");
    // The component that builds the tel: link is the only place that does; the stops screen never writes one.
    expect(sources.experience).not.toContain("tel:");
    expect(sources.failedDelivery).not.toContain("tel:");
  });

  it("is recorded in AI-07 with the same texts the app shows", () => {
    const aiSeven = read("../../docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml");
    const screen = aiSeven.slice(aiSeven.indexOf("\n  driver_stop:\n"), aiSeven.indexOf("\n  driver_account:\n"));
    const record = screen
      .slice(screen.indexOf("    dispatch_contact:\n"), screen.indexOf("    recipient_call:"))
      .replace(/\s+/g, " ");
    for (const expected of [
      "decision: DRV-DISPATCH-CONTACT-2026-10-12",
      "GET /driver/me/dispatch-contact (getMyDispatchContact, DriverDispatchContact)",
      "label: Llamar a despacho",
      "at least 44 x 44 px",
      failedDeliveryCopy.codRecorded,
      driverCashRecordedRefusalMessage,
      driverCashRecordedReportMessage,
      "si no, llama a despacho.",
      "the link is never placed anywhere else",
      "store dispatch_contacts",
    ]) {
      expect(record).toContain(expected);
    }
    const cache = screen.slice(screen.indexOf("    offline_cache:\n"), screen.indexOf("    privacy:\n"));
    expect(cache).toContain("GET /driver/me/dispatch-contact");
  });

  it("is asked for once per driver session and stops with it, like the roles", () => {
    const experience = read("src/driver/components/driver-stops-experience.tsx");
    expect(experience).toContain("new DriverDispatchContactController({");
    expect(experience).toContain("api: createDriverDispatchContactApi({ baseUrl, session }),");
    expect(experience).toContain("cache: new IndexedDbDriverDispatchContactCache(),");
    expect(experience).toContain("void dispatchContactController.start();");
    expect(experience).toContain("dispatchContactController.dispose();");
    expect(experience).toContain("dispatchContactControllerRef.current?.dispose();");
  });
});
