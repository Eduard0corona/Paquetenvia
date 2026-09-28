import { readFileSync } from "node:fs";
import { describe, expect, it, vi } from "vitest";
import { TenantApiError } from "../api/tenant-request";
import type { PublicTrackingLink, TrackingLinkApi } from "../api/tracking-link-api";
import {
  TrackingLinkController,
  type TrackingLinkState,
} from "./tracking-link-controller";

const orderId = "66666666-6666-6666-6666-666666666666";
const token = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA";
// A second, obviously synthetic token of the contracted shape (43 Base64URL characters), built at runtime.
const secondToken = "s".repeat(43);

function link(value = token): PublicTrackingLink {
  return {
    tokenId: "77777777-7777-7777-7777-777777777777",
    orderId,
    token: value,
    expiresAt: "2026-10-05T12:00:00.000Z",
  };
}

function setup(api: Partial<TrackingLinkApi>) {
  const states: TrackingLinkState[] = [];
  let counter = 0;
  const controller = new TrackingLinkController(
    {
      issue: vi.fn().mockResolvedValue(link()),
      revoke: vi.fn().mockResolvedValue(undefined),
      ...api,
    },
    orderId,
    "https://ops.synthetic.test",
    (state) => states.push(state),
    () => `uuid-${++counter}`,
  );
  return { controller, states };
}

describe("tracking link controller (TRK-002-ISSUE-ENDPOINT)", () => {
  it("shows the public link once with a fresh key per click", async () => {
    const issue = vi
      .fn()
      .mockResolvedValueOnce(link())
      .mockResolvedValueOnce(link(secondToken));
    const { controller, states } = setup({ issue });

    await controller.issue();
    expect(controller.current).toEqual({
      kind: "shown",
      url: `https://ops.synthetic.test/track/${token}`,
      expiresAt: "2026-10-05T12:00:00.000Z",
      copied: false,
    });
    expect(states[0]).toEqual({ kind: "busy", action: "issue" });

    // Issuing again rotates: only the new link is shown.
    await controller.issue();
    expect(controller.current).toMatchObject({
      url: `https://ops.synthetic.test/track/${secondToken}`,
    });
    expect(issue.mock.calls.map((call) => call[1])).toEqual([
      "tracking-link-uuid-1",
      "tracking-link-uuid-2",
    ]);
  });

  it("copies to the clipboard and reports it, or stays uncopied without a clipboard", async () => {
    const { controller } = setup({});
    await controller.issue();
    const writeText = vi.fn().mockResolvedValue(undefined);
    await controller.copy({ writeText });
    expect(writeText).toHaveBeenCalledWith(`https://ops.synthetic.test/track/${token}`);
    expect(controller.current).toMatchObject({ kind: "shown", copied: true });

    await controller.copy(undefined);
    expect(controller.current).toMatchObject({ kind: "shown", copied: false });
    await controller.copy({ writeText: vi.fn().mockRejectedValue(new Error("denied")) });
    expect(controller.current).toMatchObject({ kind: "shown", copied: false });
  });

  it("forgets the link when hidden, revoked or disposed", async () => {
    const { controller, states } = setup({});
    await controller.issue();
    controller.hide();
    expect(controller.current.kind).toBe("idle");
    expect(JSON.stringify(controller.current)).not.toContain(token);

    await controller.issue();
    await controller.revoke();
    expect(controller.current).toEqual({
      kind: "idle",
      message: "Enlace revocado. El enlace anterior ya no muestra la orden.",
      stepUpHref: null,
    });

    await controller.issue();
    const notifications = states.length;
    controller.dispose();
    expect(controller.current).toEqual({ kind: "idle", message: null, stepUpHref: null });
    expect(states.length).toBe(notifications);
    await controller.issue();
    expect(states.length).toBe(notifications);
  });

  it("drops a response that arrives after the link was hidden or the controller disposed", async () => {
    let resolve: (value: PublicTrackingLink) => void = () => undefined;
    const issue = vi.fn(
      () => new Promise<PublicTrackingLink>((done) => (resolve = done)),
    );
    const { controller, states } = setup({ issue });
    const pending = controller.issue();
    controller.dispose();
    resolve(link());
    await pending;
    expect(states.some((state) => state.kind === "shown")).toBe(false);
  });

  it.each([
    ["forbidden", "No tienes permiso para gestionar el enlace de seguimiento."],
    ["not_found", "La orden no está disponible."],
    ["unavailable", "No fue posible generar el enlace. Intenta de nuevo."],
  ] as const)("maps %s to a message without the token", async (category, message) => {
    const { controller } = setup({
      issue: vi.fn().mockRejectedValue(new TenantApiError(category)),
    });
    await controller.issue();
    expect(controller.current).toEqual({ kind: "idle", message, stepUpHref: null });
  });

  it.each(["issue", "revoke"] as const)(
    "offers the MFA step-up back to the order when %s answers 403 MFA_REQUIRED",
    async (action) => {
      const mfa = new TenantApiError("forbidden", "MFA_REQUIRED", true);
      const { controller } = setup({
        issue: vi.fn().mockRejectedValue(mfa),
        revoke: vi.fn().mockRejectedValue(mfa),
      });
      await controller[action]();
      expect(controller.current).toEqual({
        kind: "idle",
        message: "Esta acción requiere verificar tu identidad (MFA).",
        stepUpHref: `/login?mfa=required&return_url=${encodeURIComponent(`/ops/orders/${orderId}`)}`,
      });
    },
  );

  it("keeps a generic 403 without the step-up", async () => {
    const { controller } = setup({
      issue: vi.fn().mockRejectedValue(new TenantApiError("forbidden")),
    });
    await controller.issue();
    expect(controller.current).toMatchObject({ stepUpHref: null });
  });

  it("ignores a second action while one is in flight", async () => {
    let resolve: (value: PublicTrackingLink) => void = () => undefined;
    const issue = vi.fn(
      () => new Promise<PublicTrackingLink>((done) => (resolve = done)),
    );
    const revoke = vi.fn().mockResolvedValue(undefined);
    const { controller } = setup({ issue, revoke });
    const pending = controller.issue();
    await controller.issue();
    await controller.revoke();
    resolve(link());
    await pending;
    expect(issue).toHaveBeenCalledOnce();
    expect(revoke).not.toHaveBeenCalled();
  });

  it("never persists or logs the link in the browser", () => {
    const sources = [
      "src/operations/api/tracking-link-api.ts",
      "src/operations/state/tracking-link-controller.ts",
      "src/operations/components/operations-tracking-link.tsx",
    ]
      .map((path) => readFileSync(path, "utf8"))
      .join("\n");
    expect(sources).not.toMatch(
      /localStorage|sessionStorage|indexedDB|caches\.|document\.cookie|console\.|postMessage|history\.(push|replace)State|searchParams/,
    );
    const component = readFileSync(
      "src/operations/components/operations-tracking-link.tsx",
      "utf8",
    );
    expect(component).toContain('autoComplete="off"');
    expect(component).toContain("canManageTrackingLink(");
    expect(component).toContain("href={state.stepUpHref}");
    expect(component).toContain("no se volverá a mostrar");
    expect(component).toContain("todavía no está disponible");
  });
});
