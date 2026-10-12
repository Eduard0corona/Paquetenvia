import { readFileSync } from "node:fs";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { driverFailedDeliveryReasonsFor } from "../contracts/failed-delivery";
import type { DriverFailedDeliveryAvailability } from "../offline/failed-delivery-policy";
import { collectionOperation, orderId } from "../offline/failed-delivery.fixtures";
import {
  FailedDeliveryForm,
  FailedDeliverySection,
  OtherReasonChoice,
  chosenFailedDeliveryReason,
  discardOperationConfirmation,
  failedDeliveryCopy,
  type FailedDeliveryPrimaryChoice,
} from "./driver-failed-delivery";

/**
 * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: what the delivery stop renders
 * for "No se pudo entregar" in each state, and the form itself.
 */
const read = (path: string) => readFileSync(path, "utf8");
const component = read("src/driver/components/driver-failed-delivery.tsx");
const experience = read("src/driver/components/driver-stops-experience.tsx");
const css = read("src/driver/components/driver-stops.module.css");
const text = (html: string) => html.replace(/<[^>]+>/g, " ").replace(/\s+/g, " ").trim();
const noop = () => undefined;

function section(availability: DriverFailedDeliveryAvailability): string {
  return renderToStaticMarkup(
    createElement(FailedDeliverySection, {
      availability,
      orderId,
      disabled: false,
      onReport: () => Promise.resolve({ stored: true as const }),
      onDiscardCollection: noop,
    }),
  );
}

function form(props: {
  readonly cash?: boolean;
  readonly primary?: FailedDeliveryPrimaryChoice;
  readonly other?: Parameters<typeof chosenFailedDeliveryReason>[1];
  readonly photoChosen?: boolean;
  readonly disabled?: boolean;
  readonly problem?: string | null;
}): string {
  return renderToStaticMarkup(
    createElement(FailedDeliveryForm, {
      orderId,
      reasons: driverFailedDeliveryReasonsFor(props.cash ?? false),
      primary: props.primary ?? null,
      other: props.other ?? null,
      photoChosen: props.photoChosen ?? false,
      disabled: props.disabled ?? false,
      problem: props.problem ?? null,
      onChoosePrimary: noop,
      onChooseOther: noop,
      onSend: noop,
      onCancel: noop,
      photoStep: createElement("p", null, "paso de la foto"),
    }),
  );
}

/** The opening tag of the button with this text. */
function button(html: string, label: string): string {
  const end = html.indexOf(`>${label}</button>`);
  if (end < 0) throw new Error(`No button "${label}".`);
  return html.slice(html.lastIndexOf("<button", end), end + 1);
}

