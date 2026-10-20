import { describe, expect, it, vi } from "vitest";
import { isDriverPagePath } from "./components/bff-session-bootstrap";
import {
  watchDriverSessionConfirmation,
  type DriverSessionConfirmationHost,
} from "./driver-session-confirmation";
import {
  driverSessionCheckRequestedEvent,
  type DriverSession,
} from "../driver/session/driver-session";

const remembered: DriverSession = {
  organizationId: "11111111-1111-4111-8111-111111111111",
  cacheNamespace: "N".repeat(32),
  credentialMode: "cookie",
  getCsrfToken: () => {
    throw new Error("none");
  },
  remembered: true,
};
const confirmed: DriverSession = {
  organizationId: remembered.organizationId,
  cacheNamespace: remembered.cacheNamespace,
  credentialMode: "cookie",
  getCsrfToken: () => "C".repeat(43),
};

function page(session?: DriverSession) {
  const listeners = new Map<string, Set<() => void>>();
  const on = (type: string, listener: () => void) => {
    if (!listeners.has(type)) listeners.set(type, new Set());
    listeners.get(type)!.add(listener);
  };
  const off = (type: string, listener: () => void) => listeners.get(type)?.delete(listener);
  const fire = (type: string) => {
    for (const listener of [...(listeners.get(type) ?? [])]) listener();
  };
  const host: DriverSessionConfirmationHost & {
    navigator: { onLine: boolean };
    document: { visibilityState: DocumentVisibilityState } & DriverSessionConfirmationHost["document"];
  } = {
    __paquetenviaDriverSession: session,
    navigator: { onLine: true },
    document: {
      visibilityState: "visible",
      addEventListener: (type, listener) => on(`document:${type}`, listener),
      removeEventListener: (type, listener) => off(`document:${type}`, listener),
    },
    addEventListener: on,
    removeEventListener: off,
  };
  return { host, fire, listeners };
}

function manualTimers() {
  const pending: { callback: () => void; milliseconds: number }[] = [];
  return {
    pending,
    setTimer: (callback: () => void, milliseconds: number) => {
      const timer = { callback, milliseconds };
      pending.push(timer);
      return timer;
    },
    clearTimer: (timer: unknown) => {
      const index = pending.indexOf(timer as (typeof pending)[number]);
      if (index >= 0) pending.splice(index, 1);
    },
    fireAll: () => {
      for (const timer of pending.splice(0)) timer.callback();
    },
  };
}

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

describe("driver session confirmation (DRV-SESSION-EXPIRY-OFFLINE-2026-10-19, owner answer E3)", () => {
  it("checks the session again when the connection returns, while it is only remembered", async () => {
    const { host, fire } = page(remembered);
    const timers = manualTimers();
    const check = vi.fn(async () => {
      host.__paquetenviaDriverSession = confirmed;
    });
    const stop = watchDriverSessionConfirmation({ host, initial: Promise.resolve(), check, ...timers });
    await settle();

    fire("online");
    await settle();
    expect(check).toHaveBeenCalledOnce();

    // Confirmed: nothing more is asked, by any trigger.
    fire("online");
    fire(driverSessionCheckRequestedEvent);
    fire("document:visibilitychange");
    timers.fireAll();
    await settle();
    expect(check).toHaveBeenCalledOnce();
    expect(timers.pending).toEqual([]);
    stop();
  });

  it("asks again when the page is shown or the stops screen asks, one check at a time", async () => {
    const { host, fire } = page(remembered);
    let release: () => void = () => undefined;
    const check = vi.fn(
      () =>
        new Promise<void>((resolve) => {
          release = resolve;
        }),
    );
    const stop = watchDriverSessionConfirmation({
      host,
      initial: Promise.resolve(),
      check,
      ...manualTimers(),
    });
    await settle();

    fire(driverSessionCheckRequestedEvent);
    fire("document:visibilitychange");
    fire("online");
    expect(check).toHaveBeenCalledOnce();
    release();
    await settle();

    fire("document:visibilitychange");
    expect(check).toHaveBeenCalledTimes(2);
    stop();
  });

  it("waits for the page's first check and asks nothing offline or hidden", async () => {
    const { host, fire } = page(remembered);
    let finishInitial: () => void = () => undefined;
    const initial = new Promise<void>((resolve) => {
      finishInitial = resolve;
    });
    const check = vi.fn(async () => undefined);
    const stop = watchDriverSessionConfirmation({ host, initial, check, ...manualTimers() });

    fire("online");
    expect(check).not.toHaveBeenCalled();
    finishInitial();
    await settle();

    host.navigator.onLine = false;
    fire(driverSessionCheckRequestedEvent);
    host.navigator.onLine = true;
    host.document.visibilityState = "hidden";
    fire("document:visibilitychange");
    expect(check).not.toHaveBeenCalled();
    stop();
  });

  it("retries on a timer only while a remembered session waits, online and shown", async () => {
    const { host } = page(remembered);
    const timers = manualTimers();
    const check = vi.fn(async () => undefined);
    const stop = watchDriverSessionConfirmation({
      host,
      initial: Promise.resolve(),
      check,
      retryMilliseconds: 30_000,
      ...timers,
    });
    await settle();
    expect(timers.pending.map((timer) => timer.milliseconds)).toEqual([30_000]);

    timers.fireAll();
    await settle();
    expect(check).toHaveBeenCalledOnce();
    expect(timers.pending).toHaveLength(1);

    // No remembered session (for example "Sesión no disponible"): no polling.
    host.__paquetenviaDriverSession = undefined;
    timers.fireAll();
    await settle();
    expect(check).toHaveBeenCalledTimes(2);
    expect(timers.pending).toEqual([]);
    stop();
  });

  it("stops listening and drops its timer when the page goes away", async () => {
    const { host, fire, listeners } = page(remembered);
    const timers = manualTimers();
    const check = vi.fn(async () => undefined);
    const stop = watchDriverSessionConfirmation({ host, initial: Promise.resolve(), check, ...timers });
    await settle();

    stop();
    fire("online");
    expect(check).not.toHaveBeenCalled();
    expect(timers.pending).toEqual([]);
    expect([...listeners.values()].every((set) => set.size === 0)).toBe(true);
  });

  it("applies to the driver pages only", () => {
    expect(isDriverPagePath("/driver/stops")).toBe(true);
    expect(isDriverPagePath("/driver/stops/22222222-2222-2222-2222-222222222222")).toBe(true);
    expect(isDriverPagePath("/driver")).toBe(true);
    for (const path of ["/", "/ops/inbox", "/finance/cod", "/drivers", "/login"]) {
      expect(isDriverPagePath(path)).toBe(false);
    }
  });
});
