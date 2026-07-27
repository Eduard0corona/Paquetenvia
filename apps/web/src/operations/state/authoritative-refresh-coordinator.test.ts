import { describe, expect, it, vi } from "vitest";
import {
  AuthoritativeRefreshCoordinator,
  RefreshScopeChangedError,
  type RefreshRequirement,
} from "./authoritative-refresh-coordinator";

interface Snapshot {
  readonly version: number;
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

function operation(
  requirement: RefreshRequirement,
  source: ReturnType<typeof deferred<Snapshot>>,
  applied: number[],
  isCurrent: () => boolean = () => true,
) {
  return {
    requirement,
    isCurrent,
    execute: vi.fn(() => source.promise),
    apply: vi.fn((snapshot: Snapshot) => applied.push(snapshot.version)),
  };
}

describe("AuthoritativeRefreshCoordinator", () => {
  it("waits for an earlier normal request and starts a distinct mandatory reconnect read", async () => {
    const coordinator = new AuthoritativeRefreshCoordinator<Snapshot>();
    const prior = deferred<Snapshot>();
    const mandatory = deferred<Snapshot>();
    const applied: number[] = [];
    const priorOperation = operation("normal", prior, applied);
    const mandatoryOperation = operation(
      "mandatory-reconnect",
      mandatory,
      applied,
    );

    const priorResult = coordinator.request(priorOperation);
    let connected = false;
    const reconnectResult = coordinator
      .request(mandatoryOperation)
      .then((result) => {
        connected = true;
        return result;
      });

    expect(priorOperation.execute).toHaveBeenCalledOnce();
    expect(mandatoryOperation.execute).not.toHaveBeenCalled();
    expect(connected).toBe(false);
    prior.resolve({ version: 4 });
    await expect(priorResult).resolves.toEqual({ version: 4 });
    await vi.waitFor(() =>
      expect(mandatoryOperation.execute).toHaveBeenCalledOnce(),
    );
    expect(connected).toBe(false);
    mandatory.resolve({ version: 6 });

    await expect(reconnectResult).resolves.toEqual({ version: 6 });
    expect(connected).toBe(true);
    expect(applied).toEqual([4, 6]);
  });

  it("returns the exact mandatory response instead of a stale applied snapshot", async () => {
    const coordinator = new AuthoritativeRefreshCoordinator<Snapshot>();
    const prior = deferred<Snapshot>();
    const mandatory = deferred<Snapshot>();
    const applied: number[] = [];

    const priorResult = coordinator.request(operation("normal", prior, applied));
    const guardVersions: number[] = [];
    const reconnectResult = coordinator
      .request(operation("mandatory-reconnect", mandatory, applied))
      .then((snapshot) => guardVersions.push(snapshot.version));
    prior.resolve({ version: 7 });
    await priorResult;
    mandatory.resolve({ version: 9 });

    await expect(reconnectResult).resolves.toBe(1);
    expect(applied.at(-1)).toBe(9);
    expect(guardVersions).toEqual([9]);
  });

  it("rejects a mandatory failure without reporting a successful reconnect", async () => {
    const coordinator = new AuthoritativeRefreshCoordinator<Snapshot>();
    const prior = deferred<Snapshot>();
    const mandatory = deferred<Snapshot>();
    let connected = false;
    let unavailable = false;

    const priorResult = coordinator.request(operation("normal", prior, []));
    const mandatoryOperation = operation(
      "mandatory-reconnect",
      mandatory,
      [],
    );
    const reconnect = coordinator.request(mandatoryOperation);
    prior.resolve({ version: 4 });
    await priorResult;
    await vi.waitFor(() =>
      expect(mandatoryOperation.execute).toHaveBeenCalledOnce(),
    );
    mandatory.reject(new Error("503"));
    try {
      await reconnect;
      connected = true;
    } catch {
      unavailable = true;
    }

    expect(connected).toBe(false);
    expect(unavailable).toBe(true);
  });

  it("rejects and does not apply when the session changes before the response", async () => {
    const coordinator = new AuthoritativeRefreshCoordinator<Snapshot>();
    const response = deferred<Snapshot>();
    const applied: number[] = [];
    let current = true;
    const result = coordinator.request(
      operation("mandatory-reconnect", response, applied, () => current),
    );

    current = false;
    coordinator.cancel();
    response.resolve({ version: 3 });

    await expect(result).rejects.toBeInstanceOf(RefreshScopeChangedError);
    expect(applied).toEqual([]);
  });

  it("requires a fresh detail and single-item projection pair", async () => {
    type DetailSnapshot = {
      readonly detailVersion: number;
      readonly projectionVersions: readonly number[];
    };
    const coordinator =
      new AuthoritativeRefreshCoordinator<DetailSnapshot>();
    const prior = deferred<DetailSnapshot>();
    const mandatory = deferred<DetailSnapshot>();
    const applied: DetailSnapshot[] = [];
    const makeOperation = (
      requirement: RefreshRequirement,
      source: ReturnType<typeof deferred<DetailSnapshot>>,
    ) => ({
      requirement,
      isCurrent: () => true,
      execute: async () => {
        const result = await source.promise;
        if (result.projectionVersions.length !== 1)
          throw new Error("The exact projection is required.");
        return result;
      },
      apply: (result: DetailSnapshot) => applied.push(result),
    });

    const priorResult = coordinator.request(makeOperation("normal", prior));
    const reconnectResult = coordinator.request(
      makeOperation("mandatory-reconnect", mandatory),
    );
    prior.resolve({ detailVersion: 4, projectionVersions: [4] });
    await priorResult;
    mandatory.resolve({ detailVersion: 6, projectionVersions: [6] });

    await expect(reconnectResult).resolves.toEqual({
      detailVersion: 6,
      projectionVersions: [6],
    });
    expect(applied).toEqual([
      { detailVersion: 4, projectionVersions: [4] },
      { detailVersion: 6, projectionVersions: [6] },
    ]);
  });

  it("rejects detail reconnect when either mandatory REST read fails", async () => {
    type DetailSnapshot = {
      readonly detailVersion: number;
      readonly projectionVersion: number;
    };
    const coordinator =
      new AuthoritativeRefreshCoordinator<DetailSnapshot>();
    const detail = deferred<number>();
    const projection = deferred<number>();
    const applied: DetailSnapshot[] = [];
    const reconnect = coordinator.request({
      requirement: "mandatory-reconnect",
      isCurrent: () => true,
      execute: async () => {
        const [detailVersion, projectionVersion] = await Promise.all([
          detail.promise,
          projection.promise,
        ]);
        return { detailVersion, projectionVersion };
      },
      apply: (result) => applied.push(result),
    });

    detail.resolve(8);
    projection.reject(new Error("projection 503"));

    await expect(reconnect).rejects.toThrow("projection 503");
    expect(applied).toEqual([]);
  });

  it("runs a fresh mandatory read for every reconnect", async () => {
    const coordinator = new AuthoritativeRefreshCoordinator<Snapshot>();
    const prior = deferred<Snapshot>();
    const firstReconnect = deferred<Snapshot>();
    const secondReconnect = deferred<Snapshot>();
    const applied: number[] = [];

    const priorResult = coordinator.request(operation("normal", prior, applied));
    const firstResult = coordinator.request(
      operation("mandatory-reconnect", firstReconnect, applied),
    );
    const secondResult = coordinator.request(
      operation("mandatory-reconnect", secondReconnect, applied),
    );
    prior.resolve({ version: 1 });
    await priorResult;
    firstReconnect.resolve({ version: 2 });
    await firstResult;
    secondReconnect.resolve({ version: 3 });

    await expect(secondResult).resolves.toEqual({ version: 3 });
    expect(applied).toEqual([1, 2, 3]);
  });

  it("coalesces normal signals to at most one additional request", async () => {
    const coordinator = new AuthoritativeRefreshCoordinator<Snapshot>();
    const first = deferred<Snapshot>();
    const extra = deferred<Snapshot>();
    const applied: number[] = [];
    const firstOperation = operation("normal", first, applied);
    const superseded = operation("normal", deferred<Snapshot>(), applied);
    const extraOperation = operation("normal", extra, applied);

    const firstResult = coordinator.request(firstOperation);
    const secondResult = coordinator.request(superseded);
    const thirdResult = coordinator.request(extraOperation);
    first.resolve({ version: 1 });
    await firstResult;
    await vi.waitFor(() =>
      expect(extraOperation.execute).toHaveBeenCalledOnce(),
    );
    expect(superseded.execute).not.toHaveBeenCalled();
    extra.resolve({ version: 2 });

    await expect(Promise.all([secondResult, thirdResult])).resolves.toEqual([
      { version: 2 },
      { version: 2 },
    ]);
    expect(applied).toEqual([1, 2]);
  });
});
