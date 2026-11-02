import { describe, expect, it } from "vitest";
import { clearThenStopPrevious } from "./session-switch";

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
