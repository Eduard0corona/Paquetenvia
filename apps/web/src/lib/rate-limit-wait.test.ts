import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  rateLimitFallbackSeconds,
  rateLimitMessage,
  rateLimitSecondsOf,
  RateLimitWaits,
  sleepUntilWaitEnds,
  watchWait,
} from "./rate-limit-wait";

describe("SEC-004 panel wait: text and seconds (owner answers Q6, Q12)", () => {
  it("says the approved text with N seconds, and '1 segundo' when one is left", () => {
    expect(rateLimitMessage(60)).toBe("Demasiadas solicitudes. Espera 60 segundos e intenta de nuevo.");
    expect(rateLimitMessage(2)).toBe("Demasiadas solicitudes. Espera 2 segundos e intenta de nuevo.");
    expect(rateLimitMessage(1)).toBe("Demasiadas solicitudes. Espera 1 segundo e intenta de nuevo.");
    // A part of a second still to wait counts as a whole one.
    expect(rateLimitMessage(1.9)).toBe("Demasiadas solicitudes. Espera 2 segundos e intenta de nuevo.");
    expect(rateLimitMessage(0.2)).toBe("Demasiadas solicitudes. Espera 1 segundo e intenta de nuevo.");
  });

  it("takes N from Retry-After, and 60 when it is missing or not a positive whole number", () => {
    expect(rateLimitFallbackSeconds).toBe(60);
    expect(rateLimitSecondsOf(new Headers({ "Retry-After": "2" }))).toBe(2);
    expect(rateLimitSecondsOf(new Headers({ "Retry-After": "60" }))).toBe(60);
    expect(rateLimitSecondsOf(new Headers({ "Retry-After": "86400" }))).toBe(3_600);
    expect(rateLimitSecondsOf(new Headers())).toBe(60);
    for (const value of ["0", "-3", "1.5", "+2", "soon", "", "Wed, 21 Oct 2026 07:28:00 GMT"]) {
      expect(rateLimitSecondsOf(new Headers({ "Retry-After": value }))).toBe(60);
    }
  });
});

describe("SEC-004 panel wait store", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-13T12:00:00Z"));
  });
  afterEach(() => vi.useRealTimers());

  it("keeps one wait per limited operation, so a limit never holds an unrelated one", () => {
    const waits = new RateLimitWaits();
    waits.record("dashboard", 2);
    expect(waits.isWaiting(["dashboard"])).toBe(true);
    expect(waits.secondsLeft(["dashboard"])).toBe(2);
    expect(waits.isWaiting(["queue_counts"])).toBe(false);
    expect(waits.isWaiting(["order_list", "csv_import"])).toBe(false);
    // A screen that reads both waits for either (owner answer Q16).
    expect(waits.secondsLeft(["dashboard", "queue_counts"])).toBe(2);

    vi.advanceTimersByTime(1_001);
    expect(waits.secondsLeft(["dashboard"])).toBe(1);
    vi.advanceTimersByTime(999);
    expect(waits.isWaiting(["dashboard"])).toBe(false);
    expect(waits.secondsLeft(["dashboard"])).toBe(0);
  });

  it("never shortens a running wait, takes the longer one, and uses 60 s without a usable Retry-After", () => {
    const waits = new RateLimitWaits();
    waits.record("order_list", 30);
    waits.record("order_list", 5);
    expect(waits.secondsLeft(["order_list"])).toBe(30);
    waits.record("order_list", 45);
    expect(waits.secondsLeft(["order_list"])).toBe(45);
    waits.record("quote", null);
    expect(waits.secondsLeft(["quote"])).toBe(60);
    waits.record("csv_import", 0);
    expect(waits.secondsLeft(["csv_import"])).toBe(1);
  });

  it("tells its subscribers about every recorded wait and names the newest one", () => {
    const waits = new RateLimitWaits();
    const listener = vi.fn();
    const unsubscribe = waits.subscribe(listener);
    expect(waits.newest()).toBeNull();
    waits.record("dashboard", 60);
    waits.record("queue_counts", 2);
    expect(listener).toHaveBeenCalledTimes(2);
    expect(waits.newest()?.seconds).toBe(2);
    vi.advanceTimersByTime(2_000);
    // The newest one ended: the older, longer one still runs, but it was announced already, with seconds no longer
    // left, so nothing is named.
    expect(waits.isWaiting(["dashboard"])).toBe(true);
    expect(waits.newest()).toBeNull();
    unsubscribe();
    waits.clear();
    expect(listener).toHaveBeenCalledTimes(2);
    expect(waits.newest()).toBeNull();
  });

  it("counts down once per second to 0 and leaves no timer behind", () => {
    const waits = new RateLimitWaits();
    const seen: number[] = [];
    const stop = watchWait(waits, () => waits.millisecondsLeft(["quote"]), true, () =>
      seen.push(waits.secondsLeft(["quote"])),
    );
    expect(vi.getTimerCount()).toBe(0);
    waits.record("quote", 3);
    expect(seen).toEqual([3]);
    vi.advanceTimersByTime(1_000);
    expect(seen).toEqual([3, 2]);
    vi.advanceTimersByTime(2_000);
    expect(seen).toEqual([3, 2, 1, 0]);
    expect(vi.getTimerCount()).toBe(0);
    vi.advanceTimersByTime(10_000);
    expect(seen).toEqual([3, 2, 1, 0]);
    stop();
  });

  it("stops its timer when unsubscribed before the wait ends (unmount)", () => {
    const waits = new RateLimitWaits();
    const listener = vi.fn();
    const stop = watchWait(waits, () => waits.millisecondsLeft(["dashboard"]), true, listener);
    waits.record("dashboard", 60);
    expect(vi.getTimerCount()).toBe(1);
    stop();
    expect(vi.getTimerCount()).toBe(0);
    vi.advanceTimersByTime(60_000);
    expect(listener).toHaveBeenCalledTimes(1);
    waits.record("dashboard", 120);
    expect(listener).toHaveBeenCalledTimes(1);
  });

  it("can wake only when the wait ends (the announcer)", () => {
    const waits = new RateLimitWaits();
    const listener = vi.fn();
    const stop = watchWait(waits, () => waits.millisecondsLeft(["order_list"]), false, listener);
    waits.record("order_list", 5);
    vi.advanceTimersByTime(4_999);
    expect(listener).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(1);
    expect(listener).toHaveBeenCalledTimes(2);
    expect(vi.getTimerCount()).toBe(0);
    stop();
  });

  it("sleeps until the wait ends, including a wait recorded meanwhile, and stops on abort", async () => {
    const waits = new RateLimitWaits();
    const abort = new AbortController();
    let done = false;
    void sleepUntilWaitEnds(waits, ["assignable_drivers"], abort.signal).then(() => (done = true));
    await vi.advanceTimersByTimeAsync(0);
    expect(done).toBe(true);

    done = false;
    waits.record("assignable_drivers", 2);
    void sleepUntilWaitEnds(waits, ["assignable_drivers"], abort.signal).then(() => (done = true));
    await vi.advanceTimersByTimeAsync(1_000);
    waits.record("assignable_drivers", 3);
    await vi.advanceTimersByTimeAsync(1_000);
    expect(done).toBe(false);
    await vi.advanceTimersByTimeAsync(2_000);
    expect(done).toBe(true);

    done = false;
    waits.record("assignable_drivers", 60);
    void sleepUntilWaitEnds(waits, ["assignable_drivers"], abort.signal).then(() => (done = true));
    abort.abort();
    await vi.advanceTimersByTimeAsync(0);
    expect(done).toBe(true);
    expect(vi.getTimerCount()).toBe(0);
  });
});
