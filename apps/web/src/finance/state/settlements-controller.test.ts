import { describe, expect, it, vi } from "vitest";
import { TenantApiError } from "../../operations/api/tenant-request";
import type { OperationsSession } from "../../operations/session/operations-session";
import { PendingSubmissions } from "../../operations/state/pending-submissions";
import type { SettlementsApi } from "../api/settlements-api";
import { parseSettlement } from "../contracts/settlement";
import { settlementId, settlementResponse } from "../contracts/settlement.fixtures";
import {
  actionNeedsMfa,
  SettlementsController,
  settlementMfaHint,
  visibleSettlementActions,
} from "./settlements-controller";

const orgA = "11111111-1111-4111-8111-111111111111";
const orgB = "22222222-2222-4222-8222-222222222222";
const calculated = () => parseSettlement(settlementResponse());
const approved = () => parseSettlement(settlementResponse({ status: "APPROVED" }));

function setup(
  roles: Record<string, string>,
  overrides: Partial<SettlementsApi> = {},
  initialSelection: string | null = null,
) {
  let current: OperationsSession | null = {
    organizationId: orgA,
    sessionNamespace: "synthetic",
    getAccessToken: () => "token",
  };
  const api: SettlementsApi = {
    list: vi.fn(async () => ({ items: [calculated()], next_cursor: null })),
    get: vi.fn(async () => calculated()),
    create: vi.fn(async () => calculated()),
    addAdjustment: vi.fn(async () => calculated()),
    approve: vi.fn(async () => approved()),
    markPaid: vi.fn(async () => parseSettlement(settlementResponse({ status: "PAID" }))),
    void: vi.fn(async () => parseSettlement(settlementResponse({ status: "VOID" }))),
    exportCsv: vi.fn(async () => ({ filename: `settlement-${settlementId}.csv`, content: new Blob(["x"]) })),
    ...overrides,
  };
  const download = vi.fn();
  const pending = new PendingSubmissions();
  const controller = new SettlementsController(
    {
      readSession: () => current,
      createApi: () => api,
      loadRole: async (session) => roles[session.organizationId] ?? null,
      download,
      initialSelection: () => initialSelection,
    },
    pending,
  );
  return {
    controller,
    api,
    download,
    pending,
    switchTo(organizationId: string) {
      current = { organizationId, sessionNamespace: "synthetic", getAccessToken: () => "token" };
    },
  };
}

describe("settlement capability gating (D5-CAPABILITY-MATRIX)", () => {
  it.each(["FINANCE", "PLATFORM_ADMIN"])("loads the REST list for %s", async (role) => {
    const { controller, api } = setup({ [orgA]: role });
    await controller.start();
    expect(api.list).toHaveBeenCalledOnce();
    expect(controller.getSnapshot()).toMatchObject({ phase: "ready", canCreate: true });
    expect(controller.getSnapshot().items).toHaveLength(1);
  });

  it.each(["DISPATCHER", "VIEWER", "DRIVER", "CUSTOMER_SUPPORT"])(
    "never reads settlements for %s",
    async (role) => {
      const { controller, api } = setup({ [orgA]: role });
      await controller.start();
      expect(controller.getSnapshot().phase).toBe("access_unavailable");
      expect(api.list).not.toHaveBeenCalled();
      expect(controller.getSnapshot().items).toEqual([]);
    },
  );

  it("shows only the actions the role and the status admit", () => {
    expect(visibleSettlementActions("FINANCE", "CALCULATED")).toEqual(["adjust", "approve", "void", "export"]);
    expect(visibleSettlementActions("FINANCE", "APPROVED")).toEqual(["pay", "void", "export"]);
    expect(visibleSettlementActions("PLATFORM_ADMIN", "PAID")).toEqual(["export"]);
    expect(visibleSettlementActions("DISPATCHER", "CALCULATED")).toEqual([]);
    expect(visibleSettlementActions("VIEWER", "APPROVED")).toEqual([]);
    expect(visibleSettlementActions(null, "CALCULATED")).toEqual([]);
  });

  it("hints MFA for approve and pay", () => {
    expect(actionNeedsMfa("FINANCE", "approve")).toBe(true);
    expect(actionNeedsMfa("FINANCE", "pay")).toBe(true);
    expect(actionNeedsMfa("FINANCE", "adjust")).toBe(false);
    expect(actionNeedsMfa("PLATFORM_ADMIN", "export")).toBe(true);
  });

  it("words the MFA hint for the role's actual MFA scope", () => {
    const approveAndPay = "Aprobar y marcar pagada requieren verificar tu identidad (MFA).";
    const everyOperation = "Operar liquidaciones requiere verificar tu identidad (MFA).";
    expect(settlementMfaHint("FINANCE", visibleSettlementActions("FINANCE", "CALCULATED"))).toBe(approveAndPay);
    expect(settlementMfaHint("FINANCE", ["adjust", "export"])).toBeNull();
    expect(settlementMfaHint("PLATFORM_ADMIN", visibleSettlementActions("PLATFORM_ADMIN", "CALCULATED")))
      .toBe(everyOperation);
    expect(settlementMfaHint("PLATFORM_ADMIN", [])).toBe(everyOperation);
    expect(settlementMfaHint("VIEWER", [])).toBeNull();
    expect(settlementMfaHint(null, [])).toBeNull();
  });
});

