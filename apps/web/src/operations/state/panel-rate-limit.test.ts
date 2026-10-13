import { afterEach, describe, expect, it, vi } from "vitest";
import { RateLimitWaits } from "../../lib/rate-limit-wait";
import type { AssignmentApi } from "../api/assignment-api";
import type { CsvImportApi } from "../api/csv-import-api";
import { OperationsApiError } from "../api/operations-api";
import type { OrderListApi } from "../api/order-list-api";
import { TenantApiError } from "../api/tenant-request";
import type { AssignableDriver, AssignableDriverPage } from "../contracts/assignable-driver";
import type { BulkOrder } from "../contracts/bulk-assignment";
import { parseCsvImportCommit, parseCsvImportPreview } from "../contracts/csv-import";
import { defaultInboxView } from "../contracts/inbox";
import type {
  OperationsDashboardFilters,
  OperationsDashboardOrder,
  OperationsDashboardResponse,
} from "../contracts/operations-dashboard";
import type { OperationsQueueCounts } from "../contracts/queue-counts";
import {
  bearerSession,
  commitResponse,
  orderId,
  orgA,
  previewResponse,
  syntheticUuid,
} from "../contracts/ui-001-screens.fixtures";
import type { OperationsSession } from "../session/operations-session";
import { BulkAssignmentController, type BulkAssignmentApi } from "./bulk-assignment-controller";
import { CsvImportController } from "./csv-import-controller";
import { DriverAssignmentController } from "./driver-assignment-controller";
import { InboxController, type InboxApi } from "./inbox-controller";
import { OrderListController } from "./order-list-controller";
import { searchOrderByPublicId } from "./order-search";

/**
 * SEC-004 on the panel, owner answers Q6 "Mensaje común con espera (Recommended)" (the screens show "Demasiadas
 * solicitudes. Espera N segundos e intenta de nuevo." and their buttons re-enable themselves when N runs out), Q9
 * "Espaciar recargas (Recommended)" (realtime reloads at most every 3 seconds per tab) and Q10 "Dejarlo en 60/min
 * (Recommended)" ("Si pasa, el panel pide esperar unos segundos."). A 429 is never a failure message of the screen,
 * nothing is sent during the server's wait, and nothing it had is lost.
 */

/** A clock and timers the test moves by hand, shared by the waits and the reload spacing. */
class FakeTime {
  public now = 1_000_000;
  private timers: { at: number; callback: () => void; id: number }[] = [];
  private nextId = 1;

  public readonly setTimer = (callback: () => void, delay: number): unknown => {
    const id = this.nextId++;
    this.timers.push({ at: this.now + delay, callback, id });
    return id;
  };

  public readonly clearTimer = (timer: unknown): void => {
    this.timers = this.timers.filter((entry) => entry.id !== timer);
  };

  public async advance(milliseconds: number): Promise<void> {
    const until = this.now + milliseconds;
    for (;;) {
      const due = this.timers.filter((entry) => entry.at <= until).sort((left, right) => left.at - right.at)[0];
      if (due === undefined) break;
      this.timers = this.timers.filter((entry) => entry !== due);
      this.now = due.at;
      due.callback();
      await flush();
    }
    this.now = until;
    await flush();
  }
}

const flush = async () => {
  for (let turn = 0; turn < 5; turn += 1) await new Promise((resolve) => setTimeout(resolve, 0));
};

function rateLimited(waits: RateLimitWaits, operation: "dashboard" | "queue_counts", seconds: number): OperationsApiError {
  waits.record(operation, seconds);
  return new OperationsApiError("rate_limited", seconds);
}

