import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { once } from "node:events";
import { createServer } from "node:net";
import { resolve } from "node:path";
import { HubConnectionState } from "@microsoft/signalr";
import { afterEach, describe, expect, it } from "vitest";
import { createOperationsConnection } from "./operations-connection";
import { asUuid } from "./envelope";

const organizationA = asUuid("11111111-1111-1111-1111-111111111111");
const organizationB = asUuid("22222222-2222-2222-2222-222222222222");
const aggregateId = asUuid("77777777-7777-7777-7777-777777777777");
const firstEventId = asUuid("10000000-0000-0000-0000-000000000001");
const lowerEventId = asUuid("10000000-0000-0000-0000-000000000004");
const currentEventId = asUuid("10000000-0000-0000-0000-000000000006");
const higherEventId = asUuid("10000000-0000-0000-0000-000000000007");
const crossTenantEventId = asUuid("10000000-0000-0000-0000-000000000099");
const hosts = new Set<ControlledHost>();
const diagnosticsEnabled = process.env.PAQUETERIA_REALTIME_DIAGNOSTICS === "1";

class DiagnosticTrace {
  private readonly startedAtUnixMilliseconds = Date.now();
  private readonly startedAtMonotonic = process.hrtime.bigint();
  private readonly repetition = process.env.PAQUETERIA_DIAGNOSTIC_REPETITION ?? "unknown";
  private readonly correlationId: string;

  public constructor(private readonly testcase: string) {
    this.correlationId =
      `${this.repetition}:${this.testcase}:${process.pid}:${this.startedAtUnixMilliseconds}`;
  }

  public emit(event: string, details: Readonly<Record<string, unknown>> = {}): void {
    if (!diagnosticsEnabled) return;
    const elapsedNanoseconds = process.hrtime.bigint() - this.startedAtMonotonic;
    process.stderr.write(
      `RTDIAG ${JSON.stringify({
        schema: "paquetenvia-realtime-correlation-v1",
        repetition: this.repetition,
        testcase: this.testcase,
        process_role: "client-test",
        pid: process.pid,
        correlation_id: this.correlationId,
        utc: new Date().toISOString(),
        elapsed_ms: Number(elapsedNanoseconds) / 1_000_000,
        event,
        ...details,
      })}\n`,
    );
  }

  public childEnvironment(): Readonly<Record<string, string>> {
    return {
      PAQUETERIA_REALTIME_DIAGNOSTICS: "1",
      PAQUETERIA_DIAGNOSTIC_REPETITION: this.repetition,
      PAQUETERIA_DIAGNOSTIC_TESTCASE: this.testcase,
      PAQUETERIA_DIAGNOSTIC_CORRELATION_ID: this.correlationId,
      PAQUETERIA_DIAGNOSTIC_TEST_STARTED_UNIX_MS:
        this.startedAtUnixMilliseconds.toString(),
    };
  }
}

function errorDetails(error?: Error): Readonly<Record<string, unknown>> {
  return {
    error_type: error?.name ?? null,
    error_message: error?.message ?? null,
  };
}

