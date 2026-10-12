import { expect, vi } from "vitest";
import { createDriverOrganizationRolesApi } from "../api/organization-roles-api";
import type { DriverOrganizationRolesCache } from "../cache/driver-roles-cache";
import type { DriverCachePartition } from "../cache/driver-stops-cache";
import type { DriverSession } from "../session/driver-session";
import { DriverRolesController } from "./driver-roles-controller";

/**
 * Test support only. ORG-MY-ROLES-2026-10-12: GET /api/v1/me/organization-roles
 * as the API answers it - {"roles": [...]}, each role once in ordinal order
 * (MyOrganizationRoles.FromRoles on the server).
 */
export function myOrganizationRolesAnswer(...roles: string[]): RolesAnswer {
  return {
    status: 200,
    body: JSON.stringify({ roles: [...new Set(roles)].sort() }),
  };
}

/** What an API without getMyOrganizationRoles answers (an older API): the uniform 404. */
export const olderApiAnswer: RolesAnswer = Object.freeze({
  status: 404,
  body: JSON.stringify({ title: "Not Found", status: 404 }),
  contentType: "application/problem+json",
});

export interface RolesAnswer {
  readonly status: number;
  readonly body: string;
  readonly contentType?: string;
}

/** A fetch that answers every getMyOrganizationRoles call with `answer` and records the calls. */
export function rolesFetch(answer: RolesAnswer) {
  return vi.fn<(input: RequestInfo | URL, init?: RequestInit) => Promise<Response>>(async () =>
    new Response(answer.body, {
      status: answer.status,
      headers: { "Content-Type": answer.contentType ?? "application/json; charset=utf-8" },
    }),
  );
}

/** The roles store of one phone, in memory. */
export class MemoryRolesCache implements DriverOrganizationRolesCache {
  public readonly saved = new Map<string, readonly string[]>();

  public async readRoles(partition: DriverCachePartition): Promise<readonly string[] | null> {
    return this.saved.get(partition.key) ?? null;
  }

  public async replaceRoles(partition: DriverCachePartition, roles: readonly string[]): Promise<void> {
    this.saved.set(partition.key, [...roles]);
  }

  public async clearRoles(partition: DriverCachePartition): Promise<void> {
    this.saved.delete(partition.key);
  }
}

/**
 * A DriverRolesController started against `answer` through the real client
 * and its strict parser, so a test never injects the roles. With "pending" the
 * first request never answers: the roles stay loading.
 */
export async function startedRolesController(
  session: DriverSession,
  answer: RolesAnswer | "pending",
  cache: DriverOrganizationRolesCache = new MemoryRolesCache(),
): Promise<DriverRolesController> {
  const fetcher =
    answer === "pending"
      ? vi.fn(() => new Promise<Response>(() => undefined))
      : rolesFetch(answer);
  const controller = new DriverRolesController({
    session,
    api: createDriverOrganizationRolesApi({
      baseUrl: "https://api.synthetic.test",
      session,
      fetch: fetcher as unknown as typeof fetch,
      timeoutMilliseconds: answer === "pending" ? 3_600_000 : undefined,
    }),
    cache,
    window: quietWindow(),
  });
  const started = controller.start();
  if (answer === "pending") {
    await vi.waitFor(() => expect(fetcher).toHaveBeenCalledOnce());
  } else {
    await started;
  }
  return controller;
}

/**
 * A page for DriverRolesController that a test drives: online and visible
 * until it says otherwise, and its `online` and `visibilitychange` events are
 * dispatched by hand.
 */
export function testWindow(options: { readonly online?: boolean } = {}) {
  const page = new EventTarget();
  const document = Object.assign(new EventTarget(), {
    visibilityState: "visible" as DocumentVisibilityState,
  });
  const navigator = { onLine: options.online ?? true };
  return {
    page,
    document,
    navigator,
    window: {
      addEventListener: page.addEventListener.bind(page),
      removeEventListener: page.removeEventListener.bind(page),
      navigator,
      document,
    } as unknown as Pick<
      Window,
      "addEventListener" | "removeEventListener" | "navigator" | "document"
    >,
    goOnline() {
      navigator.onLine = true;
      page.dispatchEvent(new Event("online"));
    },
    show() {
      document.visibilityState = "visible";
      document.dispatchEvent(new Event("visibilitychange"));
    },
    hide() {
      document.visibilityState = "hidden";
      document.dispatchEvent(new Event("visibilitychange"));
    },
  };
}

/** A page that never fires an event, so a test of something else never retries. */
function quietWindow() {
  return testWindow().window;
}