describe("MFA step-up (D7-SETTLEMENT-RULES, AUTH-001-MFA-STEP-UP)", () => {
  it("offers Verificar identidad returning to the same settlement, then retries after step-up", async () => {
    const mfa = new TenantApiError("forbidden", "MFA_REQUIRED", true);
    const first = setup({ [orgA]: "FINANCE" }, { approve: vi.fn().mockRejectedValue(mfa) });
    await first.controller.start();
    await first.controller.select(settlementId);
    await first.controller.approve();

    const state = first.controller.getSnapshot();
    expect(state.stepUpHref).toBe(
      `/login?mfa=required&return_url=%2Ffinance%2Fsettlements%3Fsettlement%3D${settlementId}`,
    );
    expect(state.message).toContain("verificar tu identidad");
    expect(state.selected?.status).toBe("CALCULATED");
    // The API refused before any idempotency record: no key is kept for it.
    expect(first.pending.size).toBe(0);

    // The step-up replaces the session; the page reopens the settlement from REST.
    const second = setup({ [orgA]: "FINANCE" }, {}, settlementId);
    await second.controller.start();
    expect(second.api.get).toHaveBeenCalledWith(settlementId, expect.any(AbortSignal));
    await second.controller.approve();
    expect(second.controller.getSnapshot()).toMatchObject({
      stepUpHref: null,
      selected: { status: "APPROVED" },
    });
    expect(second.api.list).toHaveBeenCalledTimes(2);
  });

  it("does not offer a step-up for a generic 403", async () => {
    const { controller } = setup(
      { [orgA]: "FINANCE" },
      { markPaid: vi.fn().mockRejectedValue(new TenantApiError("forbidden")) },
    );
    await controller.start();
    await controller.select(settlementId);
    await controller.markPaid();
    expect(controller.getSnapshot().stepUpHref).toBeNull();
  });
});

describe("authoritative writes", () => {
  it("sends the adjustment as integer cents and replaces the settlement with the response", async () => {
    const { controller, api } = setup({ [orgA]: "FINANCE" });
    await controller.start();
    await controller.select(settlementId);
    await controller.addAdjustment("-5.00", "Descuento acordado");
    expect(api.addAdjustment).toHaveBeenCalledWith(
      settlementId,
      { amount_cents: -500, reason: "Descuento acordado" },
      expect.stringMatching(/^[0-9a-f-]{36}$/),
      expect.any(AbortSignal),
    );
  });

  it.each([["0", "Motivo"], ["1.001", "Motivo"], ["10", " Motivo"], ["abc", "Motivo"]])(
    "refuses adjustment %s / %j without a request",
    async (amount, reason) => {
      const { controller, api } = setup({ [orgA]: "FINANCE" });
      await controller.start();
      await controller.select(settlementId);
      await controller.addAdjustment(amount, reason);
      expect(api.addAdjustment).not.toHaveBeenCalled();
      expect(controller.getSnapshot().errors.length).toBeGreaterThan(0);
    },
  );

  it("reuses the key after a transport failure and renews it after a definitive answer", async () => {
    const markPaid = vi
      .fn()
      .mockRejectedValueOnce(new TenantApiError("network"))
      .mockRejectedValueOnce(new TenantApiError("conflict", "SETTLEMENT_STATE_CONFLICT"))
      .mockResolvedValueOnce(parseSettlement(settlementResponse({ status: "PAID" })));
    const { controller, api } = setup({ [orgA]: "FINANCE" }, { markPaid, get: vi.fn(async () => approved()) });
    await controller.start();
    await controller.select(settlementId);
    await controller.markPaid();
    await controller.markPaid();
    await controller.markPaid();
    const keys = markPaid.mock.calls.map((call) => call[1]);
    expect(keys[0]).toBe(keys[1]);
    expect(keys[2]).not.toBe(keys[1]);
    expect(api.get).toHaveBeenCalledTimes(2);
  });

  it("reloads the settlement from REST after a state conflict", async () => {
    const get = vi.fn().mockResolvedValueOnce(calculated()).mockResolvedValueOnce(approved());
    const { controller } = setup(
      { [orgA]: "FINANCE" },
      { get, approve: vi.fn().mockRejectedValue(new TenantApiError("conflict", "SETTLEMENT_STATE_CONFLICT")) },
    );
    await controller.start();
    await controller.select(settlementId);
    await controller.approve();
    expect(controller.getSnapshot().selected?.status).toBe("APPROVED");
    expect(controller.getSnapshot().message).toContain("estado actual");
  });

  it("explains why approval is blocked", async () => {
    const { controller } = setup(
      { [orgA]: "FINANCE" },
      { approve: vi.fn().mockRejectedValue(new TenantApiError("conflict", "CASH_PENDING")) },
    );
    await controller.start();
    await controller.select(settlementId);
    await controller.approve();
    expect(controller.getSnapshot().message).toContain("efectivo contra entrega");
  });

  it("hands the export to the browser without keeping it in state", async () => {
    const { controller, download } = setup({ [orgA]: "FINANCE" });
    await controller.start();
    await controller.select(settlementId);
    await controller.exportCsv();
    expect(download).toHaveBeenCalledOnce();
    expect(JSON.stringify(controller.getSnapshot())).not.toContain("settlement-");
  });
});

