import { describe, expect, it, vi } from "vitest";
import { asUuid, type RealtimeEnvelope, type Uuid } from "./envelope";
import { realtimeEventTypes } from "./event-types";

const signalr = vi.hoisted(() => {
  let reconnectHandler: (() => Promise<void>) | undefined;
  const stop = vi.fn(async () => undefined);
  return {
    stop,
    start: vi.fn(async () => undefined),
    reconnectDelays: [] as number[],
    register(handler: () => Promise<void>) {
      reconnectHandler = handler;
    },
    async reconnect() {
      if (reconnectHandler === undefined) {
        throw new Error("Reconnect callback was not registered.");
      }

      await reconnectHandler();
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
        onreconnected: (handler: () => Promise<void>) => signalr.register(handler),
      };
    }
  },
}));

import { buildManagedConnection } from "./base-connection";

interface TestPayload {
  readonly value: string;
}

const aggregateId = asUuid("11111111-1111-1111-1111-111111111111");

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
    const built = buildManagedConnection("https://api.synthetic.local/hubs/tracking", {
      baseUrl: "https://api.synthetic.local",
      tokenFactory: async () => "ephemeral-token",
      resynchronizeFromRest: synchronize,
    });
    expect(built.guard.shouldApply(
      event("22222222-2222-2222-2222-222222222222", 1),
    )).toBe(true);

    await signalr.reconnect();

    expect(signalr.reconnectDelays).toEqual([0, 2_000, 10_000, 30_000]);
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
});
