import { readFileSync } from "node:fs";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RateLimitWaits } from "../../lib/rate-limit-wait";
import {
  isRecipientNoticeOfOrder,
  strongerOrderDetailReload,
  type OrderDetailReload,
} from "./order-detail-reloads";
import { ScreenReloads } from "./screen-reloads";

const orderId = "66666666-6666-6666-6666-666666666666";
const read = (path: string) => readFileSync(path, "utf8");

function screen(waits = new RateLimitWaits()) {
  const reloads: OrderDetailReload[] = [];
  const at: number[] = [];
  const start = Date.now();
  const gate: ScreenReloads<OrderDetailReload> = new ScreenReloads<OrderDetailReload>({
    operations: ["dashboard"],
    waits,
    stronger: strongerOrderDetailReload,
    reload: (kind) => {
      gate.reloaded();
      reloads.push(kind);
      at.push(Date.now() - start);
    },
  });
  return { gate, reloads, at, waits };
}

describe("live update of «Avisos al destinatario» (NTF-RECIPIENT-NOTICE-PANEL-2026-10-30 T6 choice 9)", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-30T15:00:00Z"));
  });
  afterEach(() => vi.useRealTimers());

  it("reloads the panel only for a NotificationStatusChanged v2 notice of this order", () => {
    const event = (event_type: string, order_id?: string) => ({ event_type, payload: order_id === undefined ? {} : { order_id } });
    expect(isRecipientNoticeOfOrder(event("NotificationStatusChanged.v2", orderId), orderId)).toBe(true);
    expect(isRecipientNoticeOfOrder(event("NotificationStatusChanged.v2", "77777777-7777-4777-8777-777777777777"), orderId)).toBe(false);
    // IN_APP (v1, no order) and a v1 row never reload the panel.
    expect(isRecipientNoticeOfOrder(event("NotificationStatusChanged.v1"), orderId)).toBe(false);
    expect(isRecipientNoticeOfOrder(event("NotificationStatusChanged.v1", orderId), orderId)).toBe(false);
  });

  it("gathers the panel's notices with the screen's: 250 ms, then at most one read every 3 seconds", async () => {
    const { gate, reloads, at } = screen();
    gate.notice("notices", 250);
    await vi.advanceTimersByTimeAsync(250);
    expect(reloads).toEqual(["notices"]);
    // A burst of notices (both channels of one event, then their receipts) is one more read 3 s after the first.
    for (let notice = 0; notice < 6; notice += 1) {
      gate.notice("notices", 250);
      await vi.advanceTimersByTimeAsync(100);
    }
    await vi.advanceTimersByTimeAsync(5_000);
    expect(reloads).toEqual(["notices", "notices"]);
    expect(at).toEqual([250, 3_250]);
  });

  it("reads the order (and with it the panel) when an order notice and a recipient notice are pending together", async () => {
    const { gate, reloads } = screen();
    gate.notice("notices", 250);
    gate.notice("realtime", 250);
    await vi.advanceTimersByTimeAsync(1_000);
    expect(reloads).toEqual(["realtime"]);
    expect(strongerOrderDetailReload("notices", "notices")).toBe("notices");
    expect(strongerOrderDetailReload("notices", "realtime")).toBe("realtime");
  });

  it("pauses with the whole screen during the dashboard's 429 wait and reads once when it ends", async () => {
    const waits = new RateLimitWaits();
    const { gate, reloads, at } = screen(waits);
    waits.record("dashboard", 10);
    // The read after "Detener avisos" is held, like a realtime notice.
    expect(gate.admit("notices")).toBe(false);
    gate.notice("notices", 250);
    await vi.advanceTimersByTimeAsync(9_000);
    expect(reloads).toEqual([]);
    await vi.advanceTimersByTimeAsync(2_000);
    expect(reloads).toEqual(["notices"]);
    expect(at).toEqual([10_000]);
  });

  it("is opt-in: only the order detail listens to NotificationStatusChanged; the dashboard and the inbox do not", () => {
    const factory = read("src/operations/realtime/operations-dashboard-realtime.ts");
    expect(factory).toContain("...(callbacks.notificationStatusChanged === undefined");
    const detail = read("src/operations/state/use-operations-order-detail.ts");
    expect(detail).toContain('if (isRecipientNoticeOfOrder(event, orderId)) reloads.notice("notices", 250);');
    expect(detail).toContain("setNoticesRevision((value) => value + 1);");
    expect(detail).toContain('if (reloads.admit("notices")) loadNotices();');
    for (const screenSource of ["src/operations/state/use-operations-dashboard.ts", "src/operations/state/use-operations-inbox.ts"])
      expect(read(screenSource)).not.toContain("notificationStatusChanged");
  });
});
