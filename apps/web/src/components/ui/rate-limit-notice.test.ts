import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { RateLimitWaits } from "../../lib/rate-limit-wait";
import { Feedback } from "./feedback";
import { RateLimitNotice } from "./rate-limit-notice";

/** SEC-004-HEAVY-ENDPOINT-MAPPING-2026-10-12, owner answer Q6 "Mensaje común con espera (Recommended)". */
describe("rate limit notice", () => {
  it("shows the owner's text with the seconds left, as a timer, while the wait lasts", () => {
    let now = 0;
    const waits = new RateLimitWaits(() => now);
    waits.record("csv_import", 45);
    const html = () => renderToStaticMarkup(createElement(RateLimitNotice, { operations: ["csv_import"], waits }));
    expect(html()).toBe(
      '<p class="notice noticeWarn" role="timer">Demasiadas solicitudes. Espera 45 segundos e intenta de nuevo.</p>',
    );
    now += 44_500;
    expect(html()).toContain("Espera 1 segundo e intenta de nuevo.");
    now += 500;
    expect(html()).toBe("");
  });

  it("shows nothing for an operation that is not waiting", () => {
    const waits = new RateLimitWaits(() => 0);
    waits.record("quote", 30);
    expect(renderToStaticMarkup(createElement(RateLimitNotice, { operations: ["order_list"], waits }))).toBe("");
  });

  it("is part of the screens' common feedback only when a screen names its operations", () => {
    const plain = renderToStaticMarkup(createElement(Feedback, { errors: [], message: null, stepUpHref: null }));
    expect(plain).toBe("");
  });
});
