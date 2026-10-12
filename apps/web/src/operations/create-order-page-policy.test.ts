import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

const read = (path: string) => readFileSync(path, "utf8");

/** Every source of "Nueva orden" (UI-PHASE3-ORDER-WIZARD-2026-10-10). */
const wizardSources = [
  "src/app/ops/orders/new/page.tsx",
  "src/operations/components/create-order-shell.tsx",
  "src/operations/components/create-order-wizard.tsx",
  "src/operations/components/create-order-summary.tsx",
  "src/operations/components/focus-later.ts",
  "src/operations/contracts/order-wizard.ts",
  "src/operations/state/create-order-controller.ts",
  "src/operations/state/use-create-order.ts",
];

describe("new order wizard page policy (UI-PHASE3-ORDER-WIZARD-2026-10-10)", () => {
  it("is declared in AI-07 with the four approved steps and the owner decision", () => {
    const aiSeven = read(resolve(process.cwd(), "../../docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml"));
    const route = aiSeven.slice(aiSeven.indexOf("\n  /ops/orders/new:\n"), aiSeven.indexOf("\n  /ops/orders/:id:\n"));
    expect(route).toContain("screen_contract: create_order");
    expect(route).toContain("- UI-PHASE3-ORDER-WIZARD-2026-10-10");
    for (const step of ['"1. Dónde"', '"2. Qué se envía"', '"3. Servicio y precio"', '"4. Confirmar"'])
      expect(route).toContain(step);
    expect(aiSeven).toContain("decision: UI-PHASE3-ORDER-WIZARD-2026-10-10");
    expect(aiSeven).toContain('reason "Confirmada al crear la orden"');
    const decisions = read(resolve(process.cwd(), "../../docs/normative/v0.6/decision-log.md"));
    expect(decisions).toContain('| UI-PHASE3-ORDER-WIZARD-2026-10-10 |');
    expect(decisions).toContain('"Confirmada (Recommended)"');
    expect(decisions).toContain('"avanza con la fase 3"');
  });

  it("keeps the draft in memory only, never logs it and does money in integer cents", () => {
    for (const path of wizardSources) {
      const source = read(path);
      expect(source, path).not.toMatch(/localStorage|sessionStorage|indexedDB|caches\.|console\.|sendBeacon/);
      expect(source, path).not.toMatch(/parseFloat|toFixed|cents\s*\/\s*100\b|\*\s*100\)/);
    }
  });

  it("calls only createQuote, createOrder and the order detail's transitionOrder client", () => {
    for (const path of wizardSources) expect(read(path), path).not.toMatch(/\bfetch\(|\/api\/v1\//);
    const hook = read("src/operations/state/use-create-order.ts");
    expect(hook).toContain("createOrdersApi(apiBaseUrl(), session)");
    expect(hook).toContain("createOrderActionsApi(apiBaseUrl(), session)");
    // The confirmation is the server's CONFIRMED transition as "Siguiente paso" builds it, no status logic here.
    const contract = read("src/operations/contracts/order-wizard.ts");
    expect(contract).toContain("nextStepActions([");
    expect(contract).toContain('{ target_status: "CONFIRMED", required_metadata: ["restricted_goods_acknowledged"] }');
    const controller = read("src/operations/state/create-order-controller.ts");
    expect(controller).toContain("api.createOrder(");
    expect(controller).toContain("actions.transitionOrder(");
    expect(controller.indexOf("api.createOrder(")).toBeLessThan(controller.indexOf("actions.transitionOrder("));
  });

  it("only says the order was confirmed in the confirmed outcome", () => {
    const summary = read("src/operations/components/create-order-summary.tsx");
    expect(summary.match(/Orden creada y confirmada/g)).toHaveLength(1);
    const confirmed = summary.slice(
      summary.indexOf('if (outcome.kind === "confirmed")'),
      summary.indexOf('const draft = outcome.certainty === "draft";'),
    );
    expect(confirmed).toContain("Orden creada y confirmada");
    // Nothing else on the screen (header included) claims the order ends confirmed: it may not.
    for (const path of wizardSources.filter((path) => !path.endsWith("create-order-summary.tsx")))
      expect(read(path), path).not.toMatch(/creada y confirmada/i);
  });

  it("lays the summary beside the steps only on wide screens and keeps 44 px targets", () => {
    const css = read("src/app/globals.css");
    const section = css.slice(css.indexOf("/* ---------- New order wizard"), css.indexOf("/* ---------- Order detail"));
    expect(section).toContain("grid-template-columns: repeat(4, minmax(0, 1fr));");
    const layout = section.slice(section.indexOf(".opsWizardLayout {"), section.indexOf(".opsWizardForm {"));
    expect(layout).not.toContain("grid-template-columns");
    const wide = section.slice(section.indexOf("@media (min-width: 1100px)"));
    expect(wide).toContain('grid-template-areas: "form summary";');
    expect(wide).toContain("position: sticky;");
    const item = section.slice(section.indexOf(".opsWizardStepItem {"), section.indexOf("button.opsWizardStepItem {"));
    expect(item).toContain("min-height: var(--target);");
    expect(section).toMatch(/\.opsWizardForm \.opsCheckbox \{[^}]*min-height: var\(--target\);/);
  });

  it("moves focus to the step title or the first message and never disables the step buttons while busy", () => {
    const wizard = read("src/operations/components/create-order-wizard.tsx");
    expect(wizard).toContain("focusLater(...(first === undefined ? [] : [fieldControlId(first.field)]), stepHeadingId)");
    expect(wizard).toContain('aria-current={status === "current" ? "step" : undefined}');
    expect(wizard).toContain("aria-disabled={busy}");
    // The only disabled button is the confirmation without configured terms and privacy versions.
    expect(wizard.match(/\sdisabled=\{/g)).toHaveLength(1);
    expect(wizard).toContain('disabled={state.step === "confirm" && !versionsConfigured}');
  });
});
