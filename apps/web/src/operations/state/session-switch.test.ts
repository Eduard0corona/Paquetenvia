import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { clearThenStopPrevious } from "./session-switch";

const read = (path: string) => readFileSync(path, "utf8");

/** The text of `source` from `from` to the first `to` after it. */
function between(source: string, from: string, to: string): string {
  const start = source.indexOf(from);
  expect(start, from).toBeGreaterThanOrEqual(0);
  const end = source.indexOf(to, start + from.length);
  expect(end, to).toBeGreaterThan(start);
  return source.slice(start, end + to.length);
}

describe("organization switch order (Z15)", () => {
  it("clears before the previous connection starts closing and resolves only once it closed", async () => {
    const steps: string[] = [];
    let closed!: () => void;
    const switching = clearThenStopPrevious(
      () => steps.push("clear"),
      () =>
        new Promise<void>((resolve) => {
          steps.push("stop");
          closed = () => {
            steps.push("closed");
            resolve();
          };
        }),
    );
    // The previous organization's state is already gone while its connection is still closing.
    expect(steps).toEqual(["clear", "stop"]);
    let done = false;
    void switching.then(() => {
      done = true;
    });
    await Promise.resolve();
    expect(done).toBe(false);
    closed();
    await switching;
    expect(steps).toEqual(["clear", "stop", "closed"]);
  });
});

describe("organization switch on the order detail and Rutas (Z17)", () => {
  it("the order detail drops the order, its row and its session before the previous connection closes", () => {
    const start = between(
      read("src/operations/state/use-operations-order-detail.ts"),
      "const start = useCallback(async () => {",
      "const session = readOperationsSession();",
    );
    const stop = start.indexOf("await previous.stop()");
    expect(start).toContain("await clearThenStopPrevious(");
    for (const cleared of [
      "refreshScopeRef.current = scope;",
      "refreshCoordinatorRef.current.cancel();",
      "reloads.cancel();",
      "sessionRef.current = null;",
      "apiRef.current = null;",
      "setOrder(null);",
      "setProjection(null);",
      "setLastUpdated(null);",
      'setConnectionState("Conectando");',
    ]) {
      const at = start.indexOf(cleared);
      expect(at, cleared).toBeGreaterThan(0);
      expect(at, cleared).toBeLessThan(stop);
    }
    // A newer change that started while the connection closed reads its own session; this one gives up.
    expect(start).toContain("if (refreshScopeRef.current !== scope) return;");
  });

  it("the order detail's panels never follow the session by themselves and mount only with an order read", () => {
    for (const panel of [
      "src/operations/components/operations-driver-assignment.tsx",
      "src/operations/components/operations-tracking-link.tsx",
      "src/operations/components/operations-recipient-notices.tsx",
      "src/operations/components/operations-failed-attempt.tsx",
      "src/operations/components/operations-next-step.tsx",
    ])
      expect(read(panel), panel).not.toContain("subscribeToOperationsSession");
    const shell = read("src/operations/components/operations-order-detail-shell.tsx");
    const loading = shell.indexOf("if (state.order === null || state.projection === null) {");
    expect(loading).toBeGreaterThan(0);
    for (const panel of [
      "<OperationsFailedAttempt",
      "<OperationsNextStep",
      "<OperationsDriverAssignment",
      "<OperationsTrackingLink",
      "<OperationsRecipientNotices",
    ])
      expect(shell.indexOf(panel), panel).toBeGreaterThan(loading);
    // The inbox's "Asignar" panel is dropped by the inbox itself (Z15).
    expect(read("src/operations/components/operations-inbox-shell.tsx")).not.toContain("followSessionChanges");
  });

  it("Rutas empties the screen before the previous hub closes and starts only after", () => {
    const hook = read("src/operations/state/use-manual-routes.ts");
    const clear = hook.indexOf("await clearThenStopPrevious(() => controller.stop(), stopConnection);");
    expect(clear).toBeGreaterThan(0);
    expect(hook.indexOf("if (cancelled || current !== run) return;", clear)).toBeGreaterThan(clear);
    expect(hook.indexOf("await controller.start();")).toBeGreaterThan(clear);
    expect(hook).not.toMatch(/await stopConnection\(\);\s*const session/);
  });

  it("the dashboard no longer shows Conectada while the previous connection closes", () => {
    const clear = between(
      read("src/operations/state/use-operations-dashboard.ts"),
      "const clearForSessionChange = useCallback(async () => {",
      "await current.stop()",
    );
    expect(clear).toContain('setConnection("Conectando");');
  });
});
