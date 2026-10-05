import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { DateTime } from "./date-time";
import { DescriptionList } from "./description-list";
import { EmptyState } from "./empty-state";
import { Feedback, ScreenGate } from "./feedback";
import { Field, type FieldControlProps } from "./field";
import { Money } from "./money";
import { PageHeader } from "./page-header";

/** Renders a Field the way screens do, with its control as the render-prop child. */
function FieldProbe() {
  return Field({
    label: "ID de la orden",
    hint: "Pega el ID completo.",
    error: "Falta el ID.",
    children: (control: FieldControlProps) => createElement("input", { ...control, name: "order" }),
  });
}

const html = (element: Parameters<typeof renderToStaticMarkup>[0]) => renderToStaticMarkup(element);

describe("shared UI components", () => {
  it("formats integer cents through formatMxnCents with tabular figures", () => {
    expect(html(createElement(Money, { cents: 123_456 }))).toBe('<span class="tabular">$1,234.56 MXN</span>');
    expect(html(createElement(Money, { cents: -5, currency: false, strong: true }))).toBe(
      '<strong class="tabular">-$0.05</strong>',
    );
    expect(() => html(createElement(Money, { cents: 1.5 }))).toThrow();
  });

  it("shows Mazatlán time with the machine-readable instant", () => {
    // 20:30 UTC is 13:30 in Mazatlán (UTC-7).
    const markup = html(createElement(DateTime, { value: "2026-10-05T20:30:00Z" }));
    expect(markup).toContain('dateTime="2026-10-05T20:30:00Z"');
    expect(markup).toContain("13:30");
  });

  it("renders label/value pairs, skipping empty rows", () => {
    const markup = html(
      createElement(DescriptionList, {
        variant: "money",
        label: "Resumen",
        items: [{ label: "Total", value: "$1.00 MXN" }, false, null, { label: "IVA", value: "$0.16 MXN" }],
      }),
    );
    expect(markup).toBe(
      '<dl class="descList descListMoney" aria-label="Resumen"><div><dt>Total</dt><dd>$1.00 MXN</dd></div>' +
        "<div><dt>IVA</dt><dd>$0.16 MXN</dd></div></dl>",
    );
  });

  it("links a field's label, hint and error to its control", () => {
    const markup = html(
      createElement(FieldProbe),
    );
    const id = /<input id="([^"]+)"/.exec(markup)?.[1];
    expect(id).toBeDefined();
    expect(markup).toContain(`<label for="${id}" class="fieldLabel">ID de la orden</label>`);
    expect(markup).toContain(`aria-describedby="${id}-hint ${id}-error"`);
    expect(markup).toContain('aria-invalid="true"');
    expect(markup).toContain(`<p id="${id}-hint" class="fieldHint">Pega el ID completo.</p>`);
    expect(markup).toContain(`<p id="${id}-error" class="fieldError">Falta el ID.</p>`);
  });

  it("renders one page title with its actions", () => {
    const markup = html(
      createElement(PageHeader, { eyebrow: "Finanzas", title: "Liquidaciones", description: "Texto", actions: "Acción", live: true }),
    );
    expect(markup.match(/<h1>/g)).toHaveLength(1);
    expect(markup).toContain('<p class="pageEyebrow">Finanzas</p>');
    expect(markup).toContain('<div class="pageActions" aria-live="polite">Acción</div>');
  });

  it("keeps the gate and feedback texts of the tenant screens", () => {
    expect(html(createElement(ScreenGate, { phase: "no_session", accessMessage: "x" }))).toContain("Sin sesión");
    expect(html(createElement(ScreenGate, { phase: "access_unavailable", accessMessage: "Sin acceso." }))).toContain(
      "Sin acceso.",
    );
    expect(html(createElement(ScreenGate, { phase: "ready", accessMessage: "x" }))).toBe("");
    const feedback = html(createElement(Feedback, { errors: ["Error"], message: "Verifica", stepUpHref: "/auth/login?mfa=required" }));
    expect(feedback).toContain('role="alert"');
    expect(feedback).toContain("Verificar identidad");
    expect(html(createElement(EmptyState, { title: "Nada" }, "Sin datos."))).toBe(
      '<div class="emptyState"><strong>Nada</strong>Sin datos.</div>',
    );
  });
});