describe("\"No se pudo entregar\" on the stop", () => {
  it("offers the button, closed, at the door", () => {
    const html = section({ kind: "offered", paymentReason: true });
    expect(text(html)).toBe("No se pudo entregar");
    expect(button(html, "No se pudo entregar")).not.toContain("disabled");
    expect(html).not.toContain("<input");
  });

  it("renders nothing when hidden, and leaves the confirmed attempt to the stop's status", () => {
    expect(section({ kind: "hidden" })).toBe("");
    expect(section({ kind: "dispatch-decides" })).toBe("");
    expect(section({ kind: "attention", discardable: null })).toBe("");
  });

  it("says a queued report will be sent when there is a connection", () => {
    expect(text(section({ kind: "queued" }))).toBe(
      "Reporte guardado en el teléfono. Se enviará cuando haya conexión.",
    );
    expect(section({ kind: "queued" })).toContain('role="status"');
  });

  it("asks to discard a saved collection first, with the existing confirmation", () => {
    const html = section({ kind: "cod-saved", discardable: collectionOperation() });
    expect(text(html)).toContain(
      "Hay un cobro guardado en el teléfono. Si fue un error, descártalo para poder reportar que no se pudo entregar.",
    );
    expect(button(html, "Descartar cobro guardado")).not.toContain("disabled");
    expect(text(section({ kind: "cod-saved", discardable: null }))).not.toContain(
      "Descartar cobro guardado",
    );
    expect(text(section({ kind: "attention", discardable: collectionOperation() }))).toContain(
      "Descartar cobro guardado",
    );
    expect(discardOperationConfirmation).toEqual({
      title: "Confirma la acción",
      description: "Se eliminarán esta operación y su evidencia local del dispositivo.",
      confirmLabel: "Confirmar",
    });
    // The discard runs only from the confirmation, through the stop's existing discard.
    const discard = component.indexOf("onDiscard(collectionId)");
    expect(discard).toBeGreaterThan(0);
    expect(component.slice(discard - 200, discard)).toContain("onConfirm:");
    const wiring = experience.slice(experience.indexOf("onDiscardCollection={(operationId) =>"));
    expect(wiring).toMatch(
      /^onDiscardCollection=\{\(operationId\) =>\s*onDiscard\(\s*operationId,\s*toOperationalStatus\(stop\.confirmedStatus\),\s*stop\.confirmedVersion,/,
    );
  });

  it("asks to call dispatch when the cash is recorded, without a phone number", () => {
    const html = section({ kind: "cod-recorded" });
    expect(text(html)).toBe(
      "El cobro ya está registrado. Si no puedes entregar, llama a despacho: ellos deciden y coordinan la devolución del cobro.",
    );
    expect(html).not.toContain("tel:");
    expect(html).not.toMatch(/\d{7,}/);
  });

  it("tells a person who is also dispatch to register the attempt from the order in Despacho, with no form", () => {
    const html = section({ kind: "dispatch-role" });
    expect(text(html)).toBe(
      "También eres despacho en esta organización, así que el intento fallido no se reporta desde el teléfono: " +
        "regístralo desde la orden en Despacho, donde eliges el motivo y el siguiente paso.",
    );
    expect(html).not.toContain("<button");
    expect(html).not.toContain("<input");
    // ORG-MY-ROLES-2026-10-12: the role comes from getMyOrganizationRoles (DriverRolesController), and the
    // controller that stores the report checks the same roles.
    expect(experience).toContain("reportRole={driverReportRoleOf(rolesState)}");
    expect(experience).toContain("reportRole: () => driverReportRoleOf(rolesController.current),");
    expect(experience).toContain("void rolesController.start();");
  });

  it("says the role could not be confirmed, with no form and never asking to sign out, when no answer is known", () => {
    const html = section({ kind: "role-unknown" });
    expect(text(html)).toBe("No pudimos confirmar tu rol. Vuelve a abrir la app con conexión para reportar.");
    expect(text(html)).not.toMatch(/sesi[oó]n/i);
    expect(html).not.toContain("<button");
    expect(html).not.toContain("<input");
  });

  it("shows nothing about the role while the roles are still loading (the report is just not offered yet)", () => {
    // driverFailedDeliveryAvailability answers `hidden` for the `loading` report role.
    expect(section({ kind: "hidden" })).toBe("");
    // A new session starts as loading, never as unknown.
    expect(experience).toContain("useState<DriverOrganizationRolesState>(\n    loadingRolesState,\n  )");
    expect(experience).toMatch(/const loadingRolesState: DriverOrganizationRolesState = Object\.freeze\(\{\s+roles: null,\s+loading: true,/);
  });
});

describe("¿Qué pasó?", () => {
  it("lists the four reasons of a stop with cash and \"Otro…\", with the design's subtitle", () => {
    const html = form({ cash: true });
    const visible = text(html);
    expect(visible).toContain("¿Qué pasó?");
    expect(visible).toContain("El despacho decidirá si se reprograma o regresa");
    for (const label of ["No había nadie", "Dirección incorrecta", "Rechazó el paquete", "No pagó el cobro", "Otro…"]) {
      expect(visible).toContain(label);
    }
    expect(html.match(/type="radio"/g)).toHaveLength(5);
    for (const hidden of ["Acceso restringido", "Paquete dañado", "Riesgo de seguridad"]) {
      expect(visible).not.toContain(hidden);
    }
  });

  it("leaves \"No pagó el cobro\" out where the stop collects no cash", () => {
    const html = form({ cash: false });
    expect(text(html)).not.toContain("No pagó el cobro");
    expect(html.match(/type="radio"/g)).toHaveLength(4);
  });

  it("reveals the three other reasons under \"Otro…\"", () => {
    const html = form({ primary: OtherReasonChoice });
    for (const label of ["Otro motivo", "Acceso restringido", "Paquete dañado", "Riesgo de seguridad"]) {
      expect(text(html)).toContain(label);
    }
    expect(html.match(/type="radio"/g)).toHaveLength(7);
    expect(html).toContain('aria-controls="failed-delivery-other-');
  });

  it("keeps \"Enviar reporte\" disabled until a reason and the photo are chosen", () => {
    expect(button(form({}), "Enviar reporte")).toContain("disabled");
    expect(button(form({ primary: "RECIPIENT_ABSENT" }), "Enviar reporte")).toContain("disabled");
    expect(button(form({ photoChosen: true }), "Enviar reporte")).toContain("disabled");
    expect(button(form({ primary: OtherReasonChoice, photoChosen: true }), "Enviar reporte")).toContain(
      "disabled",
    );
    expect(button(form({ primary: "RECIPIENT_ABSENT", photoChosen: true }), "Enviar reporte")).not.toContain(
      "disabled",
    );
    expect(
      button(form({ primary: OtherReasonChoice, other: "PACKAGE_DAMAGED", photoChosen: true }), "Enviar reporte"),
    ).not.toContain("disabled");
    expect(button(form({ primary: "RECIPIENT_ABSENT", photoChosen: true, disabled: true }), "Enviar reporte")).toContain(
      "disabled",
    );
    expect(text(form({}))).toContain(failedDeliveryCopy.sendHint);
    expect(chosenFailedDeliveryReason(OtherReasonChoice, null)).toBeNull();
    expect(chosenFailedDeliveryReason(OtherReasonChoice, "SECURITY_RISK")).toBe("SECURITY_RISK");
    expect(chosenFailedDeliveryReason("ADDRESS_NOT_FOUND", "SECURITY_RISK")).toBe("ADDRESS_NOT_FOUND");
  });

  it("has no free-text field and shows a problem as an alert", () => {
    const html = form({ cash: true, primary: OtherReasonChoice, problem: failedDeliveryCopy.notSaved });
    expect(html).not.toContain("<textarea");
    expect(html).not.toMatch(/type="(text|search|email|tel|number)"/);
    expect(component).not.toContain("<textarea");
    expect(component).not.toMatch(/type="(text|search|email|tel|number)"/);
    expect(html).toContain('role="alert"');
    expect(text(html)).toContain("No se pudo guardar el reporte en el teléfono. Vuelve a intentarlo.");
  });
});

describe("Foto del lugar (obligatoria)", () => {
  it("previews exactly one JPEG or PNG like the delivery photo and revokes the object URL", () => {
    expect(component).toContain("new DriverProofPreview()");
    expect(component).toContain("useEffect(() => () => preview.release(), [preview]);");
    expect(component).toContain("Vista previa de la foto");
    expect(component).toContain(">\n              Repetir\n            </button>");
    expect(component).toContain("Usar esta foto");
    expect(component).toContain('accept="image/jpeg,image/png"');
    expect(component).toContain('capture="environment"');
    expect(component).not.toContain("multiple");
    expect(failedDeliveryCopy.photo).toBe("Foto del lugar (obligatoria)");
    // Nothing is stored before "Enviar reporte": the photo only reaches the queue through onReport.
    expect(component).not.toContain("indexedDB");
    expect(component).not.toContain("localStorage");
    expect(component).not.toMatch(/console\./);
  });

  it("sends with \"Enviar reporte\" directly, like \"Usar esta foto\" queues the delivery (no ConfirmDialog)", () => {
    const send = component.slice(component.indexOf("const send = async () => {"), component.indexOf("if (!open) {"));
    expect(send).toContain("await onReport(reason, photo)");
    expect(send).not.toContain("confirm(");
  });

  it("shows why a report was refused instead of \"Vuelve a intentarlo\" (MINOR 9)", () => {
    const send = component.slice(component.indexOf("const send = async () => {"), component.indexOf("if (!open) {"));
    expect(send).toContain("else setProblem(result.message);");
    // "Vuelve a intentarlo" only when the call itself failed.
    expect(send).toContain("result = { stored: false, message: failedDeliveryCopy.notSaved };");
    const refused = "El cobro ya está registrado. Llama a despacho: ellos deciden y coordinan la devolución del cobro.";
    const html = form({ primary: "RECIPIENT_ABSENT", photoChosen: true, problem: refused });
    expect(text(html)).toContain(refused);
    expect(text(html)).not.toContain("Vuelve a intentarlo");
  });
});

describe("44 px targets", () => {
  it("styles every reason, button and the file input at 44 px or more", () => {
    for (const rule of [".reasonOption {", ".failedDelivery button {", '.failedDelivery input[type="file"] {']) {
      const start = css.indexOf(rule);
      expect(start, rule).toBeGreaterThan(0);
      expect(css.slice(start, css.indexOf("}", start))).toContain("min-height: 44px;");
    }
  });
});

describe("AI-07 record", () => {
  it("declares the flow, its reasons, the photo, the API sequence, the queue, what follows and the cash rule", () => {
    const aiSeven = read("../../docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml");
    const route = aiSeven.slice(
      aiSeven.indexOf("\n  /driver/stops/:id:\n"),
      aiSeven.indexOf("\n  /track/:token:\n") + 1,
    );
    expect(route).toContain("    - UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12\n");
    expect(route).toContain("    - fail_attempt\n");
    const screen = aiSeven.slice(
      aiSeven.indexOf("\n  driver_stop:\n"),
      aiSeven.indexOf("\n  driver_account:\n"),
    );
    const flow = screen.slice(screen.indexOf("    failed_delivery:\n"), screen.indexOf("    recipient_call:"));
    const plain = flow.replace(/\s+/g, " ");
    for (const expected of [
      "decision: UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12",
      "label: No se pudo entregar",
      '"¿Qué pasó?" with the subtitle "El despacho decidirá si se reprograma o regresa"',
      'RECIPIENT_ABSENT "No había nadie"',
      'PAYMENT_UNAVAILABLE "No pagó el cobro", only on a stop with cash on delivery',
      '"Solo si hay cobro (Recommended)"',
      '"Otro…" reveals ACCESS_RESTRICTED "Acceso restringido", PACKAGE_DAMAGED "Paquete dañado" and SECURITY_RISK "Riesgo de seguridad"',
      "free_text: none",
      '"Foto del lugar (obligatoria)": exactly one JPEG or PNG',
      '"Enviar reporte" stays disabled until a reason and the photo are chosen',
      "proof_type DELIVERY_PHOTO",
      "never severity, description or next_action",
      "metadata.incident_id",
      "FAILED_DELIVERY, operation schema 3, IndexedDB version 4",
      '"Reporte guardado en el teléfono. Se enviará cuando haya conexión."',
      '"Intento fallido · El despacho decide"',
      '"No, llama a despacho (Recommended)"',
      // DRV-DISPATCH-CONTACT-2026-10-12: the text itself never carries a number; "Llamar a despacho" goes next to it.
      "the text never carries a number",
      '"Llamar a despacho" goes next to it: dispatch_contact, DRV-DISPATCH-CONTACT-2026-10-12',
      "a report made by dispatch is not blocked",
      // Review of the PR: the attempt it describes, REST confirmation, the
      // owner answers "Registrar desde Despacho" and "Después, con ADR".
      "until its incident exists, a report is sent only while REST shows its stop DELIVERING at exactly the version it was built on",
      "A failed attempt dispatch registered on its own never confirms a report that was not sent",
      "a report is never rebuilt for another version",
      '"Registrar desde Despacho (Recommended)"',
      "DISPATCHER or PLATFORM_ADMIN",
      "409 INVALID_REQUEST of openIncident points to registering the attempt in Despacho",
      'never says "not sent" nor invites reporting it again',
      "the report and the cash can then both exist on the server",
      '"Después, con ADR (Recommended)"',
      "a pending server follow-up",
      // Second and third reviews: the roles come from their own API read
      // (ORG-MY-ROLES-2026-10-12), saved per partition, and unknown roles fail closed.
      "The phone knows it from GET /me/organization-roles (getMyOrganizationRoles",
      '"Agregar los roles al API (Recommended)"',
      '"Endpoint nuevo de roles (Recommended)"',
      "GET /me/organization-contexts answers one context and one role per organization and stays unchanged",
      "No photo is uploaded and nothing is stored",
      "role_unknown: 'ORG-MY-ROLES-2026-10-12",
      "the API is earlier and answers 404",
      "it fails closed",
      '"No pudimos confirmar tu rol. Vuelve a abrir la app con conexión para reportar."',
      "never asking to sign out, which would wipe what the phone keeps offline",
      "A failed read keeps the answer already saved and is made again when the connection returns or the app is shown again, at most 5 times per session start and never after 401 or 403",
      "role_loading: 'ORG-MY-ROLES-2026-10-12",
      "the stop says nothing about the role: loading is not an error",
      "no deploy order is needed between the web and the API",
      "never holds back a new report of the same stop",
    ]) {
      expect(plain, expected).toContain(expected);
    }
    // ORG-MY-ROLES-2026-10-12: the saved roles answer is part of what the phone keeps, and leaves with the snapshots.
    const kept = screen
      .slice(screen.indexOf("    offline_cache:\n"), screen.indexOf("    privacy:\n"))
      .replace(/\s+/g, " ");
    expect(kept).toContain(
      "the last successful answer of GET /me/organization-roles for the session''s partition: the role codes only",
    );
    expect(kept).toContain("with every snapshot on logout");
    expect(screen.replace(/\s+/g, " ")).toContain(
      "the saved roles answer (ORG-MY-ROLES-2026-10-12) is wiped with the snapshots, on logout and on 401 or 403",
    );
    expect(plain).not.toContain("ORG-CONTEXT-ROLES");
    expect(plain).not.toContain("the roles array of the selected organization context");
    expect(plain).not.toContain("the report and the cash are never both sent");
    const cash = screen.slice(screen.indexOf("    cod_collection:\n"), screen.indexOf("    failed_delivery:\n"));
    expect(cash.replace(/\s+/g, " ")).toContain(
      '"No pagó el cobro" is a reason of "No se pudo entregar" (failed_delivery,',
    );
    expect(cash).toContain("      door_only:");
    expect(cash).not.toContain("outside this change");
  });
});

describe("stop wiring", () => {
  it("puts the report last on the delivery screen and sends DriverStop.cod with it", () => {
    const action = experience.indexOf("<StopAction");
    const call = experience.indexOf("<RecipientCall");
    const failed = experience.indexOf("<DriverFailedDelivery\n");
    const conflict = experience.indexOf("<ConflictResolution\n          stop={stop}");
    expect(failed).toBeGreaterThan(call);
    expect(failed).toBeGreaterThan(action);
    expect(conflict).toBeGreaterThan(failed);
    expect(experience).toContain("operationsControllerRef.current.enqueueFailedDelivery(");
    expect(experience).toContain("cod: stop.cod,");
  });

  it("never offers a rebuild for a report, retries it only where it still applies, and explains it (MAJOR 3 and 4)", () => {
    const conflict = experience.slice(experience.indexOf("function ConflictResolution("));
    expect(conflict).toContain("driverOperationDefinitions[attention.kind].transition && !report;");
    expect(conflict).toMatch(
      /cashHold !== "recorded" &&\s*confirmedOperationalStatus !== null &&\s*driverOperationApplies\(/,
    );
    // driverFailedReportAttention is driverFailedReportAttentionMessage plus whether it asks to call dispatch
    // (DRV-DISPATCH-CONTACT-2026-10-12).
    expect(conflict).toContain("driverFailedReportAttention(attention, stop, operations)");
    expect(conflict).toContain("reportAttention.message");
    expect(conflict).toContain("{offerRebuild ? (");
  });

  it("shows the confirmed status as the server has it", () => {
    expect(experience).toContain("{driverConfirmedStatusLabel(stop.confirmedStatus)}");
    expect(experience).not.toContain("{driverStopStatusLabel(stop.confirmedStatus)}\n              </span>");
  });
});
