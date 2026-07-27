import { afterEach, describe, expect, it, vi } from "vitest";
import { asUuid, type RealtimeEnvelope, type Uuid } from "./envelope";
import { realtimeEventTypes } from "./event-types";

const signalr = vi.hoisted(() => {
  let reconnectHandler: (() => Promise<void>) | undefined;
  let reconnectingHandler: ((error?: Error) => void) | undefined;
  const stop = vi.fn(async () => undefined);
  return {
    stop,
    start: vi.fn(async () => undefined),
    reconnectDelays: [] as number[],
    register(handler: () => Promise<void>) {
      reconnectHandler = handler;
    },
    registerReconnecting(handler: (error?: Error) => void) {
      reconnectingHandler = handler;
    },
    async reconnect() {
      if (reconnectHandler === undefined) {
        throw new Error("Reconnect callback was not registered.");
      }

      await reconnectHandler();
    },
    reconnecting(error?: Error) {
      reconnectingHandler?.(error);
    },
  };
});

vi.mock("@microsoft/signalr", () => ({
  HubConnectionState: { Disconnected: "Disconnected" },
  LogLevel: { Warning: 3 },
  HubConnectionBuilder: class {
    public withUrl(): this {
      return this;
    }

    public withAutomaticReconnect(delays: number[]): this {
      signalr.reconnectDelays = delays;
      return this;
    }

    public configureLogging(): this {
      return this;
    }

    public build() {
      return {
        state: "Disconnected",
        start: signalr.start,
        stop: signalr.stop,
        onreconnecting: (handler: (error?: Error) => void) =>
          signalr.registerReconnecting(handler),
        onreconnected: (handler: () => Promise<void>) => signalr.register(handler),
      };
    }
  },
}));

import { buildManagedConnection } from "./base-connection";
import { createOperationsApi } from "../operations/api/operations-api";
import type { OperationsDashboardResponse } from "../operations/contracts/operations-dashboard";
import type { OperationsSession } from "../operations/session/operations-session";
import { AuthoritativeRefreshCoordinator } from "../operations/state/authoritative-refresh-coordinator";

interface TestPayload {
  readonly value: string;
}

const aggregateId = asUuid("11111111-1111-1111-1111-111111111111");

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

function event(
  eventId: string,
  aggregateVersion: number,
): RealtimeEnvelope<Uuid, TestPayload> {
  return {
    event_id: asUuid(eventId),
    event_type: realtimeEventTypes.orderStatusChanged,
    occurred_at: "2026-07-24T00:00:00.000Z",
    aggregate_id: aggregateId,
    aggregate_version: aggregateVersion,
    payload: { value: "synthetic" },
  };
}

describe("managed SignalR reconnect lifecycle", () => {
  it("uses bounded automatic reconnect and replaces missed state from mandatory REST sync", async () => {
    const synchronize = vi.fn(async () => ({
      aggregate_versions: { [aggregateId]: 5 },
    }));
    const onReconnecting = vi.fn();
    const onReconnected = vi.fn();
    const built = buildManagedConnection("https://api.synthetic.local/hubs/tracking", {
      baseUrl: "https://api.synthetic.local",
      tokenFactory: async () => "ephemeral-token",
      resynchronizeFromRest: synchronize,
      onReconnecting,
      onReconnected,
    });
    expect(built.guard.shouldApply(
      event("22222222-2222-2222-2222-222222222222", 1),
    )).toBe(true);

    const transportError = new Error("controlled transport interruption");
    signalr.reconnecting(transportError);
    await signalr.reconnect();

    expect(signalr.reconnectDelays).toEqual([0, 2_000, 10_000, 30_000]);
    expect(onReconnecting).toHaveBeenCalledWith(transportError);
    expect(onReconnected).toHaveBeenCalledOnce();
    expect(synchronize).toHaveBeenCalledOnce();
    expect(built.guard.shouldApply(
      event("33333333-3333-3333-3333-333333333333", 4),
    )).toBe(false);
    const current = event("44444444-4444-4444-4444-444444444444", 6);
    expect(built.guard.shouldApply(current)).toBe(true);
    expect(built.guard.shouldApply(current)).toBe(false);
  });

  it("stops the connection when REST resynchronization fails closed", async () => {
    signalr.stop.mockClear();
    const onError = vi.fn();
    buildManagedConnection("https://api.synthetic.local/hubs/tracking", {
      baseUrl: "https://api.synthetic.local",
      tokenFactory: async () => "ephemeral-token",
      resynchronizeFromRest: async () => {
        throw new Error("controlled REST outage");
      },
      onResynchronizationError: onError,
    });

    await signalr.reconnect();

    expect(onError).toHaveBeenCalledOnce();
    expect(signalr.stop).toHaveBeenCalledOnce();
  });

  it("stops reconnect when mandatory REST token acquisition times out", async () => {
    vi.useFakeTimers();
    signalr.stop.mockClear();
    const token = deferred<string>();
    const session: OperationsSession = {
      organizationId: "11111111-1111-1111-1111-111111111111",
      sessionNamespace: "reconnect-token-timeout",
      getAccessToken: () => token.promise,
    };
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const api = createOperationsApi(
      "https://api.synthetic.local",
      session,
      25,
    );
    const coordinator =
      new AuthoritativeRefreshCoordinator<OperationsDashboardResponse>();
    const apply = vi.fn();
    const onResynchronized = vi.fn();
    const onResynchronizationError = vi.fn();
    let state = "Conectada";
    buildManagedConnection("https://api.synthetic.local/hubs/operations", {
      baseUrl: "https://api.synthetic.local",
      tokenFactory: session.getAccessToken,
      resynchronizeFromRest: async () => {
        const response = await coordinator.request({
          requirement: "mandatory-reconnect",
          isCurrent: () => true,
          execute: (signal) => api.list({}, signal),
          apply,
        });
        return {
          aggregate_versions: Object.fromEntries(
            response.items.map((item) => [
              item.order_id,
              item.aggregate_version,
            ]),
          ),
        };
      },
      onReconnecting: () => {
        state = "Reconectando";
      },
      onResynchronized: () => {
        state = "Conectada";
        onResynchronized();
      },
      onResynchronizationError: (error) => {
        state = "Sin conexión";
        onResynchronizationError(error);
      },
    });

    signalr.reconnecting(new Error("controlled transport interruption"));
    const reconnect = signalr.reconnect();
    expect(state).toBe("Reconectando");
    expect(fetchMock).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(25);
    await reconnect;

    expect(onResynchronizationError).toHaveBeenCalledOnce();
    expect(onResynchronized).not.toHaveBeenCalled();
    expect(signalr.stop).toHaveBeenCalledOnce();
    expect(apply).not.toHaveBeenCalled();
    expect(state).toBe("Sin conexión");
    expect(fetchMock).not.toHaveBeenCalled();

    token.resolve("late-token");
    await Promise.resolve();
    expect(fetchMock).not.toHaveBeenCalled();
  });
});

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}
