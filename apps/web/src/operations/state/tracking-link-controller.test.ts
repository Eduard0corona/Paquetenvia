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

function link(value = token, generation = 1, validUntil: string | null = null): PublicTrackingLink {
  return {
    tokenId: "77777777-7777-7777-7777-777777777777",
    orderId,
    token: value,
    url: `https://paquetenvia.test/track/${value}`,
    generation,
    validUntil,
  };
}

function setup(api: Partial<TrackingLinkApi>) {
  const states: TrackingLinkState[] = [];
  let counter = 0;
  const controller = new TrackingLinkController(
    {
      getOrCreate: vi.fn().mockResolvedValue(link()),
      ...api,
    },
    orderId,
    (state) => states.push(state),
    () => `uuid-${++counter}`,
  );
  return { controller, states };
}

describe("tracking link controller (TRK-002-AUTO-LINK)", () => {
  it("shows the server's public link, the same one on every show, with a fresh key per click", async () => {
    const getOrCreate = vi.fn().mockResolvedValue(link());
    const { controller, states } = setup({ getOrCreate });

    await controller.show();
    expect(controller.current).toEqual({
      kind: "shown",
      url: `https://paquetenvia.test/track/${token}`,
      generation: 1,
      validUntil: null,
      copied: false,
    });
    expect(states[0]).toEqual({ kind: "busy", action: "show" });

    // Showing again is get-or-create: the same link, never a rotation.
    controller.hide();
    await controller.show();
    expect(controller.current).toMatchObject({
      url: `https://paquetenvia.test/track/${token}`,
      generation: 1,
    });
    expect(getOrCreate.mock.calls.map((call) => call[1])).toEqual([
      "tracking-link-uuid-1",
      "tracking-link-uuid-2",
    ]);
  });

  it("shows a finished order's grace end with the same link", async () => {
    const getOrCreate = vi
      .fn()
      .mockResolvedValueOnce(link())
      .mockResolvedValueOnce(link(token, 1, "2026-10-01T18:00:00.000Z"));
    const { controller } = setup({ getOrCreate });
    await controller.show();
    controller.hide();
    await controller.show();
    expect(controller.current).toEqual({
      kind: "shown",
      url: `https://paquetenvia.test/track/${token}`,
      generation: 1,
      validUntil: "2026-10-01T18:00:00.000Z",
      copied: false,
    });
  });

  it("offers no revocation (TRK-002-NO-REVOCATION)", () => {
    const { controller } = setup({});
    expect("revoke" in controller).toBe(false);
    const component = readFileSync(
      "src/operations/components/operations-tracking-link.tsx",
      "utf8",
    );
    expect(component).not.toMatch(/Revocar|revoke\(/);
  });

  it("copies to the clipboard and reports it, or stays uncopied without a clipboard", async () => {
    const { controller } = setup({});
    await controller.show();
    const writeText = vi.fn().mockResolvedValue(undefined);
    await controller.copy({ writeText });
    expect(writeText).toHaveBeenCalledWith(`https://paquetenvia.test/track/${token}`);
    expect(controller.current).toMatchObject({ kind: "shown", copied: true });

    await controller.copy(undefined);
    expect(controller.current).toMatchObject({ kind: "shown", copied: false });
    await controller.copy({ writeText: vi.fn().mockRejectedValue(new Error("denied")) });
    expect(controller.current).toMatchObject({ kind: "shown", copied: false });
  });

  it("forgets the link when hidden or disposed", async () => {
    const { controller, states } = setup({});
    await controller.show();
    controller.hide();
    expect(controller.current).toEqual({ kind: "idle", message: null, stepUpHref: null });
    expect(JSON.stringify(controller.current)).not.toContain(token);

    await controller.show();
    const notifications = states.length;
    controller.dispose();
    expect(controller.current).toEqual({ kind: "idle", message: null, stepUpHref: null });
    expect(states.length).toBe(notifications);
    await controller.show();
    expect(states.length).toBe(notifications);
  });

  it("drops a response that arrives after the controller was disposed", async () => {
    let resolve: (value: PublicTrackingLink) => void = () => undefined;
    const getOrCreate = vi.fn(
      () => new Promise<PublicTrackingLink>((done) => (resolve = done)),
    );
    const { controller, states } = setup({ getOrCreate });
    const pending = controller.show();
    controller.dispose();
    resolve(link());
    await pending;
    expect(states.some((state) => state.kind === "shown")).toBe(false);
  });

  it.each([
    [new TenantApiError("forbidden"), "No tienes permiso para gestionar el enlace de seguimiento."],
    [new TenantApiError("not_found"), "La orden no está disponible."],
    [
      new TenantApiError("conflict", "TRACKING_LINK_ORDER_FINISHED"),
      "La orden ya terminó y su enlace ya no está vigente; no se generan enlaces nuevos.",
    ],
    [new TenantApiError("conflict"), "No fue posible obtener el enlace. Intenta de nuevo."],
    [new TenantApiError("unavailable"), "No fue posible obtener el enlace. Intenta de nuevo."],
  ] as const)("maps %o to a message without the token", async (error, message) => {
    const { controller } = setup({
      getOrCreate: vi.fn().mockRejectedValue(error),
    });
    await controller.show();
    expect(controller.current).toEqual({ kind: "idle", message, stepUpHref: null });
  });

  it("offers the MFA step-up back to the order when show answers 403 MFA_REQUIRED", async () => {
    const mfa = new TenantApiError("forbidden", "MFA_REQUIRED", true);
    const { controller } = setup({
      getOrCreate: vi.fn().mockRejectedValue(mfa),
    });
    await controller.show();
    expect(controller.current).toEqual({
      kind: "idle",
      message: "Esta acción requiere verificar tu identidad (MFA).",
      stepUpHref: `/login?mfa=required&return_url=${encodeURIComponent(`/ops/orders/${orderId}`)}`,
    });
  });

  it("keeps a generic 403 without the step-up", async () => {
    const { controller } = setup({
      getOrCreate: vi.fn().mockRejectedValue(new TenantApiError("forbidden")),
    });
    await controller.show();
    expect(controller.current).toMatchObject({ stepUpHref: null });
  });

  it("ignores a second action while one is in flight", async () => {
    let resolve: (value: PublicTrackingLink) => void = () => undefined;
    const getOrCreate = vi.fn(
      () => new Promise<PublicTrackingLink>((done) => (resolve = done)),
    );
    const { controller } = setup({ getOrCreate });
    const pending = controller.show();
    await controller.show();
    resolve(link());
    await pending;
    expect(getOrCreate).toHaveBeenCalledOnce();
  });

  it("never persists or logs the link in the browser and offers no rotation", () => {
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
    expect(sources).not.toMatch(/Generar o rotar|\.rotate\(|\.issue\(/);
    const component = readFileSync(
      "src/operations/components/operations-tracking-link.tsx",
      "utf8",
    );
    expect(component).toContain('autoComplete="off"');
    expect(component).toContain("canManageTrackingLink(");
    expect(component).toContain("href={state.stepUpHref}");
    expect(component).toContain("siempre es el mismo");
    expect(component).toContain("todavía no está disponible");
    expect(component).not.toContain("window.location.origin,");
  });
});
