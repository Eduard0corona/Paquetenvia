import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { dashboardPath, dashboardViewHref, parseDashboardView } from "./dashboard-view";

const read = (path: string) => readFileSync(path, "utf8");
const view = (query: string) => parseDashboardView(new URLSearchParams(query));

describe("dashboard view in the URL (UI-PHASE3-INBOX-2026-10-10)", () => {
  it("reads the positions view only from view=positions", () => {
    expect(view("view=positions")).toBe("positions");
    expect(view("")).toBe("list");
    expect(view("view=list")).toBe("list");
    expect(view("view=POSITIONS")).toBe("list");
    expect(view("view=positions%20")).toBe("list");
    expect(view("other=positions")).toBe("list");
    // A full URL, as the inbox "Mapa de posiciones" link builds it.
    expect(parseDashboardView(new URL("/ops/dashboard?view=positions", "https://paquetenvia.test").searchParams)).toBe(
      "positions",
    );
  });

  it("builds the canonical URL of each view and keeps other parameters", () => {
    expect(dashboardViewHref("positions")).toBe("/ops/dashboard?view=positions");
    expect(dashboardViewHref("list")).toBe(dashboardPath);
    expect(dashboardViewHref("list", new URLSearchParams("view=positions"))).toBe("/ops/dashboard");
    expect(dashboardViewHref("positions", new URLSearchParams("view=list&keep=1"))).toBe(
      "/ops/dashboard?view=positions&keep=1",
    );
    expect(dashboardViewHref("list", new URLSearchParams("keep=1&view=positions"))).toBe("/ops/dashboard?keep=1");
    for (const next of ["list", "positions"] as const)
      expect(parseDashboardView(new URL(dashboardViewHref(next), "https://paquetenvia.test").searchParams)).toBe(next);
  });

  it("is the URL the inbox opens as Mapa de posiciones", () => {
    const inbox = read("src/operations/components/operations-inbox-shell.tsx");
    expect(inbox).toContain(`href="${dashboardViewHref("positions")}"`);
  });

  it("is read while rendering and follows later navigations, without a deferred tick", () => {
    const shell = read("src/operations/components/operations-dashboard-shell.tsx");
    // The review finding on #212: no effect with [] deps and no setTimeout deciding the view.
    expect(shell).not.toMatch(/setTimeout|useEffect|useState<"list" \| "positions">|setView\(/);
    expect(shell).not.toContain("window.location");
    expect(shell).toContain("const searchParams = useSearchParams();");
    expect(shell).toContain("const view = parseDashboardView(searchParams);");
    // The toggle updates the same URL in place (no server request, no history entry).
    expect(shell).toContain('window.history.replaceState(null, "", dashboardViewHref(next, searchParams))');
    expect(shell).toContain('onClick={() => showView("list")}');
    expect(shell).toContain('onClick={() => showView("positions")}');
    // useSearchParams on a statically rendered page needs a Suspense boundary.
    const page = read("src/app/ops/dashboard/page.tsx");
    expect(page).toContain("<Suspense");
    expect(page).toContain("<OperationsDashboardShell />");
  });
});
