import { describe, expect, it, vi } from "vitest";
import type { OrderListApi } from "../api/order-list-api";
import { TenantApiError } from "../api/tenant-request";
import {
  orderListRangeOrderMessage,
  orderListRangeTooLongMessage,
  type OrderListItem,
  type OrderListPage,
  type OrderListQuery,
  type OrderListView,
} from "../contracts/order-list";
import type { OperationsSession } from "../session/operations-session";
import {
  backgroundRefreshIntervalMilliseconds,
  OrderListController,
  orderListFiltersRefusedMessage,
  orderListLoadFailedMessage,
  orderListPaginationFailedMessage,
} from "./order-list-controller";

/** UI-PHASE3-ORDERS-LIST-2026-10-12: the states of the "Órdenes" screen. */
const orgA = "11111111-1111-4111-8111-111111111111";
const orgB = "22222222-2222-4222-8222-222222222222";
const guide = "ORD_AbCdEfGhIjKlMnOpQrSt_-";
const noFilters: OrderListQuery = { status: null, publicId: null, createdFrom: null, createdTo: null };

function row(id: number, status: OrderListItem["status"] = "DELIVERED"): OrderListItem {
  return {
    id: `0a1b2c3d-1111-4222-8333-${String(id).padStart(12, "0")}`,
    public_id: `ORD_${String(id).padStart(22, "0")}`,
    status,
    total_cents: 1_000 * id,
    created_at: null,
  };
}

function setup(
  roles: Record<string, string>,
  list: OrderListApi["list"] = vi.fn(async () => ({ items: [row(2), row(1)], next_cursor: null })),
  view?: OrderListView,
) {
  let current: OperationsSession | null = { organizationId: orgA, sessionNamespace: "synthetic", getAccessToken: () => "token" };
  let hidden = false;
  let clock = 1_000_000;
  const api: OrderListApi = { list };
  const controller = new OrderListController(
    {
      readSession: () => current,
      createApi: () => api,
      loadRole: async (session) => roles[session.organizationId] ?? null,
      isHidden: () => hidden,
      now: () => clock,
    },
    view,
  );
  return {
    controller,
    list: vi.mocked(list),
    signOut() {
      current = null;
    },
    switchTo(organizationId: string) {
      current = { organizationId, sessionNamespace: "synthetic", getAccessToken: () => "token" };
    },
    hide(value: boolean) {
      hidden = value;
    },
    advance(milliseconds: number) {
      clock += milliseconds;
    },
  };
}

describe("order list states", () => {
  it.each([
    ["DISPATCHER", true],
    ["PLATFORM_ADMIN", true],
    ["VIEWER", false],
  ])("lists every order for %s, newest first as the API returns them", async (role, canOpenDetail) => {
    const env = setup({ [orgA]: role });
    await env.controller.start();
    expect(env.list).toHaveBeenCalledWith(noFilters, null, expect.any(AbortSignal));
    const state = env.controller.getSnapshot();
    expect(state).toMatchObject({ phase: "ready", loaded: true, loading: false, error: null, nextCursor: null, canOpenDetail });
    expect(state.rows.map((item) => item.public_id)).toEqual([row(2).public_id, row(1).public_id]);
    expect(state.lastUpdated).toBe(new Date(1_000_000).toISOString());
  });

  it.each(["FINANCE", "DRIVER", "BUSINESS_ADMIN", "UNKNOWN"])("never reads the list for %s", async (role) => {
    const env = setup({ [orgA]: role });
    await env.controller.start();
    expect(env.controller.getSnapshot()).toMatchObject({ phase: "access_unavailable", rows: [] });
    expect(env.list).not.toHaveBeenCalled();
  });

  it("shows no session without one", async () => {
    const env = setup({ [orgA]: "DISPATCHER" });
    env.signOut();
    await env.controller.start();
    expect(env.controller.getSnapshot().phase).toBe("no_session");
    expect(env.list).not.toHaveBeenCalled();
  });

  it("tells an empty organization from a pending read", async () => {
    let resolve: (page: OrderListPage) => void = () => undefined;
    const env = setup({ [orgA]: "DISPATCHER" }, vi.fn(() => new Promise<OrderListPage>((done) => (resolve = done))));
    const started = env.controller.start();
    await vi.waitFor(() => expect(env.list).toHaveBeenCalledOnce());
    expect(env.controller.getSnapshot()).toMatchObject({ loaded: false, loading: true });
    resolve({ items: [], next_cursor: null });
    await started;
    expect(env.controller.getSnapshot()).toMatchObject({ loaded: true, loading: false, rows: [] });
  });
});