describe("real managed SignalR reconnect", () => {
  afterEach(async () => {
    await Promise.all([...hosts].map(async (host) => host.stop()));
    hosts.clear();
  });

  it("observes reconnect lifecycle, restores REST state, recovers groups and deduplicates", async () => {
    const trace = new DiagnosticTrace("reconnect-contract");
    trace.emit("testcase_begin");
    trace.emit("port_reservation_begin");
    const port = await reservePort();
    trace.emit("port_reservation_complete", { port });
    let host = await startHost(port, 1, trace);
    hosts.add(host);
    const baseUrl = `http://127.0.0.1:${port}`;
    const lifecycle: string[] = [];
    const appliedVersions: number[] = [];
    let localVersion = 0;
    let tokenFactoryCount = 0;
    let restSynchronizationCount = 0;
    const reconnecting = deferred<void>();
    const reconnected = deferred<void>();
    const synchronized = deferred<void>();
    const initialApplied = deferred<void>();
    const currentApplied = deferred<void>();
    const higherApplied = deferred<void>();

    trace.emit("connection_object_creation_begin");
    const connection = createOperationsConnection(
      {
        baseUrl,
        organizationId: organizationA,
        tokenFactory: async () => {
          tokenFactoryCount += 1;
          trace.emit("token_factory_invoked", { invocation: tokenFactoryCount });
          return "synthetic-dispatcher-token";
        },
        onReconnecting: (error) => {
          trace.emit("client_onreconnecting_entry", {
            state: connection.state,
            connection_id: connection.connectionId,
            ...errorDetails(error),
          });
          lifecycle.push("Reconnecting");
          reconnecting.resolve();
          queueMicrotask(() => {
            trace.emit("client_onreconnecting_microtask", {
              state: connection.state,
              connection_id: connection.connectionId,
            });
          });
          setImmediate(() => {
            trace.emit("client_onreconnecting_next_turn", {
              state: connection.state,
              connection_id: connection.connectionId,
            });
          });
        },
        onReconnected: (connectionId) => {
          trace.emit("client_onreconnected_entry", {
            state: connection.state,
            connection_id: connectionId ?? connection.connectionId,
          });
          lifecycle.push("Reconnected");
          reconnected.resolve();
        },
        onClosed: (error) => {
          trace.emit("client_onclose", {
            state: connection.state,
            connection_id: connection.connectionId,
            ...errorDetails(error),
          });
        },
        resynchronizeFromRest: async () => {
          restSynchronizationCount += 1;
          trace.emit("rest_resynchronization_begin", {
            invocation: restSynchronizationCount,
          });
          const snapshot = await getSnapshot(baseUrl, trace);
          localVersion = snapshot.aggregate_versions[aggregateId] ?? 0;
          trace.emit("rest_resynchronization_complete", {
            invocation: restSynchronizationCount,
            local_version: localVersion,
          });
          synchronized.resolve();
          return snapshot;
        },
      },
      {
        OrderStatusChanged: (event) => {
          trace.emit("client_event_received", {
            event_type: "OrderStatusChanged",
            event_id: event.event_id,
            aggregate_version: event.aggregate_version,
            connection_id: connection.connectionId,
          });
          appliedVersions.push(event.aggregate_version);
          localVersion = event.aggregate_version;
          if (event.aggregate_version === 1) initialApplied.resolve();
          if (event.aggregate_version === 6) currentApplied.resolve();
          if (event.aggregate_version === 7) higherApplied.resolve();
        },
      },
    );
    trace.emit("connection_object_creation_complete", {
      state: connection.state,
      connection_id: connection.connectionId,
    });

    try {
      trace.emit("client_start_begin", {
        state: connection.state,
        connection_id: connection.connectionId,
      });
      await connection.start();
      trace.emit("client_start_complete", {
        state: connection.state,
        connection_id: connection.connectionId,
      });
      expect(connection.state).toBe(HubConnectionState.Connected);
      await publish(baseUrl, organizationA, 1, firstEventId, trace, "initial");
      await initialApplied.promiseWithTimeout(5_000);
      trace.emit("initial_event_wait_complete", { local_version: localVersion });
      expect(localVersion).toBe(1);
      await expectStats(baseUrl, 1, "WebSockets");
      trace.emit("initial_transport_stats_verified");

      await host.stop();
      hosts.delete(host);
      trace.emit("old_host_stop_complete", {
        state: connection.state,
        connection_id: connection.connectionId,
      });
      await reconnecting.promiseWithTimeout(10_000);
      trace.emit("reconnecting_callback_wait_complete", {
        state: connection.state,
        connection_id: connection.connectionId,
      });
      let lastObservedReconnectState: HubConnectionState | undefined;
      await waitFor(
        () => {
          const state = connection.state;
          if (state !== lastObservedReconnectState) {
            lastObservedReconnectState = state;
            trace.emit("reconnecting_assertion_state_transition", {
              state,
              connection_id: connection.connectionId,
            });
          }
          return state === HubConnectionState.Reconnecting;
        },
        10_000,
        "connection to remain reconnecting after the old host exits",
      );
      trace.emit("reconnecting_state_assertion_complete", {
        state: connection.state,
        connection_id: connection.connectionId,
      });
      expect(connection.state).toBe(HubConnectionState.Reconnecting);

      host = await startHost(port, 5, trace);
      hosts.add(host);
      await reconnected.promiseWithTimeout(15_000);
      trace.emit("reconnected_callback_wait_complete", {
        state: connection.state,
        connection_id: connection.connectionId,
      });
      await synchronized.promiseWithTimeout(5_000);
      trace.emit("rest_resynchronization_wait_complete", {
        state: connection.state,
        connection_id: connection.connectionId,
        local_version: localVersion,
      });
      await waitFor(
        () =>
          connection.state === HubConnectionState.Connected &&
          localVersion === 5,
        5_000,
        "managed connection to report its recovered state and REST snapshot",
      );
      trace.emit("recovered_state_assertion_complete", {
        state: connection.state,
        connection_id: connection.connectionId,
        local_version: localVersion,
      });
      expect(connection.state).toBe(HubConnectionState.Connected);
      expect(lifecycle.length).toBeGreaterThanOrEqual(2);
      expect(lifecycle.length % 2).toBe(0);
      for (let index = 0; index < lifecycle.length; index += 2) {
        expect(lifecycle.slice(index, index + 2)).toEqual([
          "Reconnecting",
          "Reconnected",
        ]);
      }
      expect(tokenFactoryCount).toBeGreaterThanOrEqual(2);
      expect(restSynchronizationCount).toBe(lifecycle.length / 2);
      expect(localVersion).toBe(5);
      await expectStats(baseUrl, 1, "WebSockets");

      await publish(baseUrl, organizationB, 99, crossTenantEventId, trace, "cross-tenant");
      await publish(baseUrl, organizationA, 4, lowerEventId, trace, "lower-version");
      await delay(300);
      expect(appliedVersions).toEqual([1]);

      await publish(baseUrl, organizationA, 6, currentEventId, trace, "post-reconnect-current");
      await currentApplied.promiseWithTimeout(5_000);
      trace.emit("post_reconnect_current_event_wait_complete");
      await publish(baseUrl, organizationA, 6, currentEventId, trace, "duplicate");
      await publish(baseUrl, organizationA, 7, higherEventId, trace, "post-reconnect-higher");
      await higherApplied.promiseWithTimeout(5_000);
      trace.emit("post_reconnect_higher_event_wait_complete");
      await delay(200);

      expect(appliedVersions).toEqual([1, 6, 7]);
      expect(localVersion).toBe(7);
    } finally {
      trace.emit("client_stop_begin", {
        state: connection.state,
        connection_id: connection.connectionId,
      });
      await connection.stop();
      trace.emit("client_stop_complete", {
        state: connection.state,
        connection_id: connection.connectionId,
      });
      trace.emit("testcase_end");
    }
  }, 45_000);

  it("stops fail-closed when the real reconnected callback cannot resynchronize", async () => {
    const trace = new DiagnosticTrace("resync-fail-closed");
    trace.emit("testcase_begin");
    trace.emit("port_reservation_begin");
    const port = await reservePort();
    trace.emit("port_reservation_complete", { port });
    let host = await startHost(port, 1, trace);
    hosts.add(host);
    const baseUrl = `http://127.0.0.1:${port}`;
    const reconnecting = deferred<void>();
    const reconnected = deferred<void>();
    const synchronizationError = deferred<unknown>();
    trace.emit("connection_object_creation_begin");
    const connection = createOperationsConnection(
      {
        baseUrl,
        organizationId: organizationA,
        tokenFactory: async () => {
          trace.emit("token_factory_invoked");
          return "synthetic-dispatcher-token";
        },
        onReconnecting: (error) => {
          trace.emit("client_onreconnecting_entry", {
            state: connection.state,
            connection_id: connection.connectionId,
            ...errorDetails(error),
          });
          reconnecting.resolve();
          queueMicrotask(() => {
            trace.emit("client_onreconnecting_microtask", {
              state: connection.state,
              connection_id: connection.connectionId,
            });
          });
          setImmediate(() => {
            trace.emit("client_onreconnecting_next_turn", {
              state: connection.state,
              connection_id: connection.connectionId,
            });
          });
        },
        onReconnected: (connectionId) => {
          trace.emit("client_onreconnected_entry", {
            state: connection.state,
            connection_id: connectionId ?? connection.connectionId,
          });
          reconnected.resolve();
        },
        onClosed: (error) => {
          trace.emit("client_onclose", {
            state: connection.state,
            connection_id: connection.connectionId,
            ...errorDetails(error),
          });
        },
        resynchronizeFromRest: async () => {
          trace.emit("rest_resynchronization_begin");
          trace.emit("rest_resynchronization_controlled_failure", {
            error_type: "Error",
            error_message: "controlled REST outage",
          });
          throw new Error("controlled REST outage");
        },
        onResynchronizationError: (error) => {
          trace.emit("rest_resynchronization_error_observed", {
            error_type: error instanceof Error ? error.name : typeof error,
            error_message: error instanceof Error ? error.message : String(error),
          });
          synchronizationError.resolve(error);
        },
      },
      {},
    );
    trace.emit("connection_object_creation_complete", {
      state: connection.state,
      connection_id: connection.connectionId,
    });

    try {
      trace.emit("client_start_begin", {
        state: connection.state,
        connection_id: connection.connectionId,
      });
      await connection.start();
      trace.emit("client_start_complete", {
        state: connection.state,
        connection_id: connection.connectionId,
      });
      await host.stop();
      hosts.delete(host);
      trace.emit("old_host_stop_complete", {
        state: connection.state,
        connection_id: connection.connectionId,
      });
      await reconnecting.promiseWithTimeout(10_000);
      trace.emit("reconnecting_callback_wait_complete", {
        state: connection.state,
        connection_id: connection.connectionId,
      });

      host = await startHost(port, 2, trace);
      hosts.add(host);
      await reconnected.promiseWithTimeout(15_000);
      trace.emit("reconnected_callback_wait_complete", {
        state: connection.state,
        connection_id: connection.connectionId,
      });
      const error = await synchronizationError.promiseWithTimeout(5_000);
      trace.emit("rest_resynchronization_error_wait_complete");
      expect(error).toBeInstanceOf(Error);
      await waitFor(
        () => connection.state === HubConnectionState.Disconnected,
        5_000,
        "managed connection to stop after REST sync failure",
      );
      trace.emit("disconnected_state_assertion_complete", {
        state: connection.state,
        connection_id: connection.connectionId,
      });
      expect(connection.state).toBe(HubConnectionState.Disconnected);
    } finally {
      trace.emit("client_stop_begin", {
        state: connection.state,
        connection_id: connection.connectionId,
      });
      await connection.stop();
      trace.emit("client_stop_complete", {
        state: connection.state,
        connection_id: connection.connectionId,
      });
      trace.emit("testcase_end");
    }
  }, 45_000);
});

