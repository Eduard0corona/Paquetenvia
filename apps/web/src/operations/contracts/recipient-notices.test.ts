import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import {
  recipientNoticeAttempts,
  recipientNoticeChannelLabels,
  recipientNoticeChannels,
  recipientNoticeKinds,
  recipientNoticeLabels,
  recipientNoticeLastCodes,
  recipientNoticeOrderFacts,
  recipientNoticeOtherFailure,
  recipientNoticeReason,
  recipientNoticeReasons,
  recipientNoticeRowTitle,
  recipientNoticeStatusLabels,
  recipientNoticeStatuses,
  recipientNoticeStoppedLines,
  recipientNoticeTexts,
  stopRecipientNoticesConfirmation,
  type RecipientNotice,
} from "./recipient-notices";

const openApi = readFileSync(resolve(process.cwd(), "../../docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml"), "utf8");
const uiContracts = readFileSync(resolve(process.cwd(), "../../docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml"), "utf8");

function notice(overrides: Partial<RecipientNotice> = {}): RecipientNotice {
  return {
    notificationId: "0a1b2c3d-1111-4222-8333-944445555666",
    notice: "TRACKING_LINK",
    channel: "WHATSAPP",
    status: "PENDING",
    attempts: 0,
    lastCode: null,
    sentAt: null,
    deliveredAt: null,
    updatedAt: "2026-10-30T15:00:00.000Z",
    ...overrides,
  };
}

/** The enum values of one AI-05 schema property, in order. */
function publishedEnum(schema: string, property: string): string[] {
  const start = openApi.indexOf(`\n    ${schema}:\n`);
  expect(start, schema).toBeGreaterThan(0);
  const from = openApi.indexOf(`\n        ${property}:\n`, start);
  const enumAt = openApi.indexOf("enum:\n", from);
  const values: string[] = [];
  for (const line of openApi.slice(enumAt + "enum:\n".length).split("\n")) {
    const match = /^ {10}- (\S+)$/.exec(line);
    if (!match) break;
    if (match[1] !== "null") values.push(match[1]);
  }
  return values;
}

