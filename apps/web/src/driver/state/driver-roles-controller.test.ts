import { describe, expect, it, vi } from "vitest";
import {
  createDriverOrganizationRolesApi,
  type DriverOrganizationRolesApi,
} from "../api/organization-roles-api";
import { createDriverCachePartition } from "../cache/driver-stops-cache";
import { driverReportRoleOf } from "../contracts/organization-roles";
import type { DriverSession } from "../session/driver-session";
import {
  DriverRolesController,
  DriverRolesMaximumRetries,
} from "./driver-roles-controller";
import {
  MemoryRolesCache,
  myOrganizationRolesAnswer,
  olderApiAnswer,
  rolesFetch,
  testWindow,
  type RolesAnswer,
} from "./driver-roles.fixtures";

/**
 * ORG-MY-ROLES-2026-10-12: the roles come from getMyOrganizationRoles when the
 * session starts; the answer saved for the partition keeps the report working
 * offline, a failed call keeps it and is asked again when the connection
 * returns or the app is shown again, and 403 removes it for good (a 401 keeps it).
 */
const session: DriverSession = {
  organizationId: "33333333-3333-4333-8333-333333333333",
  cacheNamespace: "opaque-driver-roles-session",
  getAccessToken: () => "memory-only",
};

function response(answer: RolesAnswer): Response {
  return new Response(answer.body, {
    status: answer.status,
    headers: { "Content-Type": answer.contentType ?? "application/json; charset=utf-8" },
  });
}

/** Each call takes the next answer; the last one repeats. */
function answersFetch(...answers: (RolesAnswer | Error)[]) {
  let call = 0;
  return vi.fn<(input: RequestInfo | URL, init?: RequestInit) => Promise<Response>>(async () => {
    const answer = answers[Math.min(call, answers.length - 1)];
    call += 1;
    if (answer instanceof Error) throw answer;
    return response(answer);
  });
}

function controllerFor(
  answer: RolesAnswer | Error | ReturnType<typeof answersFetch>,
  options: { readonly cache?: MemoryRolesCache; readonly page?: ReturnType<typeof testWindow> } = {},
) {
  const fetcher =
    typeof answer === "function"
      ? answer
      : answer instanceof Error
        ? answersFetch(answer)
        : rolesFetch(answer);
  const cache = options.cache ?? new MemoryRolesCache();
  const page = options.page ?? testWindow();
  const controller = new DriverRolesController({
    session,
    api: createDriverOrganizationRolesApi({
      baseUrl: "https://api.synthetic.test",
      session,
      fetch: fetcher as unknown as typeof fetch,
    }),
    cache,
    window: page.window,
  });
  return { controller, cache, fetcher, page };
}

async function savedFor(cache: MemoryRolesCache, roles: readonly string[]) {
  cache.saved.set((await createDriverCachePartition(session)).key, roles);
}