describe("order list filters", () => {
  it("reads the URL filters as the status and the Mazatlán period, and drops the previous rows at once", async () => {
    let resolveSecond: (page: OrderListPage) => void = () => undefined;
    const list = vi
      .fn()
      .mockResolvedValueOnce({ items: [row(3), row(2), row(1)], next_cursor: null })
      .mockImplementationOnce(() => new Promise<OrderListPage>((done) => (resolveSecond = done)));
    const env = setup({ [orgA]: "DISPATCHER" }, list);
    await env.controller.start();

    const filtering = env.controller.setView({ status: "CLOSED", from: "2026-10-01", to: "2026-10-07" });
    expect(env.controller.getSnapshot()).toMatchObject({ rows: [], loaded: false, loading: true });
    expect(env.list).toHaveBeenLastCalledWith(
      {
        status: "CLOSED",
        publicId: null,
        createdFrom: "2026-10-01T07:00:00.000Z",
        createdTo: "2026-10-08T06:59:59.999999Z",
      },
      null,
      expect.any(AbortSignal),
    );
    resolveSecond({ items: [row(2, "CLOSED")], next_cursor: null });
    await filtering;
    expect(env.controller.getSnapshot().rows).toEqual([row(2, "CLOSED")]);

    // The same view again reads nothing.
    await env.controller.setView({ status: "CLOSED", from: "2026-10-01", to: "2026-10-07" });
    expect(env.list).toHaveBeenCalledTimes(2);
  });

  it.each([
    [{ status: null, from: "2026-10-08", to: "2026-10-07" }, orderListRangeOrderMessage],
    [{ status: null, from: "2026-10-01", to: "2026-11-01" }, orderListRangeTooLongMessage],
  ] as const)("refuses an impossible period before anything is sent (%j)", async (view, message) => {
    const env = setup({ [orgA]: "DISPATCHER" });
    await env.controller.start();
    await env.controller.setView(view);
    expect(env.controller.getSnapshot()).toMatchObject({ rangeError: message, rows: [], loaded: false, loading: false });
    expect(env.list).toHaveBeenCalledOnce();

    // Fixing the period reads again and clears the message.
    await env.controller.setView({ status: null, from: "2026-10-01", to: "2026-10-31" });
    expect(env.controller.getSnapshot()).toMatchObject({ rangeError: null, loaded: true });
    expect(env.list).toHaveBeenCalledTimes(2);
  });

  it("searches one exact guía together with the URL filters and stops searching on request", async () => {
    const env = setup({ [orgA]: "VIEWER" }, undefined, { status: "DELIVERED", from: null, to: null });
    await env.controller.start();
    await env.controller.setGuide(guide);
    expect(env.list).toHaveBeenLastCalledWith(
      { status: "DELIVERED", publicId: guide, createdFrom: null, createdTo: null },
      null,
      expect.any(AbortSignal),
    );
    expect(env.controller.getSnapshot().guide).toBe(guide);
    await env.controller.setGuide(null);
    expect(env.list).toHaveBeenLastCalledWith(
      { status: "DELIVERED", publicId: null, createdFrom: null, createdTo: null },
      null,
      expect.any(AbortSignal),
    );
  });

  it("never applies an answer of filters the person already left", async () => {
    let resolveFirst: (page: OrderListPage) => void = () => undefined;
    const list = vi
      .fn()
      .mockResolvedValueOnce({ items: [row(1)], next_cursor: null })
      .mockImplementationOnce(() => new Promise<OrderListPage>((done) => (resolveFirst = done)))
      .mockResolvedValueOnce({ items: [row(9, "CANCELLED")], next_cursor: null });
    const env = setup({ [orgA]: "DISPATCHER" }, list);
    await env.controller.start();
    const stale = env.controller.setView({ status: "CLOSED", from: null, to: null });
    await env.controller.setView({ status: "CANCELLED", from: null, to: null });
    resolveFirst({ items: [row(5, "CLOSED")], next_cursor: null });
    await stale;
    expect(env.controller.getSnapshot()).toMatchObject({ view: { status: "CANCELLED" }, rows: [row(9, "CANCELLED")] });
  });
});

describe("order list pagination", () => {
  it("appends the next page with the cursor and the exact query that issued it", async () => {
    const list = vi
      .fn()
      .mockResolvedValueOnce({ items: [row(4), row(3)], next_cursor: "c1" })
      .mockResolvedValueOnce({ items: [row(3), row(2)], next_cursor: "c2" })
      .mockResolvedValueOnce({ items: [row(1)], next_cursor: null });
    const env = setup({ [orgA]: "DISPATCHER" }, list, { status: null, from: "2026-10-01", to: null });
    await env.controller.start();
    const query = { status: null, publicId: null, createdFrom: "2026-10-01T07:00:00.000Z", createdTo: null };
    await env.controller.loadMore();
    expect(env.list).toHaveBeenNthCalledWith(2, query, "c1", expect.any(AbortSignal));
    await env.controller.loadMore();
    expect(env.list).toHaveBeenNthCalledWith(3, query, "c2", expect.any(AbortSignal));
    const state = env.controller.getSnapshot();
    expect(state.rows.map((item) => item.id)).toEqual([row(4), row(3), row(2), row(1)].map((item) => item.id));
    expect(state.nextCursor).toBeNull();
    await env.controller.loadMore();
    expect(env.list).toHaveBeenCalledTimes(3);
  });

  it("stops on a repeated cursor and keeps the rows on a failed page", async () => {
    const list = vi
      .fn()
      .mockResolvedValueOnce({ items: [row(3)], next_cursor: "c1" })
      .mockResolvedValueOnce({ items: [row(2)], next_cursor: "c1" });
    const env = setup({ [orgA]: "DISPATCHER" }, list);
    await env.controller.start();
    await env.controller.loadMore();
    expect(env.controller.getSnapshot()).toMatchObject({ nextCursor: null, error: orderListPaginationFailedMessage });

    const failing = vi
      .fn()
      .mockResolvedValueOnce({ items: [row(3)], next_cursor: "c1" })
      .mockRejectedValueOnce(new TenantApiError("conflict", "INVALID_REQUEST"));
    const other = setup({ [orgA]: "DISPATCHER" }, failing);
    await other.controller.start();
    await other.controller.loadMore();
    expect(other.controller.getSnapshot()).toMatchObject({
      rows: [row(3)],
      nextCursor: "c1",
      error: orderListPaginationFailedMessage,
      loadingMore: false,
    });
  });
});

