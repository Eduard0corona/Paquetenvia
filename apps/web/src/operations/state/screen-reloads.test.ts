import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RateLimitWaits } from "../../lib/rate-limit-wait";
import { realtimeReloadSpacingMilliseconds, ScreenReloads } from "./screen-reloads";

type Kind = "realtime" | "location";

function screen(waits = new RateLimitWaits(), spacing?: number) {
  const reloads: Kind[] = [];
  const at: number[] = [];
  const start = Date.now();
  const gate: ScreenReloads<Kind> = new ScreenReloads<Kind>({
    operations: ["dashboard", "queue_counts"],
    waits,
    spacing,
    stronger: (left, right) => (left === "realtime" || right === "realtime" ? "realtime" : "location"),
    reload: (kind) => {
      // The screen counts every reload it starts, as the hooks and controllers do.
      gate.reloaded();
      reloads.push(kind);
      at.push(Date.now() - start);
    },
  });
  return { gate, reloads, at, waits };
}

describe("SEC-004 realtime reload spacing (owner answer Q9, 'Espaciar recargas')", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-13T12:00:00Z"));
  });
  afterEach(() => vi.useRealTimers());

  it("reloads at most once every 3 seconds, however many notices arrive", async () => {
    expect(realtimeReloadSpacingMilliseconds).toBe(3_000);
    const { gate, reloads, at } = screen();
    // A burst (a bulk assignment): 40 notices within 2 seconds.
    for (let notice = 0; notice < 40; notice += 1) {
      gate.notice("realtime", 250);
      await vi.advanceTimersByTimeAsync(50);
    }
    await vi.advanceTimersByTimeAsync(10_000);
    // The first after its 250 ms debounce, the rest gathered into one reload 3 s after it.
    expect(reloads).toEqual(["realtime", "realtime"]);
    expect(at).toEqual([250, 3_250]);
    expect(vi.getTimerCount()).toBe(0);
  });

  it("never loses a notice that arrives during the spacing: one reload when it ends", async () => {
    const { gate, reloads, at } = screen();
    gate.notice("realtime", 250);
    await vi.advanceTimersByTimeAsync(250);
    expect(reloads).toHaveLength(1);
    // Right after a reload: it is read by one reload 3 s after the previous one.
    await vi.advanceTimersByTimeAsync(100);
    gate.notice("location", 500);
    await vi.advanceTimersByTimeAsync(2_000);
    expect(reloads).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(1_000);
    expect(reloads).toEqual(["realtime", "location"]);
    expect(at).toEqual([250, 3_250]);
  });

  it("gathers an order notice and a driver-position notice into the reload that also reads the counts", async () => {
    const { gate, reloads } = screen();
    gate.reloaded();
    gate.notice("location", 500);
    gate.notice("realtime", 250);
    gate.notice("location", 500);
    await vi.advanceTimersByTimeAsync(3_000);
    expect(reloads).toEqual(["realtime"]);
  });

  it("counts a reload of any kind (polling, 'Actualizar') for the spacing", async () => {
    const { gate, reloads, at } = screen();
    await vi.advanceTimersByTimeAsync(1_000);
    gate.reloaded();
    gate.notice("realtime", 250);
    await vi.advanceTimersByTimeAsync(2_999);
    expect(reloads).toEqual([]);
    await vi.advanceTimersByTimeAsync(1);
    expect(at).toEqual([4_000]);
  });

  it("drops what is pending when the screen leaves or the session changes", async () => {
    const { gate, reloads } = screen();
    gate.notice("realtime", 250);
    gate.cancel();
    expect(vi.getTimerCount()).toBe(0);
    await vi.advanceTimersByTimeAsync(10_000);
    expect(reloads).toEqual([]);
  });

  it("reloads after the debounce alone without realtime spacing (a screen without notices)", async () => {
    const { gate, reloads } = screen(new RateLimitWaits(), 0);
    gate.reloaded();
    gate.notice("realtime", 0);
    await vi.advanceTimersByTimeAsync(0);
    expect(reloads).toEqual(["realtime"]);
  });
});

describe("SEC-004 server wait on a screen that refreshes itself (owner answers Q11, Q14, Q16)", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-13T12:00:00Z"));
  });
  afterEach(() => vi.useRealTimers());

  it("refuses every read during the wait and reloads exactly once, by itself, when it ends", async () => {
    const { gate, reloads, at, waits } = screen();
    waits.record("dashboard", 2);
    gate.rateLimited("realtime");
    // Polling, the page becoming visible, "Actualizar", a filter change and realtime notices during the wait.
    expect(gate.admit("realtime")).toBe(false);
    expect(gate.admit("location")).toBe(false);
    gate.notice("realtime", 250);
    gate.notice("location", 500);
    await vi.advanceTimersByTimeAsync(1_000);
    expect(gate.admit("realtime")).toBe(false);
    expect(reloads).toEqual([]);
    await vi.advanceTimersByTimeAsync(1_000);
    expect(reloads).toEqual(["realtime"]);
    expect(at).toEqual([2_000]);
    expect(gate.waiting).toBe(false);
    expect(gate.admit("realtime")).toBe(true);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(reloads).toHaveLength(1);
    expect(vi.getTimerCount()).toBe(0);
  });

  it("pauses the whole screen when only the counts are limited (Q16, 'Toda la pantalla')", async () => {
    const { gate, reloads, waits } = screen();
    waits.record("queue_counts", 5);
    expect(gate.waiting).toBe(true);
    expect(gate.admit("location")).toBe(false);
    await vi.advanceTimersByTimeAsync(5_000);
    expect(reloads).toEqual(["location"]);
  });

  it("is not held by an unrelated limit", () => {
    const { gate, waits } = screen();
    waits.record("order_list", 60);
    waits.record("assignable_drivers", 60);
    expect(gate.waiting).toBe(false);
    expect(gate.admit("realtime")).toBe(true);
    expect(vi.getTimerCount()).toBe(0);
  });

  it("waits again when another 429 extends the wait before it ends", async () => {
    const { gate, reloads, at, waits } = screen();
    waits.record("dashboard", 2);
    gate.rateLimited("realtime");
    await vi.advanceTimersByTimeAsync(1_000);
    waits.record("queue_counts", 5);
    await vi.advanceTimersByTimeAsync(4_999);
    expect(reloads).toEqual([]);
    await vi.advanceTimersByTimeAsync(1);
    expect(at).toEqual([6_000]);
  });

  it("reloads when a short wait ends, not 3 seconds after the read that met the 429", async () => {
    const { gate, at, waits } = screen();
    gate.reloaded();
    waits.record("dashboard", 1);
    gate.rateLimited("realtime");
    await vi.advanceTimersByTimeAsync(10_000);
    expect(at).toEqual([1_000]);
  });

  it("reads the notices that arrived during the wait with the one reload at 0", async () => {
    const { gate, reloads, at, waits } = screen();
    gate.reloaded();
    gate.notice("location", 500);
    waits.record("dashboard", 1);
    expect(gate.admit("realtime")).toBe(false);
    await vi.advanceTimersByTimeAsync(10_000);
    expect(reloads).toEqual(["realtime"]);
    expect(at).toEqual([1_000]);
  });
});
