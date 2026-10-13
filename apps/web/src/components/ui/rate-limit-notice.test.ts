import { readFileSync } from "node:fs";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RateLimitWaits } from "../../lib/rate-limit-wait";
import {
  RateLimitAnnouncer,
  rateLimitAnnouncement,
  RateLimitNotice,
  RateLimitNoticeView,
} from "./rate-limit-notice";

const html = (element: Parameters<typeof renderToStaticMarkup>[0]) => renderToStaticMarkup(element);

describe("SEC-004 common message (owner answers Q6, Q12)", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-13T12:00:00Z"));
  });
  afterEach(() => vi.useRealTimers());

  it("shows the countdown as a timer, which is never announced by itself", () => {
    expect(html(createElement(RateLimitNoticeView, { secondsLeft: 42 }))).toBe(
      '<p class="notice noticeWarn" role="timer">Demasiadas solicitudes. Espera 42 segundos e intenta de nuevo.</p>',
    );
    expect(html(createElement(RateLimitNoticeView, { secondsLeft: 1, className: "appSearchMessage", id: "w" }))).toBe(
      '<p class="appSearchMessage" id="w" role="timer">Demasiadas solicitudes. Espera 1 segundo e intenta de nuevo.</p>',
    );
    // At 0 the message disappears.
    expect(html(createElement(RateLimitNoticeView, { secondsLeft: 0 }))).toBe("");
    expect(html(createElement(RateLimitNotice, { operations: ["quote"], waits: new RateLimitWaits() }))).toBe("");
  });

  it("announces each wait once, politely, with the seconds the server asked for, and empties when it ends", () => {
    const waits = new RateLimitWaits();
    expect(rateLimitAnnouncement(waits)).toBe("");
    waits.record("order_list", 60);
    expect(rateLimitAnnouncement(waits)).toBe("Demasiadas solicitudes. Espera 60 segundos e intenta de nuevo.");
    // The text does not tick with the countdown.
    vi.advanceTimersByTime(30_000);
    expect(rateLimitAnnouncement(waits)).toBe("Demasiadas solicitudes. Espera 60 segundos e intenta de nuevo.");
    vi.advanceTimersByTime(30_000);
    expect(rateLimitAnnouncement(waits)).toBe("");
    waits.record("quote", 1);
    expect(rateLimitAnnouncement(waits)).toBe("Demasiadas solicitudes. Espera 1 segundo e intenta de nuevo.");
    // Its live region is always there (empty until a wait), visually hidden.
    expect(html(createElement(RateLimitAnnouncer, { waits: new RateLimitWaits() }))).toBe(
      '<p class="srOnly" role="status" aria-live="polite"></p>',
    );
  });

  it("is announced once per tab by the shell, and no countdown is a live region", () => {
    const notice = readFileSync("src/components/ui/rate-limit-notice.tsx", "utf8");
    const view = notice.slice(notice.indexOf("export function RateLimitNoticeView"), notice.indexOf("export function RateLimitNotice("));
    expect(view).toContain('role="timer"');
    expect(view).not.toMatch(/aria-live|role="status"|role="alert"/);
    const shell = readFileSync("src/components/app-shell/app-shell.tsx", "utf8");
    expect(shell.match(/<RateLimitAnnouncer \/>/g)).toHaveLength(1);
    // Every limited panel screen renders inside the shell.
    for (const layout of ["src/app/ops/layout.tsx", "src/app/finance/layout.tsx"]) {
      expect(readFileSync(layout, "utf8")).toContain("<AppShell>{children}</AppShell>");
    }
  });
});
