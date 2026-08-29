import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

describe("manual route planner policy", () => {
  it("provides drag and keyboard reorder without client persistence", () => {
    const shell = readFileSync("src/operations/components/manual-routes-shell.tsx", "utf8");
    const state = readFileSync("src/operations/state/use-manual-routes.ts", "utf8");
    expect(shell).toContain("draggable=");
    expect(shell).toContain("onDrop=");
    expect(shell).toContain("Mover orden");
    expect(shell).toContain("Agregar DELIVERY");
    expect(`${shell}\n${state}`).not.toMatch(/localStorage|sessionStorage|indexedDB|caches\./i);
  });

  it("refetches REST after RouteChanged reconnect and stale conflict", () => {
    const state = readFileSync("src/operations/state/use-manual-routes.ts", "utf8");
    expect(state).toContain("RouteChanged:");
    expect(state).toContain("resynchronizeFromRest");
    expect(state).toContain("error.category === \"conflict\"");
    expect(state).toContain("loadDetail(selectedRef.current.id)");
  });
});
