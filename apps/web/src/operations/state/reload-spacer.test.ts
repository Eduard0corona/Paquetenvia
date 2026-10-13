import { describe, expect, it } from "vitest";
import { realtimeReloadSpacingMilliseconds, ReloadSpacer } from "./reload-spacer";

/** A clock and timers the test moves by hand. */
class FakeTime {
  public now = 0;
  private timers: { at: number; callback: () => void; id: number }[] = [];
  private nextId = 1;

  public setTimer = (callback: () => void, delay: number): unknown => {
    const id = this.nextId++;
    this.timers.push({ at: this.now + delay, callback, id });
    return id;
  };

  public clearTimer = (timer: unknown): void => {
    this.timers = this.timers.filter((entry) => entry.id !== timer);
  };

  public advance(milliseconds: number): void {
    const until = this.now + milliseconds;
    for (;;) {
      const due = this.timers.filter((entry) => entry.at <= until).sort((left, right) => left.at - right.at)[0];
      if (due === undefined) break;
      this.timers = this.timers.filter((entry) => entry !== due);
      this.now = due.at;
      due.callback();
    }
    this.now = until;
  }

  public get pending(): number {
    return this.timers.length;
  }
}

function spacerWith(time: FakeTime, waitLeft: () => number = () => 0) {
  const reloads: { kind: string; at: number }[] = [];
  const spacer: ReloadSpacer<"realtime" | "location"> = new ReloadSpacer<"realtime" | "location">({
    reload: (kind) => {
      reloads.push({ kind, at: time.now });
      spacer.reloaded();
    },
    waitLeft,
    stronger: (left, right) => (left === "realtime" || right === "realtime" ? "realtime" : "location"),
    now: () => time.now,
    setTimer: time.setTimer,
    clearTimer: time.clearTimer,
  });
  return { spacer, reloads };
}

/**
 * SEC-004-RELOADS-AND-BULK-2026-10-13, owner answer Q9 "Espaciar recargas (Recommended)": "El panel junta los
 * avisos y recarga como máximo cada 3 segundos por pestaña."
 */
describe("realtime reload spacing (SEC-004 Q9)", () => {
  it("spaces reloads 3 seconds apart", () => {
    expect(realtimeReloadSpacingMilliseconds).toBe(3_000);
  });

  it("reloads once after the notice's short wait when nothing reloaded lately", () => {
    const time = new FakeTime();
    const { spacer, reloads } = spacerWith(time);
    time.now = 10_000;
    spacer.request("realtime", 250);
    time.advance(249);
    expect(reloads).toEqual([]);
    time.advance(1);
    expect(reloads).toEqual([{ kind: "realtime", at: 10_250 }]);
  });

  it("gathers a burst of notices into one reload and reloads at most every 3 seconds", () => {
    const time = new FakeTime();
    const { spacer, reloads } = spacerWith(time);
    time.now = 10_000;
    // Twenty notices in two seconds, as a bulk assignment of twenty orders would send.
    for (let notice = 0; notice < 20; notice += 1) {
      spacer.request("realtime", 250);
      time.advance(100);
    }
    time.advance(10_000);
    expect(reloads.map((reload) => reload.at)).toEqual([10_250, 13_250]);
    for (let index = 1; index < reloads.length; index += 1) {
      expect(reloads[index].at - reloads[index - 1].at).toBeGreaterThanOrEqual(3_000);
    }
  });

  it("keeps reloading every 3 seconds under a steady stream of notices", () => {
    const time = new FakeTime();
    const { spacer, reloads } = spacerWith(time);
    time.now = 0;
    for (let notice = 0; notice < 100; notice += 1) {
      spacer.request("realtime", 250);
      time.advance(100);
    }
    expect(reloads.map((reload) => reload.at)).toEqual([250, 3_250, 6_250, 9_250]);
  });

  it("counts any reload of the screen, so a notice right after one waits the rest of the 3 seconds", () => {
    const time = new FakeTime();
    const { spacer, reloads } = spacerWith(time);
    time.now = 50_000;
    spacer.reloaded(); // polling or "Actualizar"
    time.advance(1_000);
    spacer.request("location", 500);
    time.advance(1_999);
    expect(reloads).toEqual([]);
    time.advance(1);
    expect(reloads).toEqual([{ kind: "location", at: 53_000 }]);
  });

  it("uses the stronger kind of the gathered notices", () => {
    const time = new FakeTime();
    const { spacer, reloads } = spacerWith(time);
    spacer.request("location", 500);
    spacer.request("realtime", 250);
    spacer.request("location", 500);
    time.advance(1_000);
    expect(reloads).toEqual([{ kind: "realtime", at: 500 }]);
  });

  it("reloads nothing during the server's wait and once when it ends", () => {
    const time = new FakeTime();
    let waitUntil = 8_000;
    const { spacer, reloads } = spacerWith(time, () => Math.max(0, waitUntil - time.now));
    time.now = 1_000;
    spacer.request("realtime", 250);
    spacer.request("realtime", 250);
    time.advance(6_999);
    expect(reloads).toEqual([]);
    time.advance(1);
    expect(reloads).toEqual([{ kind: "realtime", at: 8_000 }]);
    // A wait that grows after the timer was set is honoured too.
    waitUntil = 20_000;
    spacer.request("realtime", 250);
    time.advance(30_000);
    expect(reloads.map((reload) => reload.at)).toEqual([8_000, 20_000]);
  });

  it("drops what is pending when cancelled", () => {
    const time = new FakeTime();
    const { spacer, reloads } = spacerWith(time);
    spacer.request("realtime", 250);
    expect(spacer.hasPending).toBe(true);
    spacer.cancel();
    time.advance(10_000);
    expect(reloads).toEqual([]);
    expect(spacer.hasPending).toBe(false);
    expect(time.pending).toBe(0);
  });
});