describe("order list refresh and failures", () => {
  it("re-reads on visibility or focus only while one page is shown, never hidden nor twice in a few seconds", async () => {
    const list = vi.fn<OrderListApi["list"]>(async () => ({ items: [row(2)], next_cursor: "c1" }));
    const env = setup({ [orgA]: "DISPATCHER" }, list);
    await env.controller.start();
    await env.controller.refresh("focus");
    expect(env.list).toHaveBeenCalledOnce();
    env.advance(backgroundRefreshIntervalMilliseconds);
    env.hide(true);
    await env.controller.refresh("visibility");
    expect(env.list).toHaveBeenCalledOnce();
    env.hide(false);
    await env.controller.refresh("visibility");
    expect(env.list).toHaveBeenCalledTimes(2);

    list.mockResolvedValueOnce({ items: [row(1)], next_cursor: null });
    await env.controller.loadMore();
    env.advance(backgroundRefreshIntervalMilliseconds);
    await env.controller.refresh("focus");
    expect(env.list).toHaveBeenCalledTimes(3);
    expect(env.controller.getSnapshot().rows).toHaveLength(2);
    // Actualizar always reads the first page again.
    await env.controller.refresh("manual");
    expect(env.list).toHaveBeenCalledTimes(4);
    expect(env.controller.getSnapshot().rows).toEqual([row(2)]);
  });

  it("keeps the rows with a message when a refresh fails and says when the server refused the filters", async () => {
    const list = vi
      .fn()
      .mockResolvedValueOnce({ items: [row(2)], next_cursor: null })
      .mockRejectedValueOnce(new TenantApiError("unavailable"))
      .mockRejectedValueOnce(new TenantApiError("conflict", "INVALID_REQUEST"));
    const env = setup({ [orgA]: "DISPATCHER" }, list);
    await env.controller.start();
    await env.controller.refresh("manual");
    expect(env.controller.getSnapshot()).toMatchObject({ rows: [row(2)], error: orderListLoadFailedMessage });
    await env.controller.refresh("manual");
    expect(env.controller.getSnapshot()).toMatchObject({ rows: [row(2)], error: orderListFiltersRefusedMessage });
  });

  it("asks for the second factor on MFA_REQUIRED and clears everything on 401 or 403", async () => {
    const mfa = setup({ [orgA]: "PLATFORM_ADMIN" }, vi.fn().mockRejectedValue(new TenantApiError("forbidden", "MFA_REQUIRED", true)));
    await mfa.controller.start();
    const state = mfa.controller.getSnapshot();
    expect(state).toMatchObject({ phase: "ready", rows: [], message: "Esta acción requiere verificar tu identidad (MFA)." });
    expect(state.stepUpHref).toContain("mfa=required");
    expect(state.stepUpHref).toContain(encodeURIComponent("/ops/orders"));

    for (const error of [new TenantApiError("forbidden"), new TenantApiError("unauthorized")]) {
      const list = vi.fn().mockResolvedValueOnce({ items: [row(1)], next_cursor: null }).mockRejectedValueOnce(error);
      const env = setup({ [orgA]: "DISPATCHER" }, list);
      await env.controller.start();
      await env.controller.refresh("manual");
      expect(env.controller.getSnapshot()).toMatchObject({ phase: "access_unavailable", rows: [], nextCursor: null });
    }
  });

  it("starts from an empty list for another organization or session and forgets the guía", async () => {
    const env = setup({ [orgA]: "DISPATCHER", [orgB]: "VIEWER" });
    await env.controller.start();
    await env.controller.setGuide(guide);
    env.switchTo(orgB);
    await env.controller.start();
    expect(env.controller.getSnapshot()).toMatchObject({ phase: "ready", guide: null, canOpenDetail: false });
    expect(env.list).toHaveBeenLastCalledWith(noFilters, null, expect.any(AbortSignal));
    env.controller.stop();
    expect(env.controller.getSnapshot()).toMatchObject({ phase: "no_session", rows: [], guide: null });
  });
});