describe("CSV import (preview and commit share one budget, owner answer Q1)", () => {
  function setup(api: Partial<CsvImportApi>, waits: RateLimitWaits) {
    const full: CsvImportApi = {
      preview: vi.fn(async () => parseCsvImportPreview(previewResponse())),
      commit: vi.fn(async () => ({ kind: "committed" as const, commit: parseCsvImportCommit(commitResponse()) })),
      ...api,
    };
    const controller = new CsvImportController({
      readSession: () => bearerSession(orgA),
      createApi: () => full,
      loadRole: async () => "DISPATCHER",
      waits,
    });
    return { controller, api: full };
  }

  it("keeps the preview's report and the batch key through a commit 429, and sends again only after the wait", async () => {
    let now = 0;
    const waits = new RateLimitWaits(() => now);
    const commit = vi
      .fn<CsvImportApi["commit"]>()
      .mockImplementationOnce(async () => {
        waits.record("csv_import", 40);
        throw new TenantApiError("rate_limited", null, false, 40);
      })
      .mockResolvedValue({ kind: "committed", commit: parseCsvImportCommit(commitResponse()) });
    const { controller, api } = setup({ commit }, waits);
    await controller.start();
    await controller.selectFile(new Blob(["quote_id,payer_type,terms_version\n"]));
    await controller.preview();
    await controller.commit(true);
    expect(controller.getSnapshot()).toMatchObject({ message: null, errors: [], busy: false, commit: null });
    expect(controller.getSnapshot().preview).not.toBeNull();

    // During the wait neither the commit nor a new preview is sent: they share the budget.
    await controller.commit(true);
    await controller.preview();
    expect(commit).toHaveBeenCalledOnce();
    expect(api.preview).toHaveBeenCalledOnce();

    now += 40_000;
    await controller.commit(true);
    expect(commit).toHaveBeenCalledTimes(2);
    // Nothing was applied: the same batch goes again with the same Idempotency-Key.
    expect(commit.mock.calls[1][2]).toBe(commit.mock.calls[0][2]);
    expect(controller.getSnapshot().commit?.created_rows).toBe(1);
  });
});

describe("order list", () => {
  it("keeps the rows on a 429, shows no failure and sends nothing until the wait ends", async () => {
    let now = 0;
    const waits = new RateLimitWaits(() => now);
    const shown = { id: syntheticUuid(0x301), public_id: `ORD_${"1".padStart(22, "0")}`, status: "DELIVERED", total_cents: 1_000, created_at: null } as const;
    const list = vi
      .fn<OrderListApi["list"]>()
      .mockResolvedValueOnce({ items: [shown], next_cursor: null })
      .mockImplementationOnce(async () => {
        waits.record("order_list", 9);
        throw new TenantApiError("rate_limited", null, false, 9);
      })
      .mockResolvedValue({ items: [], next_cursor: null });
    const controller = new OrderListController({
      readSession: () => ({ organizationId: orgA, sessionNamespace: "synthetic", getAccessToken: () => "token" }),
      createApi: () => ({ list }),
      loadRole: async () => "DISPATCHER",
      isHidden: () => false,
      now: () => now,
      waits,
    });
    await controller.start();
    await controller.refresh("manual");
    expect(controller.getSnapshot()).toMatchObject({ rows: [shown], error: null, loading: false });
    await controller.refresh("manual");
    expect(list).toHaveBeenCalledTimes(2);
    now += 9_000;
    await controller.refresh("manual");
    expect(list).toHaveBeenCalledTimes(3);
  });
});

describe("Buscar guía (listOrders)", () => {
  it("answers rate_limited on a 429 and sends nothing while the wait lasts", async () => {
    let now = 0;
    const waits = new RateLimitWaits(() => now);
    const findOrderIdByPublicId = vi.fn(async () => {
      throw new TenantApiError("rate_limited", null, false, 6);
    });
    const guide = `ORD_${"7".padStart(22, "0")}`;
    expect(await searchOrderByPublicId({ findOrderIdByPublicId }, guide, undefined, waits)).toEqual({ kind: "rate_limited" });
    expect(waits.secondsLeft(["order_list"])).toBe(6);
    expect(await searchOrderByPublicId({ findOrderIdByPublicId }, guide, undefined, waits)).toEqual({ kind: "rate_limited" });
    expect(findOrderIdByPublicId).toHaveBeenCalledOnce();
    now += 6_000;
    findOrderIdByPublicId.mockResolvedValueOnce(orderId as never);
    expect(await searchOrderByPublicId({ findOrderIdByPublicId }, guide, undefined, waits)).toEqual({ kind: "found", orderId });
  });
});