interface Snapshot {
  readonly aggregate_versions: Readonly<Record<string, number>>;
}

interface Stats {
  readonly authorization_count: number;
  readonly transport: string;
}

class ControlledHost {
  private readonly output: string[] = [];
  private stopped = false;

  public constructor(
    private readonly process: ChildProcessWithoutNullStreams,
    private readonly trace: DiagnosticTrace,
  ) {
    const capture = (data: Buffer): void => {
      const text = data.toString();
      this.output.push(text);
      if (diagnosticsEnabled) globalThis.process.stderr.write(text);
    };
    process.stdout.on("data", capture);
    process.stderr.on("data", capture);
    process.once("exit", (code, signal) => {
      trace.emit("host_process_exit_observed", {
        host_pid: process.pid,
        exit_code: code,
        exit_signal: signal,
      });
    });
  }

  public diagnostics(): string {
    return this.output.join("").slice(-4_000);
  }

  public async stop(): Promise<void> {
    if (this.stopped) return;
    this.stopped = true;
    this.trace.emit("host_stop_request", {
      host_pid: this.process.pid,
      exit_code: this.process.exitCode,
      exit_signal: this.process.signalCode,
    });
    if (this.process.exitCode === null) {
      const signalAccepted = this.process.kill();
      this.trace.emit("host_stop_signal_sent", {
        host_pid: this.process.pid,
        signal: "SIGTERM",
        signal_accepted: signalAccepted,
      });
      await Promise.race([
        once(this.process, "exit"),
        delay(5_000).then(() => {
          if (this.process.exitCode === null) {
            const killAccepted = this.process.kill("SIGKILL");
            this.trace.emit("host_force_kill_sent", {
              host_pid: this.process.pid,
              signal: "SIGKILL",
              signal_accepted: killAccepted,
            });
          }
        }),
      ]);
    }
    this.trace.emit("host_stop_wait_complete", {
      host_pid: this.process.pid,
      exit_code: this.process.exitCode,
      exit_signal: this.process.signalCode,
    });
  }
}