describe("selection sequencing", () => {
  const idA = "5e7e1e00-0000-4000-8000-00000000000a";
  const idB = "5e7e1e00-0000-4000-8000-00000000000b";

  function deferredGet() {
    const releases = new Map<string, (value: unknown) => void>();
    const get = vi.fn(
      (id: string) => new Promise((resolve) => { releases.set(id, resolve); }),
    ) as unknown as SettlementsApi["get"];
    const release = (id: string, status = "CALCULATED") =>
      releases.get(id)!(parseSettlement(settlementResponse({ id, status })));
    return { get, release };
  }

  it("keeps the latest selection when an older GET resolves last", async () => {
    const { get, release } = deferredGet();
    const { controller } = setup({ [orgA]: "FINANCE" }, { get });
    await controller.start();
    const first = controller.select(idA);
    const second = controller.select(idB);
    release(idB);
    await second;
    release(idA);
    await first;
    expect(controller.getSnapshot()).toMatchObject({ selected: { id: idB }, selecting: null, loading: false });
  });

  it("never lets an action target a settlement while another selection is in flight", async () => {
    const { get, release } = deferredGet();
    const { controller, api } = setup({ [orgA]: "FINANCE" }, { get });
    await controller.start();
    const first = controller.select(idA);
    release(idA);
    await first;
    const second = controller.select(idB);
    expect(controller.getSnapshot()).toMatchObject({ selected: { id: idA }, selecting: idB });
    await controller.approve();
    await controller.voidSettlement("Motivo");
    await controller.addAdjustment("1.00", "Motivo");
    await controller.exportCsv();
    expect(api.approve).not.toHaveBeenCalled();
    expect(api.void).not.toHaveBeenCalled();
    expect(api.addAdjustment).not.toHaveBeenCalled();
    expect(api.exportCsv).not.toHaveBeenCalled();
    release(idB);
    await second;
    await controller.approve();
    expect(api.approve).toHaveBeenCalledWith(idB, expect.any(String), expect.any(AbortSignal));
  });

  it("drops a late GET after the selection is closed", async () => {
    const { get, release } = deferredGet();
    const { controller } = setup({ [orgA]: "FINANCE" }, { get });
    await controller.start();
    const pending = controller.select(idA);
    controller.clearSelection();
    release(idA);
    await pending;
    expect(controller.getSnapshot()).toMatchObject({ selected: null, selecting: null });
  });

  it("does not let a write result replace a selection made meanwhile", async () => {
    let finish: (value: unknown) => void = () => undefined;
    const approve = vi.fn(() => new Promise((resolve) => { finish = resolve; })) as unknown as SettlementsApi["approve"];
    const get = vi.fn(async (id: string) => parseSettlement(settlementResponse({ id }))) as unknown as SettlementsApi["get"];
    const { controller } = setup({ [orgA]: "FINANCE" }, { approve, get });
    await controller.start();
    await controller.select(idA);
    const writing = controller.approve();
    await controller.select(idB);
    finish(parseSettlement(settlementResponse({ id: idA, status: "APPROVED" })));
    await writing;
    expect(controller.getSnapshot().selected?.id).toBe(idB);
    expect(controller.getSnapshot().message).toBeNull();
  });
});

describe("tenant switch", () => {
  it("clears list, selection, messages and keys, and ignores the previous tenant's late answers", async () => {
    let release: (value: unknown) => void = () => undefined;
    const approve = vi.fn(() => new Promise((resolve) => { release = resolve; })) as unknown as SettlementsApi["approve"];
    const env = setup({ [orgA]: "FINANCE", [orgB]: "DISPATCHER" }, { approve });
    await env.controller.start();
    await env.controller.select(settlementId);
    const inFlight = env.controller.approve();
    expect(env.pending.size).toBe(1);

    env.switchTo(orgB);
    await env.controller.start();
    release(approved());
    await inFlight;

    expect(env.pending.size).toBe(0);
    expect(env.controller.getSnapshot()).toMatchObject({
      phase: "access_unavailable",
      role: "DISPATCHER",
      items: [],
      selected: null,
      message: null,
      stepUpHref: null,
      busy: false,
    });
  });

  it("reloads the new tenant's settlements from REST", async () => {
    const env = setup({ [orgA]: "FINANCE", [orgB]: "FINANCE" });
    await env.controller.start();
    await env.controller.select(settlementId);
    await env.controller.applyFilters({ status: "CALCULATED" });
    env.switchTo(orgB);
    await env.controller.start();
    expect(env.controller.getSnapshot()).toMatchObject({ phase: "ready", selected: null, filters: {} });
    expect(env.api.list).toHaveBeenLastCalledWith({}, expect.any(AbortSignal));
  });
});
