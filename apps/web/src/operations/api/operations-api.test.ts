import { afterEach, describe, expect, it, vi } from "vitest";
import {
  buildOperationsDashboardSearch,
  createOperationsApi,
} from "./operations-api";
import type { OperationsSession } from "../session/operations-session";

const session: OperationsSession = {
  organizationId: "11111111-1111-1111-1111-111111111111",
  sessionNamespace: "synthetic",
  getAccessToken: () => "ephemeral-token",
};

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("operations api", () => {
  it("builds canonical server filters without page size", () => {
    const search = buildOperationsDashboardSearch({
      status: "DELIVERING",
      serviceType: "URGENT",
      unassigned: true,
      cursor: "opaque",
    });
    expect(search.toString()).toBe(
      "status=DELIVERING&service_type=URGENT&unassigned=true&cursor=opaque",
    );
    expect(search.has("page_size")).toBe(false);
  });

  it("sends current bearer and tenant without credentials", async () => {
    const fetchMock = vi.fn().mockResolvedValue(dashboardResponse());
    vi.stubGlobal("fetch", fetchMock);

    await createOperationsApi("https://api.synthetic.test", session).list({});

    const [, init] = fetchMock.mock.calls[0] as [URL, RequestInit];
    expect(init.cache).toBe("no-store");
    expect(init.credentials).toBe("omit");
    expect(init.referrerPolicy).toBe("no-referrer");
    expect((init.headers as Record<string, string>).Authorization).toBe(
      "Bearer ephemeral-token",
    );
    expect((init.headers as Record<string, string>)["X-Organization-Id"]).toBe(
      session.organizationId,
    );
  });

  it("waits for an asynchronous token before starting exactly one request", async () => {
    const token = deferred<string>();
    const asyncSession: OperationsSession = {
      ...session,
      getAccessToken: vi.fn(() => token.promise),
    };
    const fetchMock = vi.fn().mockResolvedValue(dashboardResponse());
    vi.stubGlobal("fetch", fetchMock);

    const request = createOperationsApi(
      "https://api.synthetic.test",
      asyncSession,
    ).list({});
    expect(fetchMock).not.toHaveBeenCalled();

    token.resolve("async-ephemeral-token");
    await request;

    expect(fetchMock).toHaveBeenCalledOnce();
    const [, init] = fetchMock.mock.calls[0] as [URL, RequestInit];
    expect((init.headers as Record<string, string>).Authorization).toBe(
      "Bearer async-ephemeral-token",
    );
    expect((init.headers as Record<string, string>)["X-Organization-Id"]).toBe(
      session.organizationId,
    );
  });

  it("times out token acquisition and ignores a late token", async () => {
    vi.useFakeTimers();
    const token = deferred<string>();
    const pendingSession: OperationsSession = {
      ...session,
      getAccessToken: () => token.promise,
    };
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);

    const request = createOperationsApi(
      "https://api.synthetic.test",
      pendingSession,
      25,
    ).list({});
    const rejected = expect(request).rejects.toMatchObject({
      name: "TimeoutError",
    });

    await vi.advanceTimersByTimeAsync(25);
    await rejected;
    expect(fetchMock).not.toHaveBeenCalled();

    token.resolve("late-token");
    await Promise.resolve();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("cancels token acquisition immediately and ignores a late token", async () => {
    const token = deferred<string>();
    const pendingSession: OperationsSession = {
      ...session,
      getAccessToken: () => token.promise,
    };
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const controller = new AbortController();
    const request = createOperationsApi(
      "https://api.synthetic.test",
      pendingSession,
    ).getOrder(
      "66666666-6666-6666-6666-666666666666",
      controller.signal,
    );

    controller.abort();
    await expect(request).rejects.toMatchObject({ name: "AbortError" });
    expect(fetchMock).not.toHaveBeenCalled();

    token.resolve("late-token");
    await Promise.resolve();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("propagates token provider rejection without starting fetch", async () => {
    const token = deferred<string>();
    const rejectedSession: OperationsSession = {
      ...session,
      getAccessToken: () => token.promise,
    };
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const request = createOperationsApi(
      "https://api.synthetic.test",
      rejectedSession,
    ).list({});
    const rejected = expect(request).rejects.toThrow("token provider failed");

    token.reject(new Error("token provider failed"));

    await rejected;
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("rejects an already-aborted signal without acquiring a token", async () => {
    const getAccessToken = vi.fn(() => "must-not-be-read");
    const abortedSession: OperationsSession = {
      ...session,
      getAccessToken,
    };
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const controller = new AbortController();
    controller.abort();

    await expect(
      createOperationsApi(
        "https://api.synthetic.test",
        abortedSession,
      ).list({}, controller.signal),
    ).rejects.toMatchObject({ name: "AbortError" });
    expect(getAccessToken).not.toHaveBeenCalled();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it.each([
    [401, "unauthorized"],
    [403, "forbidden"],
    [404, "not_found"],
    [400, "invalid"],
    [503, "unavailable"],
  ] as const)("classifies HTTP %s", async (status, category) => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(new Response(null, { status })),
    );
    await expect(
      createOperationsApi("https://api.synthetic.test", session).list({}),
    ).rejects.toMatchObject({ category });
  });
});

function dashboardResponse(): Response {
  return new Response(
    JSON.stringify({
      generated_at: "2026-07-27T02:00:00Z",
      items: [],
      next_cursor: null,
    }),
    { headers: { "content-type": "application/json" } },
  );
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
