import { describe, expect, it, vi } from "vitest";
import {
  createDriverOrganizationRolesApi,
  type DriverOrganizationRolesApi,
} from "../api/organization-roles-api";
import { createDriverCachePartition } from "../cache/driver-stops-cache";
import { driverReportRole } from "../contracts/organization-roles";
import type { DriverSession } from "../session/driver-session";
import { DriverRolesController } from "./driver-roles-controller";
import {
  MemoryRolesCache,
  myOrganizationRolesAnswer,
  olderApiAnswer,
  rolesFetch,
  type RolesAnswer,
} from "./driver-roles.fixtures";

/**
 * ORG-MY-ROLES-2026-10-12: the roles come from getMyOrganizationRoles once per
 * session start; the answer saved for the partition keeps the report working
 * offline, a failed call keeps it, and 401 or 403 removes it.
 */
const session: DriverSession = {
  organizationId: "33333333-3333-4333-8333-333333333333",
  cacheNamespace: "opaque-driver-roles-session",
  getAccessToken: () => "memory-only",
};

function controllerFor(answer: RolesAnswer | Error, cache = new MemoryRolesCache()) {
  const fetcher =
    answer instanceof Error
      ? vi.fn(async () => {
          throw answer;
        })
      : rolesFetch(answer);
  const controller = new DriverRolesController({
    session,
    api: createDriverOrganizationRolesApi({
      baseUrl: "https://api.synthetic.test",
      session,
      fetch: fetcher as unknown as typeof fetch,
    }),
    cache,
  });
  return { controller, cache, fetcher };
}

async function savedFor(cache: MemoryRolesCache, roles: readonly string[]) {
  cache.saved.set((await createDriverCachePartition(session)).key, roles);
}

describe("organization roles of the driver session", () => {
  it("are unknown until an answer is known, then come from the API and are saved for the partition", async () => {
    const { controller, cache, fetcher } = controllerFor(myOrganizationRolesAnswer("DRIVER"));
    expect(controller.current.roles).toBeNull();

    await controller.start();

    expect(fetcher).toHaveBeenCalledOnce();
    expect(controller.current.roles).toEqual(["DRIVER"]);
    expect([...cache.saved.values()]).toEqual([["DRIVER"]]);
    expect(driverReportRole(controller.current.roles)).toBe("driver");
  });

  it("use the saved answer at once and replace it with the new one", async () => {
    const cache = new MemoryRolesCache();
    await savedFor(cache, ["DRIVER"]);
    const { controller } = controllerFor(myOrganizationRolesAnswer("DRIVER", "DISPATCHER"), cache);
    const seen: (readonly string[] | null)[] = [];
    controller.subscribe((state) => seen.push(state.roles));

    await controller.start();

    expect(seen).toEqual([null, ["DRIVER"], ["DISPATCHER", "DRIVER"]]);
    expect([...cache.saved.values()]).toEqual([["DISPATCHER", "DRIVER"]]);
    expect(driverReportRole(controller.current.roles)).toBe("dispatch");
  });

  it.each<[string, RolesAnswer | Error]>([
    ["offline", new TypeError("Failed to fetch")],
    ["a 503", { status: 503, body: "{}" }],
    ["an answer that breaks the contract", { status: 200, body: JSON.stringify({ roles: ["DRIVER"], extra: 1 }) }],
    ["an older API without the operation (404)", olderApiAnswer],
  ])("keep the saved answer when the call fails (%s): the report keeps working offline", async (_name, answer) => {
    const cache = new MemoryRolesCache();
    await savedFor(cache, ["DRIVER"]);
    const { controller } = controllerFor(answer, cache);

    await controller.start();

    expect(controller.current.roles).toEqual(["DRIVER"]);
    expect([...cache.saved.values()]).toEqual([["DRIVER"]]);
    expect(driverReportRole(controller.current.roles)).toBe("driver");
  });

  it("stay unknown against an older API (404) with nothing saved: the report fails closed, nothing else changes", async () => {
    const { controller, cache } = controllerFor(olderApiAnswer);

    await controller.start();

    expect(controller.current.roles).toBeNull();
    expect(cache.saved.size).toBe(0);
    expect(driverReportRole(controller.current.roles)).toBe("unknown");
  });

  it.each([401, 403])("are removed on %i (the person is no longer a member there)", async (status) => {
    const cache = new MemoryRolesCache();
    await savedFor(cache, ["DRIVER"]);
    const { controller } = controllerFor({ status, body: "{}", contentType: "application/problem+json" }, cache);

    await controller.start();

    expect(controller.current.roles).toBeNull();
    expect(cache.saved.size).toBe(0);
  });

  it("write nothing after the session ended (logout)", async () => {
    const pending: { answer?: (response: Response) => void } = {};
    const api: DriverOrganizationRolesApi = createDriverOrganizationRolesApi({
      baseUrl: "https://api.synthetic.test",
      session,
      fetch: (() =>
        new Promise<Response>((resolve) => {
          pending.answer = resolve;
        })) as unknown as typeof fetch,
    });
    const cache = new MemoryRolesCache();
    const controller = new DriverRolesController({ session, api, cache });
    const started = controller.start();
    await vi.waitFor(() => expect(pending.answer).toBeDefined());

    controller.dispose();
    pending.answer?.(
      new Response(myOrganizationRolesAnswer("DRIVER").body, {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );
    await started;

    expect(controller.current.roles).toBeNull();
    expect(cache.saved.size).toBe(0);
  });
});
