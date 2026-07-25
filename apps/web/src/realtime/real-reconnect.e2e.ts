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
const reconnectDelays = [0, 250, 500, 1_000, 2_000] as const;
const hosts = new Set<ControlledHost>();

describe("real managed SignalR reconnect", () => {
  afterEach(async () => {
    await Promise.all([...hosts].map(async (host) => host.stop()));
    hosts.clear();
  });

  it("observes reconnect lifecycle, restores REST state, recovers groups and deduplicates", async () => {
    const port = await reservePort();
    let host = await startHost(port, 1);
    hosts.add(host);
    const baseUrl = `http://127.0.0.1:${port}`;
    const lifecycle: string[] = [];
    const appliedVersions: number[] = [];
    let localVersion = 0;
    let tokenFactoryCount = 0;
    let restSynchronizationCount = 0;
    let reconnectingState: HubConnectionState | undefined;
    const connectionReference: {
      current?: ReturnType<typeof createOperationsConnection>;
    } = {};
    const reconnecting = deferred<void>();
    const reconnected = deferred<void>();
    const synchronized = deferred<void>();
    const initialApplied = deferred<void>();
    const currentApplied = deferred<void>();
    const higherApplied = deferred<void>();

    const connection = createOperationsConnection(
      {
        baseUrl,
        organizationId: organizationA,
        tokenFactory: async () => {
          tokenFactoryCount += 1;
          return "synthetic-dispatcher-token";
        },
        reconnectDelaysMilliseconds: reconnectDelays,
        onReconnecting: () => {
          reconnectingState = connectionReference.current?.state;
          lifecycle.push("Reconnecting");
          reconnecting.resolve();
        },
        onReconnected: () => {
          lifecycle.push("Reconnected");
          reconnected.resolve();
        },
        resynchronizeFromRest: async () => {
          restSynchronizationCount += 1;
          const snapshot = await getJson<Snapshot>(`${baseUrl}/__test/snapshot`);
          localVersion = snapshot.aggregate_versions[aggregateId] ?? 0;
          synchronized.resolve();
          return snapshot;
        },
      },
      {
        OrderStatusChanged: (event) => {
          appliedVersions.push(event.aggregate_version);
          localVersion = event.aggregate_version;
          if (event.aggregate_version === 1) initialApplied.resolve();
          if (event.aggregate_version === 6) currentApplied.resolve();
          if (event.aggregate_version === 7) higherApplied.resolve();
        },
      },
    );
    connectionReference.current = connection;

    try {
      await connection.start();
      expect(connection.state).toBe(HubConnectionState.Connected);
      await publish(baseUrl, organizationA, 1, firstEventId);
      await initialApplied.promiseWithTimeout(5_000);
      expect(localVersion).toBe(1);
      await expectStats(baseUrl, 1, "WebSockets");

      await host.stop();
      hosts.delete(host);
      await reconnecting.promiseWithTimeout(10_000);
      expect(reconnectingState).toBe(HubConnectionState.Reconnecting);

      host = await startHost(port, 5);
      hosts.add(host);
      await reconnected.promiseWithTimeout(15_000);
      await synchronized.promiseWithTimeout(5_000);
      await waitFor(
        () => connection.state === HubConnectionState.Connected,
        5_000,
        "managed connection to report its recovered state",
      );
      expect(connection.state).toBe(HubConnectionState.Connected);
      expect(lifecycle).toEqual(["Reconnecting", "Reconnected"]);
      expect(tokenFactoryCount).toBeGreaterThanOrEqual(2);
      expect(restSynchronizationCount).toBe(1);
      expect(localVersion).toBe(5);
      await expectStats(baseUrl, 1, "WebSockets");

      await publish(baseUrl, organizationB, 99, crossTenantEventId);
      await publish(baseUrl, organizationA, 4, lowerEventId);
      await delay(300);
      expect(appliedVersions).toEqual([1]);

      await publish(baseUrl, organizationA, 6, currentEventId);
      await currentApplied.promiseWithTimeout(5_000);
      await publish(baseUrl, organizationA, 6, currentEventId);
      await publish(baseUrl, organizationA, 7, higherEventId);
      await higherApplied.promiseWithTimeout(5_000);
      await delay(200);

      expect(appliedVersions).toEqual([1, 6, 7]);
      expect(localVersion).toBe(7);
    } finally {
      await connection.stop();
    }
  }, 45_000);

  it("stops fail-closed when the real reconnected callback cannot resynchronize", async () => {
    const port = await reservePort();
    let host = await startHost(port, 1);
    hosts.add(host);
    const baseUrl = `http://127.0.0.1:${port}`;
    const reconnecting = deferred<void>();
    const reconnected = deferred<void>();
    const synchronizationError = deferred<unknown>();
    const connection = createOperationsConnection(
      {
        baseUrl,
        organizationId: organizationA,
        tokenFactory: async () => "synthetic-dispatcher-token",
        reconnectDelaysMilliseconds: reconnectDelays,
        onReconnecting: () => reconnecting.resolve(),
        onReconnected: () => reconnected.resolve(),
        resynchronizeFromRest: async () => {
          throw new Error("controlled REST outage");
        },
        onResynchronizationError: (error) => synchronizationError.resolve(error),
      },
      {},
    );

    try {
      await connection.start();
      await host.stop();
      hosts.delete(host);
      await reconnecting.promiseWithTimeout(10_000);

      host = await startHost(port, 2);
      hosts.add(host);
      await reconnected.promiseWithTimeout(15_000);
      const error = await synchronizationError.promiseWithTimeout(5_000);
      expect(error).toBeInstanceOf(Error);
      await waitFor(
        () => connection.state === HubConnectionState.Disconnected,
        5_000,
        "managed connection to stop after REST sync failure",
      );
      expect(connection.state).toBe(HubConnectionState.Disconnected);
    } finally {
      await connection.stop();
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

  public constructor(private readonly process: ChildProcessWithoutNullStreams) {
    process.stdout.on("data", (data: Buffer) => this.output.push(data.toString()));
    process.stderr.on("data", (data: Buffer) => this.output.push(data.toString()));
  }

  public diagnostics(): string {
    return this.output.join("").slice(-4_000);
  }

  public async stop(): Promise<void> {
    if (this.stopped) return;
    this.stopped = true;
    if (this.process.exitCode === null) {
      this.process.kill();
      await Promise.race([
        once(this.process, "exit"),
        delay(5_000).then(() => {
          if (this.process.exitCode === null) this.process.kill("SIGKILL");
        }),
      ]);
    }
  }
}

async function startHost(port: number, snapshotVersion: number): Promise<ControlledHost> {
  const hostDll =
    process.env.PAQUETERIA_TEST_HOST_DLL ??
    resolve(
      process.cwd(),
      "../../tests/Paqueteria.RealtimeTestHost/bin/Debug/net10.0/Paqueteria.RealtimeTestHost.dll",
    );
  const child = spawn(
    "dotnet",
    [hostDll, "--urls", `http://127.0.0.1:${port}`],
    {
      env: {
        ...process.env,
        ASPNETCORE_ENVIRONMENT: "Testing",
        DOTNET_NOLOGO: "1",
        PAQUETERIA_TEST_SNAPSHOT_VERSION: snapshotVersion.toString(),
      },
      stdio: ["pipe", "pipe", "pipe"],
    },
  );
  const host = new ControlledHost(child);
  try {
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
): Promise<void> {
  const response = await fetch(
    `${baseUrl}/__test/publish/${organizationId}/${version}/${eventId}`,
    { method: "POST" },
  );
  if (!response.ok) throw new Error(`Publish failed with HTTP ${response.status}.`);
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