describe("organization roles of the driver session", () => {
  it("are loading until an answer is known, then come from the API and are saved for the partition", async () => {
    const { controller, cache, fetcher } = controllerFor(myOrganizationRolesAnswer("DRIVER"));
    expect(controller.current).toEqual({ roles: null, loading: true });
    expect(driverReportRoleOf(controller.current)).toBe("loading");

    await controller.start();

    expect(fetcher).toHaveBeenCalledOnce();
    expect(controller.current).toEqual({ roles: ["DRIVER"], loading: false });
    expect([...cache.saved.values()]).toEqual([["DRIVER"]]);
    expect(driverReportRoleOf(controller.current)).toBe("driver");
  });

  it("read as loading, never as unknown, while the first request after a sign-in is on its way", async () => {
    const pending: { answer?: (response: Response) => void } = {};
    const api: DriverOrganizationRolesApi = createDriverOrganizationRolesApi({
      baseUrl: "https://api.synthetic.test",
      session,
      fetch: (() =>
        new Promise<Response>((resolve) => {
          pending.answer = resolve;
        })) as unknown as typeof fetch,
    });
    const controller = new DriverRolesController({
      session,
      api,
      cache: new MemoryRolesCache(),
      window: testWindow().window,
    });
    const seen: ReturnType<typeof driverReportRoleOf>[] = [];
    controller.subscribe((state) => seen.push(driverReportRoleOf(state)));

    const started = controller.start();
    await vi.waitFor(() => expect(pending.answer).toBeDefined());
    expect(driverReportRoleOf(controller.current)).toBe("loading");

    pending.answer?.(response(myOrganizationRolesAnswer("DRIVER")));
    await started;

    expect(seen).toEqual(["loading", "driver"]);
    expect(seen).not.toContain("unknown");
  });

  it("use the saved answer at once and replace it with the new one", async () => {
    const cache = new MemoryRolesCache();
    await savedFor(cache, ["DRIVER"]);
    const { controller } = controllerFor(myOrganizationRolesAnswer("DRIVER", "DISPATCHER"), { cache });
    const seen: (readonly string[] | null)[] = [];
    controller.subscribe((state) => seen.push(state.roles));

    await controller.start();

    expect(seen).toEqual([null, ["DRIVER"], ["DISPATCHER", "DRIVER"]]);
    expect([...cache.saved.values()]).toEqual([["DISPATCHER", "DRIVER"]]);
    expect(driverReportRoleOf(controller.current)).toBe("dispatch");
  });

  it.each<[string, RolesAnswer | Error]>([
    ["offline", new TypeError("Failed to fetch")],
    ["a 503", { status: 503, body: "{}" }],
    ["an answer that breaks the contract", { status: 200, body: JSON.stringify({ roles: ["DRIVER"], extra: 1 }) }],
    ["an older API without the operation (404)", olderApiAnswer],
  ])("keep the saved answer when the call fails (%s): the report keeps working offline", async (_name, answer) => {
    const cache = new MemoryRolesCache();
    await savedFor(cache, ["DRIVER"]);
    const { controller } = controllerFor(answer, { cache });

    await controller.start();

    expect(controller.current).toEqual({ roles: ["DRIVER"], loading: false });
    expect([...cache.saved.values()]).toEqual([["DRIVER"]]);
    expect(driverReportRoleOf(controller.current)).toBe("driver");
  });

  it("stay unknown against an older API (404) with nothing saved: the report fails closed, nothing else changes", async () => {
    const { controller, cache } = controllerFor(olderApiAnswer);

    await controller.start();

    expect(controller.current).toEqual({ roles: null, loading: false });
    expect(cache.saved.size).toBe(0);
    expect(driverReportRoleOf(controller.current)).toBe("unknown");
  });

  it("ask again when the connection returns after a failed first request, and then report", async () => {
    const fetcher = answersFetch(new TypeError("Failed to fetch"), myOrganizationRolesAnswer("DRIVER"));
    const page = testWindow({ online: false });
    const { controller, cache } = controllerFor(fetcher, { page });

    await controller.start();
    expect(driverReportRoleOf(controller.current)).toBe("unknown");

    // Shown again while still offline: nothing is asked (no retry is spent).
    page.show();
    expect(fetcher).toHaveBeenCalledOnce();

    page.goOnline();
    await vi.waitFor(() => expect(controller.current).toEqual({ roles: ["DRIVER"], loading: false }));
    expect(fetcher).toHaveBeenCalledTimes(2);
    expect([...cache.saved.values()]).toEqual([["DRIVER"]]);
    expect(driverReportRoleOf(controller.current)).toBe("driver");

    // An answer is known: nothing more is asked.
    page.goOnline();
    page.show();
    expect(fetcher).toHaveBeenCalledTimes(2);
  });

  it("ask again when the app is shown again after a 503, reading as loading meanwhile", async () => {
    const fetcher = answersFetch({ status: 503, body: "{}" }, myOrganizationRolesAnswer("DRIVER", "DISPATCHER"));
    const { controller, page } = controllerFor(fetcher);
    await controller.start();
    expect(driverReportRoleOf(controller.current)).toBe("unknown");
    const seen: ReturnType<typeof driverReportRoleOf>[] = [];
    controller.subscribe((state) => seen.push(driverReportRoleOf(state)));

    page.hide();
    expect(fetcher).toHaveBeenCalledOnce();
    page.show();

    await vi.waitFor(() => expect(driverReportRoleOf(controller.current)).toBe("dispatch"));
    expect(seen).toEqual(["unknown", "loading", "dispatch"]);
  });

  it(`ask again at most ${DriverRolesMaximumRetries} times per session start`, async () => {
    const fetcher = answersFetch(new TypeError("Failed to fetch"));
    const { controller, page } = controllerFor(fetcher);
    await controller.start();

    for (let event = 0; event < DriverRolesMaximumRetries + 3; event += 1) {
      page.goOnline();
      await vi.waitFor(() => expect(controller.current.loading).toBe(false));
    }

    expect(fetcher).toHaveBeenCalledTimes(1 + DriverRolesMaximumRetries);
    expect(driverReportRoleOf(controller.current)).toBe("unknown");
  });

  it("are kept on 401 (the session ended) for the same driver's next sign-in, and never asked again", async () => {
    // DRV-SESSION-EXPIRY-OFFLINE-2026-10-19 (owner answer E2): nothing of the partition is wiped.
    const cache = new MemoryRolesCache();
    await savedFor(cache, ["DRIVER"]);
    const { controller, fetcher, page } = controllerFor(
      { status: 401, body: "{}", contentType: "application/problem+json" },
      { cache },
    );

    await controller.start();
    page.goOnline();
    page.show();

    expect(fetcher).toHaveBeenCalledOnce();
    expect(controller.current).toEqual({ roles: ["DRIVER"], loading: false });
    expect(cache.saved.size).toBe(1);
  });

  it.each([403])("are removed on %i (the person is no longer a member there) and never asked again", async (status) => {
    const cache = new MemoryRolesCache();
    await savedFor(cache, ["DRIVER"]);
    const { controller, fetcher, page } = controllerFor(
      { status, body: "{}", contentType: "application/problem+json" },
      { cache },
    );

    await controller.start();
    page.goOnline();
    page.show();

    expect(fetcher).toHaveBeenCalledOnce();
    expect(controller.current).toEqual({ roles: null, loading: false });
    expect(driverReportRoleOf(controller.current)).toBe("unknown");
    expect(cache.saved.size).toBe(0);
  });

  it("write nothing after the session ended (logout) and ask nothing more", async () => {
    const pending: { answer?: (response: Response) => void } = {};
    const fetcher = vi.fn(
      () =>
        new Promise<Response>((resolve) => {
          pending.answer = resolve;
        }),
    );
    const api: DriverOrganizationRolesApi = createDriverOrganizationRolesApi({
      baseUrl: "https://api.synthetic.test",
      session,
      fetch: fetcher as unknown as typeof fetch,
    });
    const cache = new MemoryRolesCache();
    const page = testWindow();
    const controller = new DriverRolesController({ session, api, cache, window: page.window });
    const started = controller.start();
    await vi.waitFor(() => expect(pending.answer).toBeDefined());

    controller.dispose();
    pending.answer?.(response(myOrganizationRolesAnswer("DRIVER")));
    await started;
    page.goOnline();
    page.show();

    expect(fetcher).toHaveBeenCalledOnce();
    expect(controller.current.roles).toBeNull();
    expect(cache.saved.size).toBe(0);
  });

  it("cancel a retry on its way when the session ends", async () => {
    const signals: AbortSignal[] = [];
    const fetcher = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      if (signals.length === 0) {
        signals.push(init?.signal as AbortSignal);
        throw new TypeError("Failed to fetch");
      }
      signals.push(init?.signal as AbortSignal);
      return new Promise<Response>(() => undefined);
    });
    const { controller, cache, page } = controllerFor(fetcher as unknown as ReturnType<typeof answersFetch>);
    await controller.start();

    page.goOnline();
    await vi.waitFor(() => expect(signals).toHaveLength(2));
    controller.dispose();

    expect(signals[1]?.aborted).toBe(true);
    expect(cache.saved.size).toBe(0);
  });
});
