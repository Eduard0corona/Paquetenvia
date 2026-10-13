import { describe, expect, it } from "vitest";
import {
  rateLimitFallbackSeconds,
  rateLimitMessage,
  RateLimitWaits,
  sleepUntilWaitEnds,
} from "./rate-limit-wait";

/** SEC-004-HEAVY-ENDPOINT-MAPPING-2026-10-12, owner answer Q6 "Mensaje común con espera (Recommended)". */
describe("panel rate-limit wait (SEC-004)", () => {
  it("says the owner's text with the whole seconds left", () => {
    expect(rateLimitMessage(42)).toBe("Demasiadas solicitudes. Espera 42 segundos e intenta de nuevo.");
    expect(rateLimitMessage(1)).toBe("Demasiadas solicitudes. Espera 1 segundo e intenta de nuevo.");
    expect(rateLimitMessage(0.2)).toBe("Demasiadas solicitudes. Espera 1 segundo e intenta de nuevo.");
    expect(rateLimitMessage(59.1)).toBe("Demasiadas solicitudes. Espera 60 segundos e intenta de nuevo.");
  });

  it("holds each operation for the server's Retry-After and counts it down", () => {
    let now = 1_000_000;
    const waits = new RateLimitWaits(() => now);
    waits.record("csv_import", 7);
    expect(waits.secondsLeft(["csv_import"])).toBe(7);
    expect(waits.isWaiting(["csv_import"])).toBe(true);
    // Other operations keep their own budget.
    expect(waits.isWaiting(["quote", "order_list"])).toBe(false);
    now += 6_200;
    expect(waits.secondsLeft(["csv_import"])).toBe(1);
    expect(waits.millisecondsLeft(["csv_import"])).toBe(800);
    now += 800;
    expect(waits.isWaiting(["csv_import"])).toBe(false);
    expect(waits.secondsLeft(["csv_import"])).toBe(0);
  });

  it("waits the API's window without a usable Retry-After and never more than an hour", () => {
    const now = 0;
    const waits = new RateLimitWaits(() => now);
    waits.record("dashboard", null);
    expect(waits.secondsLeft(["dashboard"])).toBe(rateLimitFallbackSeconds);
    waits.record("quote", 86_400);
    expect(waits.secondsLeft(["quote"])).toBe(3_600);
    waits.record("order_list", 0);
    expect(waits.secondsLeft(["order_list"])).toBe(1);
  });

  it("keeps the longest wait and reports the longest of several operations", () => {
    let now = 0;
    const waits = new RateLimitWaits(() => now);
    waits.record("dashboard", 30);
    waits.record("dashboard", 5);
    waits.record("queue_counts", 12);
    expect(waits.secondsLeft(["dashboard"])).toBe(30);
    expect(waits.secondsLeft(["queue_counts", "dashboard"])).toBe(30);
    now += 20_000;
    expect(waits.secondsLeft(["queue_counts", "dashboard"])).toBe(10);
  });

  it("records a screen's 429 only when the API client did not", () => {
    const now = 0;
    const waits = new RateLimitWaits(() => now);
    waits.record("assignable_drivers", 20);
    waits.ensure("assignable_drivers", 3);
    expect(waits.secondsLeft(["assignable_drivers"])).toBe(20);
    waits.ensure("order_list", 3);
    expect(waits.secondsLeft(["order_list"])).toBe(3);
  });

  it("tells its subscribers about every change", () => {
    const waits = new RateLimitWaits(() => 0);
    let calls = 0;
    const unsubscribe = waits.subscribe(() => {
      calls += 1;
    });
    const before = waits.getVersion();
    waits.record("quote", 2);
    waits.clear();
    unsubscribe();
    waits.record("quote", 2);
    expect(calls).toBe(2);
    expect(waits.getVersion()).toBe(before + 3);
  });

  it("sleeps until the wait ends, at once without one or once aborted", async () => {
    const waits = new RateLimitWaits(() => 0);
    const slept: number[] = [];
    const sleep = async (milliseconds: number) => {
      slept.push(milliseconds);
    };
    const signal = new AbortController().signal;
    await sleepUntilWaitEnds(waits, ["assignable_drivers"], signal, sleep);
    waits.record("assignable_drivers", 4);
    await sleepUntilWaitEnds(waits, ["assignable_drivers"], signal, sleep);
    const aborted = new AbortController();
    aborted.abort();
    await sleepUntilWaitEnds(waits, ["assignable_drivers"], aborted.signal, sleep);
    expect(slept).toEqual([4_000]);
  });
});