async function startHost(
  port: number,
  snapshotVersion: number,
  trace: DiagnosticTrace,
): Promise<ControlledHost> {
  const hostDll =
    process.env.PAQUETERIA_TEST_HOST_DLL ??
    resolve(
      process.cwd(),
      "../../tests/Paqueteria.RealtimeTestHost/bin/Debug/net10.0/Paqueteria.RealtimeTestHost.dll",
    );
  trace.emit("host_spawn_request", { port, snapshot_version: snapshotVersion });
  const child = spawn(
    "dotnet",
    [hostDll, "--urls", `http://127.0.0.1:${port}`],
    {
      env: {
        ...process.env,
        ASPNETCORE_ENVIRONMENT: "Testing",
        DOTNET_NOLOGO: "1",
        PAQUETERIA_TEST_SNAPSHOT_VERSION: snapshotVersion.toString(),
        ...trace.childEnvironment(),
      },
      stdio: ["pipe", "pipe", "pipe"],
    },
  );
  trace.emit("host_spawned", {
    host_pid: child.pid,
    port,
    snapshot_version: snapshotVersion,
  });
  const host = new ControlledHost(child, trace);
  try {
    trace.emit("host_health_polling_begin", { host_pid: child.pid, port });
    await waitFor(
      async () => {
        if (child.exitCode !== null) {
          throw new Error(`Test host exited early.\n${host.diagnostics()}`);
        }
        try {
          return (await fetch(`http://127.0.0.1:${port}/__test/health`)).ok;
        } catch {
          return false;
        }
      },
      15_000,
      "controlled Kestrel host to become ready",
    );
    trace.emit("host_health_success", { host_pid: child.pid, port });
    return host;
  } catch (error) {
    await host.stop();
    throw error;
  }
}