describe("work inbox: realtime reloads spaced 3 seconds (Q9) and the server's wait", () => {
  const session = { organizationId: orgA, sessionNamespace: "inbox", getAccessToken: () => "token" } as OperationsSession;

  function row(minute: number): OperationsDashboardOrder {
    const instant = new Date(Date.UTC(2026, 9, 13, 18, minute)).toISOString();
    return {
      order_id: syntheticUuid(0x400 + minute),
      aggregate_version: 1,
      public_id: `ORD_${String(minute).padStart(22, "0")}`,
      owner: { organization_id: orgA, display_name: "Dueña" },
      operator: null,
      client: null,
      status: "READY_FOR_PICKUP",
      created_at: instant,
      updated_at: instant,
      service_type: "SAME_DAY",
      pickup_window: null,
      delivery_window: null,
      delivery_zone: null,
      assignment: null,
      latest_driver_location: null,
      total: { currency: "MXN", amount_cents: 9_000 },
      cost_warning: null,
      unassigned_alert: true,
    } as OperationsDashboardOrder;
  }

  function counts(unassigned: number): OperationsQueueCounts {
    return {
      generated_at: "2026-10-13T19:00:00Z",
      total: unassigned,
      by_status: {
        DRAFT: 0, CONFIRMED: 0, READY_FOR_PICKUP: unassigned, ASSIGNED: 0, AT_PICKUP: 0, PICKED_UP: 0,
        IN_TRANSIT: 0, DELIVERING: 0, FAILED_ATTEMPT: 0, RESCHEDULED: 0, RETURNING: 0, RETURNED: 0,
        DELIVERED: 0, CLOSED: 0, CLAIM_OPEN: 0, CLAIM_RESOLVED: 0, CANCELLED: 0,
      },
      queues: { unassigned, needs_attention: 0, price_review: 0, delivered_not_closed: 0, en_route: 0 },
    } as OperationsQueueCounts;
  }

  class FakeInboxApi implements InboxApi {
    public lists = 0;
    public countReads = 0;
    public listAnswer: () => Promise<OperationsDashboardResponse> = async () => ({
      generated_at: "2026-10-13T19:00:00Z",
      items: [row(1)],
      next_cursor: null,
    });
    public countAnswer: () => Promise<OperationsQueueCounts> = async () => counts(1);

    public list(filters: OperationsDashboardFilters): Promise<OperationsDashboardResponse> {
      expect(filters.unassigned).toBe(true);
      this.lists += 1;
      return this.listAnswer();
    }

    public queueCounts(): Promise<OperationsQueueCounts> {
      this.countReads += 1;
      return this.countAnswer();
    }
  }

  function setup() {
    const time = new FakeTime();
    const waits = new RateLimitWaits(() => time.now);
    const api = new FakeInboxApi();
    const controller = new InboxController(
      {
        readSession: () => session,
        createApi: () => api,
        isHidden: () => false,
        waits,
        timers: { now: () => time.now, setTimer: time.setTimer, clearTimer: time.clearTimer },
      },
      defaultInboxView,
    );
    return { time, waits, api, controller };
  }

  it("gathers realtime notices into one reload at most every 3 seconds", async () => {
    const { time, api, controller } = setup();
    await controller.start();
    await flush();
    expect(api.lists).toBe(1);
    await time.advance(3_000);
    // A bulk assignment of twenty orders: twenty notices within two seconds.
    for (let notice = 0; notice < 20; notice += 1) {
      controller.notice();
      await time.advance(100);
    }
    await time.advance(5_000);
    expect(api.lists).toBe(3);
    // Every reload read the counts too: one per reload, not one per notice.
    expect(api.countReads).toBe(3);
  });

  it("keeps the rows on a 429, holds every reload during the wait and reads once by itself when it ends", async () => {
    const { time, waits, api, controller } = setup();
    await controller.start();
    await flush();
    const rows = controller.getSnapshot().rows;
    api.listAnswer = async () => {
      throw rateLimited(waits, "dashboard", 30);
    };
    await controller.refresh("manual");
    expect(controller.getSnapshot()).toMatchObject({ phase: "ready", error: null, rows });
    expect(api.lists).toBe(2);

    api.listAnswer = async () => ({ generated_at: "2026-10-13T19:01:00Z", items: [row(2)], next_cursor: null });
    controller.notice();
    await controller.refresh("polling");
    await controller.refresh("manual");
    await time.advance(29_000);
    expect(api.lists).toBe(2);
    await time.advance(1_000);
    expect(api.lists).toBe(3);
    expect(controller.getSnapshot().rows.map((item) => item.order_id)).toEqual([row(2).order_id]);
  });

  it("shows the counts unavailable on their own 429 and reads them once when their wait ends", async () => {
    const { time, waits, api, controller } = setup();
    api.countAnswer = async () => {
      throw rateLimited(waits, "queue_counts", 10);
    };
    await controller.start();
    await flush();
    expect(controller.getSnapshot()).toMatchObject({ counts: null, countsUnavailable: true, error: null });
    expect(api.countReads).toBe(1);
    api.countAnswer = async () => counts(4);
    await time.advance(9_999);
    expect(api.countReads).toBe(1);
    await time.advance(1);
    expect(api.countReads).toBe(2);
    expect(controller.getSnapshot().counts?.queues.unassigned).toBe(4);
    // The list was never held: it has its own budget (owner answer Q3).
    expect(api.lists).toBe(1);
  });
});

