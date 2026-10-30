import { describe, expect, it, vi } from "vitest";
import type { RecipientNoticesApi } from "../api/recipient-notices-api";
import { TenantApiError } from "../api/tenant-request";
import type { RecipientNoticeList } from "../contracts/recipient-notices";
import { RecipientNoticesController, type RecipientNoticesState } from "./recipient-notices-controller";

const orderId = "66666666-6666-6666-6666-666666666666";

const list: RecipientNoticeList = {
  items: [
    {
      notificationId: "77777777-7777-4777-8777-777777777777",
      notice: "TRACKING_LINK",
      channel: "WHATSAPP",
      status: "PENDING",
      attempts: 0,
      lastCode: null,
      sentAt: null,
      deliveredAt: null,
      updatedAt: "2026-10-30T15:00:00.000Z",
    },
  ],
  whatsappConsentAttested: true,
  emailOnFile: true,
};

function setup(api: Partial<RecipientNoticesApi>) {
  const states: RecipientNoticesState[] = [];
  const afterStop = vi.fn();
  let counter = 0;
  const full: RecipientNoticesApi = {
    list: vi.fn().mockResolvedValue(list),
    stop: vi.fn().mockResolvedValue({ orderId, channels: ["WHATSAPP", "EMAIL"], recordedAt: "2026-10-30T16:00:00.000Z" }),
    ...api,
  };
  const controller = new RecipientNoticesController(full, orderId, (state) => states.push(state), afterStop, () => `uuid-${++counter}`);
  return { controller, states, afterStop, api: full };
}

describe("recipient notices controller (NTF-RECIPIENT-NOTICE-PANEL-2026-10-30)", () => {
  it("loads the list and shows a fixed message when it cannot be read", async () => {
    const listCall = vi.fn().mockResolvedValueOnce(list).mockRejectedValueOnce(new TenantApiError("unavailable"));
    const { controller } = setup({ list: listCall });

    expect(controller.current.list).toEqual({ kind: "loading" });
    await controller.load();
    expect(controller.current.list).toEqual({ kind: "loaded", list });

    await controller.load();
    expect(controller.current.list).toEqual({
      kind: "failed",
      message: "No fue posible leer los avisos.",
      stepUpHref: null,
    });
    expect(listCall).toHaveBeenCalledWith(orderId, expect.any(AbortSignal));
  });

  it("keeps only the latest read when two overlap", async () => {
    let resolveFirst: (value: RecipientNoticeList) => void = () => undefined;
    const first = new Promise<RecipientNoticeList>((resolve) => {
      resolveFirst = resolve;
    });
    const empty: RecipientNoticeList = { items: [], whatsappConsentAttested: false, emailOnFile: false };
    const listCall = vi.fn().mockReturnValueOnce(first).mockResolvedValueOnce(empty);
    const { controller } = setup({ list: listCall });

    const pending = controller.load();
    await controller.load();
    resolveFirst(list);
    await pending;
    expect(controller.current.list).toEqual({ kind: "loaded", list: empty });
  });

  it("offers the MFA step-up back to the order for a PLATFORM_ADMIN without a second factor", async () => {
    const mfa = new TenantApiError("forbidden", "MFA_REQUIRED", true);
    const { controller } = setup({ list: vi.fn().mockRejectedValue(mfa), stop: vi.fn().mockRejectedValue(mfa) });

    await controller.load();
    expect(controller.current.list).toEqual({
      kind: "failed",
      message: "Esta acción requiere verificar tu identidad (MFA).",
      stepUpHref: expect.stringContaining(encodeURIComponent(`/ops/orders/${orderId}`)),
    });
    await controller.stop();
    expect(controller.current.stop).toMatchObject({
      kind: "failed",
      stepUpHref: expect.stringContaining(encodeURIComponent(`/ops/orders/${orderId}`)),
    });
  });

  it("confirms a stop in memory, asks for a new read and keeps the button for another press (T3)", async () => {
    const { controller, afterStop, api, states } = setup({});

    await controller.stop();
    expect(states).toContainEqual(expect.objectContaining({ stop: { kind: "busy" } }));
    expect(controller.current.stop).toEqual({ kind: "stopped", channels: ["WHATSAPP", "EMAIL"] });
    expect(afterStop).toHaveBeenCalledTimes(1);

    // Another press is a new press: a new key (the server audits it again and changes nothing for the recipient).
    await controller.stop();
    expect(api.stop).toHaveBeenNthCalledWith(1, orderId, "recipient-notices-stop-uuid-1", expect.any(AbortSignal));
    expect(api.stop).toHaveBeenNthCalledWith(2, orderId, "recipient-notices-stop-uuid-2", expect.any(AbortSignal));
    expect(afterStop).toHaveBeenCalledTimes(2);
  });

  it("reuses the press's key for a retry after an answer that may not have reached the server", async () => {
    const stop = vi
      .fn()
      .mockRejectedValueOnce(new TenantApiError("network"))
      .mockRejectedValueOnce(new TenantApiError("unavailable"))
      .mockResolvedValueOnce({ orderId, channels: ["WHATSAPP"], recordedAt: "2026-10-30T16:00:00.000Z" })
      .mockRejectedValueOnce(new TenantApiError("conflict", "RECIPIENT_CONTACT_UNAVAILABLE"))
      .mockResolvedValueOnce({ orderId, channels: ["WHATSAPP"], recordedAt: "2026-10-30T16:05:00.000Z" });
    const { controller, afterStop } = setup({ stop });

    await controller.stop();
    expect(controller.current.stop).toEqual({
      kind: "failed",
      message: "No pudimos detener los avisos. Intenta de nuevo.",
      stepUpHref: null,
    });
    await controller.stop();
    await controller.stop();
    expect(controller.current.stop).toEqual({ kind: "stopped", channels: ["WHATSAPP"] });
    await controller.stop();
    expect(controller.current.stop).toMatchObject({ kind: "failed", message: "No pudimos detener los avisos. Intenta de nuevo." });
    await controller.stop();

    const keys = stop.mock.calls.map((call) => call[1]);
    // The three tries of the first press share one key; a definitive answer drops it.
    expect(keys.slice(0, 3)).toEqual(["recipient-notices-stop-uuid-1", "recipient-notices-stop-uuid-1", "recipient-notices-stop-uuid-1"]);
    expect(keys[3]).toBe("recipient-notices-stop-uuid-2");
    expect(keys[4]).toBe("recipient-notices-stop-uuid-3");
    // The list is read again after every answer.
    expect(afterStop).toHaveBeenCalledTimes(5);
  });

  it("ignores a second press while one is in flight and drops everything when disposed", async () => {
    let resolveStop: (value: unknown) => void = () => undefined;
    const stop = vi.fn().mockReturnValue(new Promise((resolve) => (resolveStop = resolve)));
    const { controller, afterStop } = setup({ stop });

    const first = controller.stop();
    await controller.stop();
    expect(stop).toHaveBeenCalledTimes(1);
    controller.dispose();
    resolveStop({ orderId, channels: ["WHATSAPP"], recordedAt: "2026-10-30T16:00:00.000Z" });
    await first;
    expect(afterStop).not.toHaveBeenCalled();
    await controller.load();
    expect(controller.current.list).toEqual({ kind: "loading" });
  });
});
