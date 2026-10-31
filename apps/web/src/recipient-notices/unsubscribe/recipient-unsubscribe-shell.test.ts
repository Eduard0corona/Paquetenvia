import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  recipientUnsubscribeViewPhase,
  RecipientUnsubscribeShell,
  RecipientUnsubscribeView,
  type RecipientUnsubscribePhase,
} from "./recipient-unsubscribe-shell";
import { recipientUnsubscribeTexts } from "./unsubscribe-texts";

const heading = '<h1 class="trackingHeading">Seguimiento de envío</h1>';
const button = (disabled: boolean) =>
  `<button type="button"${disabled ? ' disabled=""' : ""}>Dejar de recibir correos</button>`;

function view(phase: RecipientUnsubscribePhase, secondsLeft = 0): string {
  return renderToStaticMarkup(createElement(RecipientUnsubscribeView, { phase, secondsLeft, onUnsubscribe: () => undefined }));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the /avisos/baja/:token page (AI-07, O9 texts)", () => {
  it("opens with the neutral heading and the one button, and sends nothing by itself", () => {
    const markup = view("ready");
    expect(markup).toContain(heading);
    expect(markup).toContain(button(false));
    expect(markup).not.toContain('role="status"');
  });

  it("shows exactly one approved message for each outcome", () => {
    expect(view("recorded")).toContain(`<p role="status">${recipientUnsubscribeTexts.success}</p>`);
    expect(view("recorded")).not.toContain("<button");
    expect(view("invalid")).toContain(`<p role="status">${recipientUnsubscribeTexts.invalidLink}</p>`);
    expect(view("invalid")).not.toContain("<button");
    expect(view("failed")).toContain(`<p role="status">${recipientUnsubscribeTexts.failure}</p>`);
    expect(view("failed")).toContain(button(false));
    expect(view("sending")).toContain(button(true));
    expect(view("sending")).toContain('aria-busy="true"');
  });

  it("counts the wait down with the common message and keeps the button disabled until it ends", () => {
    expect(view("wait", 42)).toContain(
      '<p class="notice noticeWarn" role="timer">Demasiadas solicitudes. Espera 42 segundos e intenta de nuevo.</p>',
    );
    expect(view("wait", 42)).toContain(button(true));
    expect(view("wait", 1)).toContain("Demasiadas solicitudes. Espera 1 segundo e intenta de nuevo.");
    expect(view("wait", 0)).not.toContain("Demasiadas solicitudes");
    expect(view("wait", 0)).toContain(button(false));
  });

  it("never shows anything but the heading, the button and the approved messages", () => {
    const allowed = new Set<string>([
      recipientUnsubscribeTexts.heading,
      recipientUnsubscribeTexts.button,
      recipientUnsubscribeTexts.success,
      recipientUnsubscribeTexts.invalidLink,
      recipientUnsubscribeTexts.failure,
      "Demasiadas solicitudes. Espera 5 segundos e intenta de nuevo.",
    ]);
    for (const phase of ["ready", "sending", "recorded", "invalid", "wait", "failed"] as const) {
      const texts = [...view(phase, 5).matchAll(/>([^<>]+)</g)].map((match) => match[1]);
      for (const text of texts) expect(allowed.has(text), `${phase}: ${text}`).toBe(true);
    }
  });

  it("reads the token only in the browser: the server render is busy and the link invalid without a token", () => {
    // The server render never sees the address, so no token reaches it (the /avisos/baja/:token rewrite).
    vi.stubGlobal("window", { location: { pathname: `/avisos/baja/${"C".repeat(88)}` } });
    const server = renderToStaticMarkup(createElement(RecipientUnsubscribeShell));
    expect(server).toContain(heading);
    expect(server).toContain(button(true));
    expect(server).not.toContain("C".repeat(88));

    expect(recipientUnsubscribeViewPhase(undefined, "ready")).toBe("sending");
    expect(recipientUnsubscribeViewPhase(null, "ready")).toBe("invalid");
    expect(recipientUnsubscribeViewPhase(null, "recorded")).toBe("invalid");
    for (const phase of ["ready", "sending", "recorded", "invalid", "wait", "failed"] as const) {
      expect(recipientUnsubscribeViewPhase("C".repeat(88), phase)).toBe(phase);
    }
  });
});