describe("assignable drivers (Dispatch.AssignableDrivers, owner answer Q10)", () => {
  const eligible: AssignableDriver = {
    driver_id: syntheticUuid(0x601),
    driver_reference: "DRV-0a1b2c3d",
    vehicle_type: "MOTORCYCLE",
    eligible: true,
    ineligibility_reasons: [],
    active_assignment_count: 0,
  };
  const page = (items: readonly AssignableDriver[], next_cursor: string | null = null): AssignableDriverPage => ({
    items,
    next_cursor,
  });

  afterEach(() => vi.restoreAllMocks());

  it("the order's picker waits the server and reads the list by itself, with no failure shown", async () => {
    let now = 0;
    const waits = new RateLimitWaits(() => now);
    const slept: number[] = [];
    const listAssignableDrivers = vi
      .fn<AssignmentApi["listAssignableDrivers"]>()
      .mockRejectedValueOnce(new TenantApiError("rate_limited", null, false, 8))
      .mockResolvedValue(page([eligible], "next"));
    const states: string[] = [];
    const controller = new DriverAssignmentController(
      { listAssignableDrivers, assignDriver: vi.fn() } as unknown as AssignmentApi,
      orderId,
      (state) => states.push(state.kind),
      vi.fn(),
      () => "uuid",
      {
        waits,
        sleep: async (milliseconds) => {
          slept.push(milliseconds);
          now += milliseconds;
        },
      },
    );
    await controller.load();
    expect(slept).toEqual([8_000]);
    expect(states).not.toContain("unavailable");
    expect(controller.current).toMatchObject({ kind: "ready", drivers: [eligible], message: null });

    // "Cargar más repartidores" sends nothing during a wait and keeps what is shown after a 429.
    waits.record("assignable_drivers", 5);
    await controller.loadMore();
    expect(listAssignableDrivers).toHaveBeenCalledTimes(2);
    now += 5_000;
    listAssignableDrivers.mockRejectedValueOnce(new TenantApiError("rate_limited", null, false, 5));
    await controller.loadMore();
    expect(controller.current).toMatchObject({ kind: "ready", drivers: [eligible], busy: "idle", message: null });
  });

  it("a bulk assignment waits the server between orders and goes on by itself", async () => {
    let now = 0;
    const waits = new RateLimitWaits(() => now);
    const slept: number[] = [];
    const orders: BulkOrder[] = [1, 2, 3].map((counter) => ({
      orderId: syntheticUuid(0x100 + counter),
      publicId: `ORD_${String(counter).padStart(22, "0")}`,
      zoneName: "Centro",
      totalCents: 9_000,
    }));
    const read: string[] = [];
    const listAssignableDrivers = vi.fn(async (id: string) => {
      read.push(id);
      if (id === orders[1].orderId && read.filter((value) => value === id).length === 1) {
        throw new TenantApiError("rate_limited", null, false, 20);
      }
      return page([eligible]);
    });
    const controller = new BulkAssignmentController(
      { listAssignableDrivers, assignDriver: vi.fn() } as unknown as BulkAssignmentApi,
      () => undefined,
      vi.fn(),
      "/ops/inbox?queue=unassigned",
      () => "uuid",
      {
        waits,
        sleep: async (milliseconds) => {
          slept.push(milliseconds);
          now += milliseconds;
        },
      },
    );
    await controller.load(orders);
    expect(slept).toEqual([20_000]);
    expect(read).toEqual([orders[0].orderId, orders[1].orderId, orders[1].orderId, orders[2].orderId]);
    expect(controller.current.phase).toBe("ready");
    for (const order of orders) expect(controller.current.drivers[order.orderId]?.kind).toBe("ready");
  });
});
