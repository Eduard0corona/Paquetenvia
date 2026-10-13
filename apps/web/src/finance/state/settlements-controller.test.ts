import { afterEach, describe, expect, it, vi } from "vitest";
import { rateLimitWaits } from "../../lib/rate-limit-wait";
import { TenantApiError } from "../../operations/api/tenant-request";
import type { OperationsSession } from "../../operations/session/operations-session";
import { PendingSubmissions } from "../../operations/state/pending-submissions";
import type { SettlementsApi } from "../api/settlements-api";
import { parseSettlement } from "../contracts/settlement";
import { driverId, settlementId, settlementResponse } from "../contracts/settlement.fixtures";
import {
  actionNeedsMfa,
  maximumPayeePages,
  payeeLabel,
  SettlementsController,
  settlementExportRateLimit,
  settlementMfaHint,
  visibleSettlementActions,
} from "./settlements-controller";

const orgA = "11111111-1111-4111-8111-111111111111";
const orgB = "22222222-2222-4222-8222-222222222222";
/** UI-PHASE3-DRIVERS-2026-10-11: the fixture settlement's payee as listSettlementPayees returns it. */
const payee = { driver_id: driverId, driver_reference: "DRV-3d93b4c6", vehicle_type: "MOTORCYCLE" } as const;
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
    listPayees: vi.fn(async () => ({ items: [payee], next_cursor: null })),
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

  it("validates a calculation before its confirmation without sending anything", async () => {
    const { controller, api } = setup({ [orgA]: "FINANCE" });
    await controller.start();
    const body = { driver_id: driverId, period_from: "2026-09-01", period_to: "2026-09-15" };

    expect(controller.prepareCreate({ ...body, period_to: "2026-08-31" })).toBeNull();
    expect(controller.getSnapshot().errors).toEqual(["El fin del periodo no puede ser anterior al inicio."]);
    expect(controller.prepareCreate({ ...body, driver_id: "no-es-uuid" })).toBeNull();

    expect(controller.prepareCreate(body)).toEqual(body);
    expect(controller.getSnapshot().errors).toEqual([]);
    expect(api.create).not.toHaveBeenCalled();

    // Confirmed: the same body is sent once.
    await controller.create(body);
    expect(api.create).toHaveBeenCalledOnce();
    expect(vi.mocked(api.create).mock.calls[0][0]).toEqual(body);
  });

  it.each(["DISPATCHER", "VIEWER"])("never prepares a calculation for %s", async (role) => {
    const { controller, api } = setup({ [orgA]: role });
    await controller.start();
    expect(
      controller.prepareCreate({ driver_id: driverId, period_from: "2026-09-01", period_to: "2026-09-15" }),
    ).toBeNull();
    expect(api.create).not.toHaveBeenCalled();
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

describe("payee picker and DRV- names (UI-PHASE3-DRIVERS-2026-10-11)", () => {
  const other = { driver_id: "d71e0000-0000-4000-8000-0000000000aa", driver_reference: "DRV-00aa11bb", vehicle_type: "VAN" } as const;
  const later = { driver_id: "d71e0000-0000-4000-8000-0000000000bb", driver_reference: "DRV-ffee0011", vehicle_type: "CAR" } as const;

  it("reads every payee page with the list and names each settlement's payee by its reference", async () => {
    const listPayees = vi
      .fn()
      .mockResolvedValueOnce({ items: [later, payee], next_cursor: "page-2" })
      .mockResolvedValueOnce({ items: [other], next_cursor: null });
    const { controller, api } = setup({ [orgA]: "FINANCE" }, { listPayees });
    await controller.start();
    expect(listPayees.mock.calls.map((call) => call[0])).toEqual([null, "page-2"]);
    const state = controller.getSnapshot();
    expect(state).toMatchObject({ payeesStatus: "ready", payeesTruncated: false });
    // Sorted by reference for the picker; the API pages by id.
    expect(state.payees.map((item) => item.driver_reference)).toEqual(["DRV-00aa11bb", "DRV-3d93b4c6", "DRV-ffee0011"]);
    expect(payeeLabel(state, driverId)).toBe("DRV-3d93b4c6");
    expect(payeeLabel(state, "d71e0000-0000-4000-8000-0000000000ff")).toBe("sin referencia");
    expect(controller.payee(other.driver_id)).toEqual(other);
    expect(api.list).toHaveBeenCalledOnce();
  });

  it("keeps the settlement list working and says so when the payee list fails", async () => {
    const { controller } = setup(
      { [orgA]: "FINANCE" },
      { listPayees: vi.fn().mockRejectedValue(new TenantApiError("unavailable")) },
    );
    await controller.start();
    const state = controller.getSnapshot();
    expect(state).toMatchObject({ phase: "ready", payeesStatus: "failed", payees: [] });
    expect(state.items).toHaveLength(1);
    expect(payeeLabel(state, driverId)).toBe("sin referencia");
    expect(payeeLabel({ payees: [], payeesStatus: "loading" }, driverId)).toBe("…");
  });

  it("stops at a repeated payee cursor and cuts a list longer than the pages it reads", async () => {
    const cycling = setup(
      { [orgA]: "FINANCE" },
      { listPayees: vi.fn().mockResolvedValue({ items: [payee], next_cursor: "same" }) },
    );
    await cycling.controller.start();
    expect(cycling.controller.getSnapshot().payeesStatus).toBe("failed");
    expect(cycling.api.listPayees).toHaveBeenCalledTimes(2);

    let page = 0;
    const endless = setup(
      { [orgA]: "FINANCE" },
      { listPayees: vi.fn(async () => ({ items: [payee], next_cursor: `page-${++page}` })) },
    );
    await endless.controller.start();
    expect(endless.api.listPayees).toHaveBeenCalledTimes(maximumPayeePages);
    expect(endless.controller.getSnapshot()).toMatchObject({ payeesStatus: "ready", payeesTruncated: true });
  });

  it("calculates only for a driver the payee list returned", async () => {
    const { controller, api } = setup({ [orgA]: "FINANCE" });
    await controller.start();
    const body = { driver_id: driverId, period_from: "2026-09-28", period_to: "2026-10-04" };
    expect(controller.prepareCreate({ ...body, driver_id: "" })).toBeNull();
    expect(controller.getSnapshot().errors).toEqual(["Elige al repartidor que vas a liquidar."]);
    expect(controller.prepareCreate({ ...body, driver_id: other.driver_id })).toBeNull();
    expect(controller.getSnapshot().errors).toEqual(["Elige un repartidor de la lista."]);
    expect(controller.prepareCreate(body)).toEqual(body);
    await controller.create(body);
    expect(api.create).toHaveBeenCalledWith(body, expect.any(String), expect.any(AbortSignal));
  });

  it("filters by a picked payee and reloads the payees with Actualizar", async () => {
    const { controller, api } = setup({ [orgA]: "FINANCE" });
    await controller.start();
    await controller.applyFilters({ payeeId: driverId });
    expect(api.list).toHaveBeenLastCalledWith({ payeeId: driverId }, expect.any(AbortSignal));
    await controller.reload();
    expect(api.listPayees).toHaveBeenCalledTimes(2);
    expect(api.list).toHaveBeenCalledTimes(3);
  });

  it("never reads payees without the capability and forgets them on a tenant switch", async () => {
    const dispatcher = setup({ [orgA]: "DISPATCHER" });
    await dispatcher.controller.start();
    expect(dispatcher.api.listPayees).not.toHaveBeenCalled();

    const env = setup({ [orgA]: "FINANCE", [orgB]: "VIEWER" });
    await env.controller.start();
    expect(env.controller.getSnapshot().payees).toHaveLength(1);
    env.switchTo(orgB);
    await env.controller.start();
    expect(env.controller.getSnapshot()).toMatchObject({ phase: "access_unavailable", payees: [], payeesStatus: "idle" });
  });

  it("asks a PLATFORM_ADMIN for MFA on every settlement operation, the payee list included", () => {
    expect(settlementMfaHint("PLATFORM_ADMIN", [])).toBe("Operar liquidaciones requiere verificar tu identidad (MFA).");
    expect(settlementMfaHint("FINANCE", [])).toBeNull();
  });
});

describe("SEC-004 export limit (owner answer Q6, 'Mensaje común con espera')", () => {
  afterEach(() => rateLimitWaits.clear());

  it("shows no message of its own on a 429 and sends nothing until the server's wait ends", async () => {
    const exportCsv = vi.fn<SettlementsApi["exportCsv"]>(async () => {
      rateLimitWaits.record("settlement_export", 30);
      throw new TenantApiError("rate_limited", null, false, 30);
    });
    const { controller, download } = setup({ [orgA]: "FINANCE" }, { exportCsv });
    await controller.start();
    await controller.select(settlementId);
    await controller.exportCsv();
    expect(controller.getSnapshot()).toMatchObject({ message: null, stepUpHref: null, busy: false });
    expect(rateLimitWaits.secondsLeft(settlementExportRateLimit)).toBe(30);

    await controller.exportCsv();
    expect(exportCsv).toHaveBeenCalledOnce();

    rateLimitWaits.clear();
    exportCsv.mockResolvedValueOnce({ filename: `settlement-${settlementId}.csv`, content: new Blob(["x"]) });
    await controller.exportCsv();
    expect(exportCsv).toHaveBeenCalledTimes(2);
    expect(download).toHaveBeenCalledOnce();
  });
});