describe("recipient notice texts (NTF-RECIPIENT-NOTICE-PANEL-2026-10-30)", () => {
  it("pins every fixed text of the panel and its dialog exactly as the owner approved them (T1, T2, T4, T5)", () => {
    expect(recipientNoticeTexts).toEqual({
      title: "Avisos al destinatario",
      notActiveYet: "Los avisos al destinatario todavía no están activos.",
      quietHours: "Los avisos solo salen de 8:00 a 21:00, hora de Mazatlán",
      empty: "Esta orden todavía no tiene avisos.",
      whatsappAllowed: "WhatsApp: permitido (se marcó la casilla)",
      whatsappNotAllowed: "WhatsApp: no permitido (no se marcó la casilla)",
      emailOnFile: "Correo del destinatario: registrado",
      emailNotOnFile: "Correo del destinatario: no registrado",
      retrying: "Reintentando",
      stopButton: "Detener avisos",
      stoppedBoth: "Listo. Se detuvieron los avisos por WhatsApp y correo.",
      stoppedWhatsApp: "Listo. Se detuvieron los avisos por WhatsApp.",
      stoppedEmail: "Listo. Se detuvieron los avisos por correo.",
      stoppedPending: "Los avisos pendientes ya no se enviarán.",
      stopFailure: "No pudimos detener los avisos. Intenta de nuevo.",
      readFailure: "No fue posible leer los avisos.",
      confirmTitle: "¿Detener avisos?",
      confirmEffect:
        "El destinatario dejará de recibir avisos de todas las empresas. Durante el piloto no se puede deshacer.",
      confirmLabel: "Detener avisos",
      cancelLabel: "Cancelar",
    });
    expect(recipientNoticeLabels).toEqual({
      TRACKING_LINK: "Enlace de seguimiento",
      IN_TRANSIT: "Va en camino",
      DELIVERY_FAILED: "No se pudo entregar",
      DELIVERED: "Entregado",
    });
    expect(recipientNoticeChannelLabels).toEqual({ WHATSAPP: "WhatsApp", EMAIL: "Correo" });
    expect(recipientNoticeStatusLabels).toEqual({
      PENDING: "Pendiente",
      SENT: "Enviado",
      DELIVERED: "Recibido",
      FAILED: "No se pudo enviar",
      CANCELLED: "No se envió",
    });
    expect(recipientNoticeReasons).toEqual({
      RECIPIENT_OPTED_OUT: "El destinatario pidió no recibir avisos.",
      NOTICE_SUPERSEDED: "Ya no aplicaba cuando tocaba enviarlo.",
      NOTICE_STALE: "Pasó demasiado tiempo para enviarlo.",
      NOTICE_LIMIT_REACHED: "Se alcanzó el límite de avisos.",
      MESSAGING_CHANNEL_DISABLED: "Los avisos por este medio están apagados.",
      MESSAGING_RECIPIENT_INVALID: { WHATSAPP: "No se pudo usar el teléfono.", EMAIL: "No se pudo usar el correo." },
      MESSAGING_RECIPIENT_UNDELIVERABLE: {
        WHATSAPP: "No llegó: el número no recibe WhatsApp.",
        EMAIL: "No llegó: el correo rebotó.",
      },
      MESSAGING_POLICY_BLOCKED: "El servicio de mensajes lo bloqueó.",
      TRACKING_LINK_UNAVAILABLE: "No había enlace de seguimiento vigente.",
      AMBIGUOUS_TIMEOUT: "No se sabe si llegó; no se reintenta para no duplicarlo.",
      MAX_ATTEMPTS_EXHAUSTED: "Falló después de varios intentos.",
    });
    expect(recipientNoticeOtherFailure).toBe("Falla del servicio de mensajes.");
  });

  it("is the AI-07 texts block, sentence for sentence", () => {
    const block = uiContracts.slice(uiContracts.indexOf("\n    recipient_notices:\n      decision: NTF-RECIPIENT-NOTICES-2026-10-20"));
    expect(block).toContain("status: DECIDED, implemented (AI-08 NTF-007)");
    const panel = block.slice(0, block.indexOf("\n    rate_limit:"));
    const texts = [
      ...Object.values(recipientNoticeTexts),
      ...Object.values(recipientNoticeLabels),
      ...Object.values(recipientNoticeStatusLabels),
      recipientNoticeOtherFailure,
      ...Object.values(recipientNoticeReasons).flatMap((reason) =>
        typeof reason === "string" ? [reason] : Object.values(reason),
      ),
    ];
    for (const text of texts) expect(panel.replace(/\n\s+/g, " "), text).toContain(text);
  });

  it("names a notice row as «{aviso} · {WhatsApp|Correo} — {estado}» and counts attempts only past one", () => {
    expect(recipientNoticeRowTitle(notice())).toBe("Enlace de seguimiento · WhatsApp — Pendiente");
    expect(recipientNoticeRowTitle(notice({ notice: "DELIVERED", channel: "EMAIL", status: "DELIVERED" }))).toBe(
      "Entregado · Correo — Recibido",
    );
    expect(recipientNoticeRowTitle(notice({ notice: "IN_TRANSIT", status: "SENT" }))).toBe(
      "Va en camino · WhatsApp — Enviado",
    );
    expect(recipientNoticeRowTitle(notice({ notice: "DELIVERY_FAILED", status: "CANCELLED" }))).toBe(
      "No se pudo entregar · WhatsApp — No se envió",
    );
    expect(recipientNoticeAttempts(notice({ attempts: 0 }))).toBeNull();
    expect(recipientNoticeAttempts(notice({ attempts: 1 }))).toBeNull();
    expect(recipientNoticeAttempts(notice({ attempts: 3 }))).toBe("Intentos: 3");
  });

  it("explains why a notice did not go out, per channel, and «Reintentando» for a pending one after a failure", () => {
    const reason = (status: RecipientNotice["status"], lastCode: RecipientNotice["lastCode"], channel: RecipientNotice["channel"] = "WHATSAPP") =>
      recipientNoticeReason(notice({ status, lastCode, channel }));
    expect(reason("PENDING", null)).toBeNull();
    expect(reason("PENDING", "MESSAGING_PROVIDER_TIMEOUT")).toBe("Reintentando");
    expect(reason("SENT", "MESSAGING_ACCEPTED")).toBeNull();
    expect(reason("DELIVERED", "MESSAGING_ACCEPTED")).toBeNull();
    expect(reason("CANCELLED", "RECIPIENT_OPTED_OUT")).toBe("El destinatario pidió no recibir avisos.");
    expect(reason("CANCELLED", "NOTICE_SUPERSEDED")).toBe("Ya no aplicaba cuando tocaba enviarlo.");
    expect(reason("CANCELLED", "NOTICE_STALE")).toBe("Pasó demasiado tiempo para enviarlo.");
    expect(reason("CANCELLED", "NOTICE_LIMIT_REACHED")).toBe("Se alcanzó el límite de avisos.");
    expect(reason("FAILED", "MESSAGING_CHANNEL_DISABLED")).toBe("Los avisos por este medio están apagados.");
    expect(reason("FAILED", "MESSAGING_RECIPIENT_INVALID")).toBe("No se pudo usar el teléfono.");
    expect(reason("FAILED", "MESSAGING_RECIPIENT_INVALID", "EMAIL")).toBe("No se pudo usar el correo.");
    expect(reason("FAILED", "MESSAGING_RECIPIENT_UNDELIVERABLE")).toBe("No llegó: el número no recibe WhatsApp.");
    expect(reason("FAILED", "MESSAGING_RECIPIENT_UNDELIVERABLE", "EMAIL")).toBe("No llegó: el correo rebotó.");
    expect(reason("FAILED", "MESSAGING_POLICY_BLOCKED")).toBe("El servicio de mensajes lo bloqueó.");
    expect(reason("FAILED", "TRACKING_LINK_UNAVAILABLE")).toBe("No había enlace de seguimiento vigente.");
    expect(reason("FAILED", "AMBIGUOUS_TIMEOUT")).toBe("No se sabe si llegó; no se reintenta para no duplicarlo.");
    expect(reason("FAILED", "MAX_ATTEMPTS_EXHAUSTED")).toBe("Falló después de varios intentos.");
    // Every other code, and a failure without one, is the one generic sentence; a code never shows itself.
    for (const code of ["MESSAGING_PROVIDER_REJECTED", "MESSAGING_PROVIDER_TIMEOUT", "MESSAGING_SYNTHETIC_PERMANENT", null] as const)
      expect(reason("FAILED", code)).toBe("Falla del servicio de mensajes.");
    for (const code of recipientNoticeLastCodes)
      for (const status of recipientNoticeStatuses)
        for (const channel of recipientNoticeChannels)
          expect(recipientNoticeReason(notice({ status, lastCode: code, channel })) ?? "").not.toContain(code);
  });

  it("states the order's two facts and what a stop did, from the channels the server names", () => {
    expect(recipientNoticeOrderFacts({ items: [], whatsappConsentAttested: true, emailOnFile: true })).toEqual([
      "WhatsApp: permitido (se marcó la casilla)",
      "Correo del destinatario: registrado",
    ]);
    expect(recipientNoticeOrderFacts({ items: [], whatsappConsentAttested: false, emailOnFile: false })).toEqual([
      "WhatsApp: no permitido (no se marcó la casilla)",
      "Correo del destinatario: no registrado",
    ]);
    expect(recipientNoticeStoppedLines(["WHATSAPP", "EMAIL"])).toEqual([
      "Listo. Se detuvieron los avisos por WhatsApp y correo.",
      "Los avisos pendientes ya no se enviarán.",
    ]);
    expect(recipientNoticeStoppedLines(["WHATSAPP"])).toEqual([
      "Listo. Se detuvieron los avisos por WhatsApp.",
      "Los avisos pendientes ya no se enviarán.",
    ]);
    expect(recipientNoticeStoppedLines(["EMAIL"])[0]).toBe("Listo. Se detuvieron los avisos por correo.");
  });

  it("builds the T1 dialog with the order's guía (title, text and the two buttons)", () => {
    expect(stopRecipientNoticesConfirmation("ORD_0000000000000000000042")).toEqual({
      title: "¿Detener avisos?",
      description:
        "Orden ORD_0000000000000000000042. El destinatario dejará de recibir avisos de todas las empresas. Durante el piloto no se puede deshacer.",
      confirmLabel: "Detener avisos",
      cancelLabel: "Cancelar",
    });
  });

  it("closes the notice, channel, status and code sets exactly as AI-05 publishes them", () => {
    expect([...recipientNoticeKinds]).toEqual(publishedEnum("RecipientNotice", "notice"));
    expect([...recipientNoticeChannels]).toEqual(publishedEnum("RecipientNotice", "channel"));
    expect([...recipientNoticeStatuses]).toEqual(publishedEnum("RecipientNotice", "status"));
    expect([...recipientNoticeLastCodes]).toEqual(publishedEnum("RecipientNotice", "last_code"));
    expect(recipientNoticeLastCodes).toContain("MAX_ATTEMPTS_EXHAUSTED");
    // T2: AI-05 answers no scheduled time for a recipient notice (routes keep their own scheduled_for).
    const schema = openApi.slice(openApi.indexOf("\n    RecipientNotice:\n"), openApi.indexOf("\n    RecipientNoticeStopResult:\n"));
    expect(schema).toContain("notification_id");
    expect(schema).not.toContain("scheduled_for");
    const part = openApi.slice(openApi.indexOf("operation: listOrderRecipientNotices"), openApi.indexOf("operation: stopOrderRecipientNotices"));
    expect(part).not.toContain("scheduled_for");
  });
});
