import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import type { RecipientNotice } from "../contracts/recipient-notices";
import type { RecipientNoticesState } from "../state/recipient-notices-controller";
import { RecipientNoticesView } from "./operations-recipient-notices";

const source = (path: string) => readFileSync(resolve(process.cwd(), path), "utf8");

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

function render(state: RecipientNoticesState): string {
  return renderToStaticMarkup(createElement(RecipientNoticesView, { state, onStop: () => undefined }));
}

/** What a person reads: the markup without tags. */
function text(html: string): string {
  return html.replace(/<[^>]+>/g, "\n").replace(/&#x27;/g, "'").replace(/\n+/g, "\n");
}

describe("panel «Avisos al destinatario» (NTF-RECIPIENT-NOTICE-PANEL-2026-10-30)", () => {
  it("always shows the T4 line, the T2 line and the button, with the order's facts and the empty list", () => {
    const html = render({
      list: { kind: "loaded", list: { items: [], whatsappConsentAttested: false, emailOnFile: true } },
      stop: { kind: "idle" },
    });
    const visible = text(html);
    expect(visible).toContain("Avisos al destinatario");
    expect(visible).toContain("Los avisos al destinatario todavía no están activos.");
    expect(visible).toContain("Los avisos solo salen de 8:00 a 21:00, hora de Mazatlán");
    expect(visible).toContain("WhatsApp: no permitido (no se marcó la casilla)");
    expect(visible).toContain("Correo del destinatario: registrado");
    expect(visible).toContain("Esta orden todavía no tiene avisos.");
    expect(html).toMatch(/<button type="button" class="btn btnSecondary"[^>]*>Detener avisos<\/button>/);
    expect(html).not.toContain("disabled");
  });

  it("shows one row per notice and channel, its Mazatlán time, its attempts past one and why it did not go out", () => {
    const html = render({
      list: {
        kind: "loaded",
        list: {
          items: [
            notice({ status: "SENT", lastCode: "MESSAGING_ACCEPTED", attempts: 1 }),
            notice({
              notificationId: "1a1b2c3d-1111-4222-8333-944445555666",
              channel: "EMAIL",
              status: "FAILED",
              attempts: 3,
              lastCode: "MESSAGING_RECIPIENT_UNDELIVERABLE",
            }),
            notice({
              notificationId: "2a1b2c3d-1111-4222-8333-944445555666",
              notice: "IN_TRANSIT",
              status: "CANCELLED",
              lastCode: "RECIPIENT_OPTED_OUT",
            }),
            notice({
              notificationId: "3a1b2c3d-1111-4222-8333-944445555666",
              notice: "DELIVERED",
              status: "PENDING",
              attempts: 1,
              lastCode: "MESSAGING_PROVIDER_TIMEOUT",
            }),
          ],
          whatsappConsentAttested: true,
          emailOnFile: true,
        },
      },
      stop: { kind: "idle" },
    });
    const visible = text(html);
    expect(visible).toContain("Enlace de seguimiento · WhatsApp — Enviado");
    expect(visible).toContain("Enlace de seguimiento · Correo — No se pudo enviar");
    expect(visible).toContain("Intentos: 3");
    expect(visible).not.toContain("Intentos: 1");
    expect(visible).toContain("No llegó: el correo rebotó.");
    expect(visible).toContain("Va en camino · WhatsApp — No se envió");
    expect(visible).toContain("El destinatario pidió no recibir avisos.");
    expect(visible).toContain("Entregado · WhatsApp — Pendiente");
    expect(visible).toContain("Reintentando");
    // 15:00 UTC is 08:00 in Mazatlán (UTC-7, no daylight saving time since 2022).
    expect(html).toContain('dateTime="2026-10-30T15:00:00.000Z"');
    expect(visible).toContain("30 oct 2026, 08:00");
    // Never an identifier or an internal code.
    expect(html).not.toContain("0a1b2c3d-1111-4222-8333-944445555666");
    for (const code of ["MESSAGING_", "RECIPIENT_OPTED_OUT", "TRACKING_LINK", "WHATSAPP", "EMAIL"])
      expect(visible).not.toContain(code);
  });

  it("says when the list cannot be read and offers the step-up when only MFA is missing", () => {
    expect(text(render({ list: { kind: "failed", message: "No fue posible leer los avisos.", stepUpHref: null }, stop: { kind: "idle" } }))).toContain(
      "No fue posible leer los avisos.",
    );
    const mfa = render({
      list: {
        kind: "failed",
        message: "Esta acción requiere verificar tu identidad (MFA).",
        stepUpHref: "/login?mfa=required&return_url=%2Fops%2Forders%2Fx",
      },
      stop: { kind: "idle" },
    });
    expect(mfa).toContain("Verificar identidad");
    expect(mfa).toContain('href="/login?mfa=required&amp;return_url=%2Fops%2Forders%2Fx"');
    // The button is still offered (T4, T6 choice 13).
    expect(mfa).toContain(">Detener avisos</button>");
  });

  it("confirms a stop in memory, keeps the button and shows the failure text", () => {
    const stopped = text(render({ list: { kind: "loading" }, stop: { kind: "stopped", channels: ["WHATSAPP", "EMAIL"] } }));
    expect(stopped).toContain("Listo. Se detuvieron los avisos por WhatsApp y correo. Los avisos pendientes ya no se enviarán.");
    expect(stopped).toContain("Detener avisos");
    expect(text(render({ list: { kind: "loading" }, stop: { kind: "stopped", channels: ["WHATSAPP"] } }))).toContain(
      "Listo. Se detuvieron los avisos por WhatsApp. Los avisos pendientes ya no se enviarán.",
    );
    expect(
      text(render({ list: { kind: "loading" }, stop: { kind: "failed", message: "No pudimos detener los avisos. Intenta de nuevo.", stepUpHref: null } })),
    ).toContain("No pudimos detener los avisos. Intenta de nuevo.");
    expect(render({ list: { kind: "loading" }, stop: { kind: "busy" } })).toMatch(/<button[^>]*disabled=""[^>]*>Detener avisos<\/button>/);
  });

  it("is mounted on the order detail for the owner organization only, with the T1 dialog and the screen's reloads", () => {
    const panel = source("src/operations/components/operations-recipient-notices.tsx");
    expect(panel).toContain("canManageRecipientNotices(contexts, session.organizationId, ownerOrganizationId)");
    expect(panel).toContain("useConfirmDialog()");
    expect(panel).toContain("...stopRecipientNoticesConfirmation(publicId)");
    expect(panel).toContain("if (allowed && !pausedRef.current) void controllerRef.current?.load();");
    expect(panel).toContain("}, [allowed, revision]);");
    // Nothing of the panel is kept in browser storage.
    expect(panel).not.toMatch(/localStorage|sessionStorage|indexedDB/);
    const shell = source("src/operations/components/operations-order-detail-shell.tsx");
    expect(shell).toContain("<OperationsRecipientNotices");
    expect(shell).toContain("ownerOrganizationId={order.owner_org_id}");
    expect(shell).toContain("revision={state.noticesRevision}");
    expect(shell).toContain("onStopped={state.reloadNotices}");
    expect(shell).toContain("paused={waiting}");
  });
});