async function publish(
  baseUrl: string,
  organizationId: string,
  version: number,
  eventId: string,
  trace: DiagnosticTrace,
  phase: string,
): Promise<void> {
  trace.emit("publish_request_begin", {
    phase,
    organization_id: organizationId,
    aggregate_version: version,
    event_id: eventId,
  });
  const response = await fetch(
    `${baseUrl}/__test/publish/${organizationId}/${version}/${eventId}`,
    { method: "POST" },
  );
  trace.emit("publish_response_received", {
    phase,
    organization_id: organizationId,
    aggregate_version: version,
    event_id: eventId,
    response_status: response.status,
  });
  if (!response.ok) throw new Error(`Publish failed with HTTP ${response.status}.`);
}

async function getSnapshot(baseUrl: string, trace: DiagnosticTrace): Promise<Snapshot> {
  const url = `${baseUrl}/__test/snapshot`;
  trace.emit("rest_snapshot_request_begin", { url_path: "/__test/snapshot" });
  const response = await fetch(url);
  trace.emit("rest_snapshot_response_received", {
    url_path: "/__test/snapshot",
    response_status: response.status,
  });
  if (!response.ok) throw new Error(`GET ${url} failed with HTTP ${response.status}.`);
  const snapshot = (await response.json()) as Snapshot;
  trace.emit("rest_snapshot_response_parsed");
  return snapshot;
}

async function expectStats(
  baseUrl: string,
  authorizationCount: number,
  transport: string,
): Promise<void> {
  await waitFor(
    async () => {
      const stats = await getJson<Stats>(`${baseUrl}/__test/stats`);
      return (
        stats.authorization_count === authorizationCount &&
        stats.transport === transport
      );
    },
    5_000,
    "authorization and WebSocket transport evidence",
  );
}

async function getJson<T>(url: string): Promise<T> {
  const response = await fetch(url);
  if (!response.ok) throw new Error(`GET ${url} failed with HTTP ${response.status}.`);
  return (await response.json()) as T;
}

async function reservePort(): Promise<number> {
  const server = createServer();
  server.unref();
  await new Promise<void>((resolveListen, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolveListen);
  });
  const address = server.address();
  if (address === null || typeof address === "string") {
    server.close();
    throw new Error("Could not reserve an ephemeral TCP port.");
  }
  await new Promise<void>((resolveClose, reject) =>
    server.close((error) => (error ? reject(error) : resolveClose())),
  );
  return address.port;
}

function deferred<T = void>(): {
  readonly resolve: (value: T) => void;
  readonly promiseWithTimeout: (milliseconds: number) => Promise<T>;
} {
  let resolvePromise!: (value: T) => void;
  const promise = new Promise<T>((resolveValue) => {
    resolvePromise = resolveValue;
  });
  return {
    resolve: resolvePromise,
    promiseWithTimeout: (milliseconds) =>
      Promise.race([
        promise,
        delay(milliseconds).then(() => {
          throw new Error(`Signal was not observed within ${milliseconds} ms.`);
        }),
      ]),
  };
}

async function waitFor(
  condition: () => boolean | Promise<boolean>,
  timeoutMilliseconds: number,
  description: string,
): Promise<void> {
  const deadline = Date.now() + timeoutMilliseconds;
  while (Date.now() < deadline) {
    if (await condition()) return;
    await delay(50);
  }
  throw new Error(`Timed out waiting for ${description}.`);
}

function delay(milliseconds: number): Promise<void> {
  return new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds));
}
