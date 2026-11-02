import { describe, expect, it, vi } from "vitest";
import type { DriverDocumentsApi } from "../api/driver-documents-api";
import type { DriversApi } from "../api/drivers-api";
import { TenantApiError } from "../api/tenant-request";
import type { DriverDocument, DriverDocumentPage } from "../contracts/driver-document";
import type { OrganizationDriver, OrganizationDriverPage } from "../contracts/organization-driver";
import type { OperationsSession } from "../session/operations-session";
import {
  backgroundRefreshIntervalMilliseconds,
  documentNotFoundMessage,
  documentsLoadFailedMessage,
  DriversController,
  driversLoadFailedMessage,
  driversPaginationFailedMessage,
} from "./drivers-controller";

/** UI-PHASE3-DRIVERS-2026-10-11: the states of the read-only "Repartidores" screen. */
const orgA = "11111111-1111-4111-8111-111111111111";
const orgB = "22222222-2222-4222-8222-222222222222";

function driver(id: number, reference: string, overrides: Partial<OrganizationDriver> = {}): OrganizationDriver {
  return {
    driver_id: `d71e0000-0000-4000-8000-${String(id).padStart(12, "0")}`,
    driver_reference: reference,
    vehicle_type: "MOTORCYCLE",
    status: "ACTIVE",
    active_stop_count: 0,
    last_position_at: null,
    ...overrides,
  };
}

const first = driver(1, "DRV-cccccccc", { active_stop_count: 2, last_position_at: "2026-10-11T15:04:05Z" });
const second = driver(2, "DRV-aaaaaaaa", { status: "SUSPENDED", vehicle_type: "VAN" });
const third = driver(3, "DRV-bbbbbbbb", { status: "PENDING" });

function setup(
  roles: Record<string, string>,
  list: DriversApi["list"] = vi.fn(async () => ({ items: [first, second], next_cursor: null })),
) {
  let current: OperationsSession | null = { organizationId: orgA, sessionNamespace: "synthetic", getAccessToken: () => "token" };
  let hidden = false;
  let clock = 1_000_000;
  const api: DriversApi = { list };
  const controller = new DriversController({
    readSession: () => current,
    createApi: () => api,
    loadRole: async (session) => roles[session.organizationId] ?? null,
    isHidden: () => hidden,
    now: () => clock,
  });
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

describe("drivers list states", () => {
  it.each(["DISPATCHER", "PLATFORM_ADMIN"])("loads the list for %s, by reference", async (role) => {
    const env = setup({ [orgA]: role });
    await env.controller.start();
    expect(env.list).toHaveBeenCalledWith(null, expect.any(AbortSignal));
    const state = env.controller.getSnapshot();
    expect(state).toMatchObject({ phase: "ready", loaded: true, loading: false, error: null, nextCursor: null });
    expect(state.drivers.map((item) => item.driver_reference)).toEqual(["DRV-aaaaaaaa", "DRV-cccccccc"]);
    expect(state.lastUpdated).toBe(new Date(1_000_000).toISOString());
  });

  it.each(["FINANCE", "VIEWER", "DRIVER", "BUSINESS_ADMIN", "UNKNOWN"])("never reads the list for %s", async (role) => {
    const env = setup({ [orgA]: role });
    await env.controller.start();
    expect(env.controller.getSnapshot()).toMatchObject({ phase: "access_unavailable", drivers: [] });
    expect(env.list).not.toHaveBeenCalled();
  });

  it("shows no session without one", async () => {
    const env = setup({ [orgA]: "DISPATCHER" });
    env.signOut();
    await env.controller.start();
    expect(env.controller.getSnapshot().phase).toBe("no_session");
    expect(env.list).not.toHaveBeenCalled();
  });

  it("tells an empty fleet from a pending read", async () => {
    let release: (page: OrganizationDriverPage) => void = () => undefined;
    const env = setup(
      { [orgA]: "DISPATCHER" },
      vi.fn(() => new Promise<OrganizationDriverPage>((resolve) => { release = resolve; })),
    );
    const starting = env.controller.start();
    await vi.waitFor(() => expect(env.list).toHaveBeenCalled());
    expect(env.controller.getSnapshot()).toMatchObject({ phase: "ready", loaded: false, loading: true });
    release({ items: [], next_cursor: null });
    await starting;
    expect(env.controller.getSnapshot()).toMatchObject({ loaded: true, loading: false, drivers: [] });
  });

  it("keeps the rows when a later read fails and clears the error on the next success", async () => {
    const list = vi
      .fn()
      .mockResolvedValueOnce({ items: [first], next_cursor: null })
      .mockRejectedValueOnce(new TenantApiError("unavailable"))
      .mockResolvedValueOnce({ items: [first, second], next_cursor: null });
    const env = setup({ [orgA]: "DISPATCHER" }, list);
    await env.controller.start();
    await env.controller.refresh("manual");
    expect(env.controller.getSnapshot()).toMatchObject({ error: driversLoadFailedMessage, loading: false });
    expect(env.controller.getSnapshot().drivers).toEqual([first]);
    await env.controller.refresh("manual");
    expect(env.controller.getSnapshot()).toMatchObject({ error: null });
    expect(env.controller.getSnapshot().drivers).toHaveLength(2);
  });

  it("clears everything when the API refuses access and offers the step-up when only MFA is missing", async () => {
    const refused = setup(
      { [orgA]: "DISPATCHER" },
      vi.fn().mockResolvedValueOnce({ items: [first], next_cursor: null }).mockRejectedValueOnce(new TenantApiError("forbidden")),
    );
    await refused.controller.start();
    await refused.controller.refresh("manual");
    expect(refused.controller.getSnapshot()).toMatchObject({ phase: "access_unavailable", drivers: [], role: "DISPATCHER" });

    const mfa = setup(
      { [orgA]: "PLATFORM_ADMIN" },
      vi.fn().mockRejectedValue(new TenantApiError("forbidden", "MFA_REQUIRED", true)),
    );
    await mfa.controller.start();
    const state = mfa.controller.getSnapshot();
    expect(state).toMatchObject({ phase: "ready", drivers: [] });
    expect(state.message).toContain("verificar tu identidad");
    expect(state.stepUpHref).toBe("/login?mfa=required&return_url=%2Fops%2Fdrivers");
  });

  it("appends the next page by reference and stops at a repeated cursor", async () => {
    const list = vi
      .fn()
      .mockResolvedValueOnce({ items: [first], next_cursor: "page-2" })
      .mockResolvedValueOnce({ items: [second, first], next_cursor: "page-3" })
      .mockResolvedValueOnce({ items: [third], next_cursor: "page-2" });
    const env = setup({ [orgA]: "DISPATCHER" }, list);
    await env.controller.start();
    await env.controller.loadMore();
    expect(list).toHaveBeenLastCalledWith("page-2", expect.any(AbortSignal));
    expect(env.controller.getSnapshot().drivers.map((item) => item.driver_reference)).toEqual([
      "DRV-aaaaaaaa",
      "DRV-cccccccc",
    ]);
    expect(env.controller.getSnapshot().nextCursor).toBe("page-3");
    await env.controller.loadMore();
    expect(env.controller.getSnapshot()).toMatchObject({ nextCursor: null, error: driversPaginationFailedMessage });
    expect(env.controller.getSnapshot().drivers).toHaveLength(2);
  });

  it("reads again on visibility or focus, never while hidden nor twice within a few seconds", async () => {
    const env = setup({ [orgA]: "DISPATCHER" });
    await env.controller.start();
    expect(env.list).toHaveBeenCalledTimes(1);

    await env.controller.refresh("focus");
    expect(env.list).toHaveBeenCalledTimes(1);
    env.advance(backgroundRefreshIntervalMilliseconds);
    env.hide(true);
    await env.controller.refresh("visibility");
    expect(env.list).toHaveBeenCalledTimes(1);
    env.hide(false);
    await env.controller.refresh("visibility");
    expect(env.list).toHaveBeenCalledTimes(2);
    await env.controller.refresh("focus");
    expect(env.list).toHaveBeenCalledTimes(2);
    // "Actualizar" always reads.
    await env.controller.refresh("manual");
    expect(env.list).toHaveBeenCalledTimes(3);
  });

  it("starts empty for another organization and ignores the previous one's late answer", async () => {
    let release: (page: OrganizationDriverPage) => void = () => undefined;
    const list = vi
      .fn()
      .mockImplementationOnce(() => new Promise<OrganizationDriverPage>((resolve) => { release = resolve; }))
      .mockResolvedValueOnce({ items: [third], next_cursor: null });
    const env = setup({ [orgA]: "DISPATCHER", [orgB]: "PLATFORM_ADMIN" }, list);
    const starting = env.controller.start();
    await vi.waitFor(() => expect(list).toHaveBeenCalledTimes(1));
    env.switchTo(orgB);
    await env.controller.start();
    release({ items: [first, second], next_cursor: null });
    await starting;
    expect(env.controller.getSnapshot().drivers).toEqual([third]);
    expect(env.controller.getSnapshot().role).toBe("PLATFORM_ADMIN");

    env.controller.stop();
    expect(env.controller.getSnapshot()).toMatchObject({ phase: "no_session", drivers: [] });
  });
});

/** DRV-DOC-001-IN-PERSON-2026-11-02: the documents column read with the list, and the record and revoke writes. */
describe("driver documents of the list", () => {
  const licence = (overrides: Partial<DriverDocument> = {}): DriverDocument => ({
    id: "d0c00000-0000-4000-8000-0000000000d1",
    document_type: "DRIVER_LICENSE",
    status: "VALID",
    rejection_code: null,
    expires_at: "2027-03-02T07:00:00+00:00",
    valid_through: "2027-03-01",
    expiry_basis: "PRINTED_EXPIRY",
    recorded_at: "2026-11-02T18:00:00+00:00",
    revoked_at: null,
    ...overrides,
  });
  const slots = (document: DriverDocument | null) => [
    { document_type: "IDENTITY" as const, document: null },
    { document_type: "DRIVER_LICENSE" as const, document },
  ];
  const page = (items: DriverDocumentPage["items"], next: string | null = null): DriverDocumentPage => ({
    documents_enabled: true,
    items,
    next_cursor: next,
  });

  function withDocuments(
    roles: Record<string, string>,
    documents: Partial<DriverDocumentsApi>,
    list: DriversApi["list"] = vi.fn(async () => ({ items: [first, second], next_cursor: null })),
  ) {
    const api: DriverDocumentsApi = {
      list: vi.fn(async () => page([])),
      record: vi.fn(async () => licence()),
      revoke: vi.fn(async () => licence({ status: "REVOKED", revoked_at: "2026-11-02T19:00:00+00:00" })),
      ...documents,
    };
    const controller = new DriversController({
      readSession: () => ({ organizationId: orgA, sessionNamespace: "synthetic", getAccessToken: () => "token" }),
      createApi: () => ({ list }),
      createDocumentsApi: () => api,
      loadRole: async (session) => roles[session.organizationId] ?? null,
      isHidden: () => false,
      now: () => 1_000_000,
    });
    return { controller, api, list: vi.mocked(list) };
  }

  it("shows no column while documents are off and never writes", async () => {
    const env = withDocuments({ [orgA]: "DISPATCHER" }, {
      list: vi.fn(async () => ({ documents_enabled: false, items: [], next_cursor: null })),
    });
    await env.controller.start();
    expect(env.api.list).toHaveBeenCalledWith(null, expect.any(AbortSignal));
    expect(env.controller.getSnapshot()).toMatchObject({ documentsEnabled: false, documents: {}, documentsError: null });
    const result = await env.controller.recordDocument(first.driver_id, {
      documentType: "IDENTITY",
      decision: "REJECTED",
      rejectionCode: "ILLEGIBLE",
    }, "drv-doc-001-web-key-00000000000001");
    expect(result).toMatchObject({ ok: false });
    expect(env.api.record).not.toHaveBeenCalled();
  });

  it("reads the same page of documents with every read and the next one with Cargar más", async () => {
    const documents = vi
      .fn()
      .mockResolvedValueOnce(page([{ driver_id: first.driver_id, documents: slots(licence()) }], "docs-2"))
      .mockResolvedValueOnce(page([{ driver_id: third.driver_id, documents: slots(null) }]));
    const list = vi
      .fn()
      .mockResolvedValueOnce({ items: [first], next_cursor: "page-2" })
      .mockResolvedValueOnce({ items: [third], next_cursor: null });
    const env = withDocuments({ [orgA]: "PLATFORM_ADMIN" }, { list: documents }, list);
    await env.controller.start();
    expect(env.controller.getSnapshot()).toMatchObject({ documentsEnabled: true, documentsNextCursor: "docs-2" });
    expect(Object.keys(env.controller.getSnapshot().documents)).toEqual([first.driver_id]);
    await env.controller.loadMore();
    expect(documents).toHaveBeenLastCalledWith("docs-2", expect.any(AbortSignal));
    expect(Object.keys(env.controller.getSnapshot().documents)).toEqual([first.driver_id, third.driver_id]);
    expect(env.controller.getSnapshot().documentsNextCursor).toBeNull();
  });

  it("keeps the rows and the documents when only the documents read fails", async () => {
    const documents = vi
      .fn()
      .mockResolvedValueOnce(page([{ driver_id: first.driver_id, documents: slots(licence()) }]))
      .mockRejectedValueOnce(new TenantApiError("unavailable"));
    const env = withDocuments({ [orgA]: "DISPATCHER" }, { list: documents });
    await env.controller.start();
    await env.controller.refresh("manual");
    const state = env.controller.getSnapshot();
    expect(state).toMatchObject({ error: null, documentsEnabled: true, documentsError: documentsLoadFailedMessage });
    expect(state.drivers).toHaveLength(2);
    expect(state.documents[first.driver_id]).toEqual(slots(licence()));
  });

  it("clears everything when the documents read is refused", async () => {
    const env = withDocuments({ [orgA]: "DISPATCHER" }, {
      list: vi.fn().mockRejectedValue(new TenantApiError("forbidden")),
    });
    await env.controller.start();
    expect(env.controller.getSnapshot()).toMatchObject({ phase: "access_unavailable", drivers: [], documents: {} });
  });

  it("never reads documents for a role outside the matrix", async () => {
    const env = withDocuments({ [orgA]: "VIEWER" }, {});
    await env.controller.start();
    expect(env.api.list).not.toHaveBeenCalled();
    expect(env.list).not.toHaveBeenCalled();
  });

  it("replaces the slot with the API's answer after a record and a revoke", async () => {
    const recorded = licence({ id: "d0c00000-0000-4000-8000-0000000000d2", valid_through: "2027-06-30", expires_at: "2027-07-01T07:00:00+00:00" });
    const revoked = { ...recorded, status: "REVOKED" as const, revoked_at: "2026-11-02T19:00:00+00:00" };
    const env = withDocuments({ [orgA]: "DISPATCHER" }, {
      list: vi.fn(async () => page([{ driver_id: first.driver_id, documents: slots(licence()) }])),
      record: vi.fn(async () => recorded),
      revoke: vi.fn(async () => revoked),
    });
    await env.controller.start();
    const input = { documentType: "DRIVER_LICENSE", decision: "VALID", permanentLicense: false, printedExpiry: "2027-06-30" } as const;
    expect(await env.controller.recordDocument(first.driver_id, input, "drv-doc-001-web-key-00000000000002")).toEqual({
      ok: true,
      document: recorded,
    });
    expect(env.api.record).toHaveBeenCalledWith(first.driver_id, input, "drv-doc-001-web-key-00000000000002", expect.any(AbortSignal));
    expect(env.controller.getSnapshot().documents[first.driver_id]).toEqual(slots(recorded));
    expect(await env.controller.revokeDocument(first.driver_id, recorded.id)).toEqual({ ok: true, document: revoked });
    expect(env.api.revoke).toHaveBeenCalledWith(first.driver_id, recorded.id, expect.any(AbortSignal));
    expect(env.controller.getSnapshot().documents[first.driver_id]).toEqual(slots(revoked));
  });

  it("explains a refused write by its code, keeps the documents, and marks only an uncertain one retryable", async () => {
    const record = vi
      .fn()
      .mockRejectedValueOnce(new TenantApiError("conflict", "EXPIRY_DATE_OUT_OF_RANGE"))
      .mockRejectedValueOnce(new TenantApiError("network"))
      .mockRejectedValueOnce(new TenantApiError("not_found"));
    const revoke = vi.fn().mockRejectedValueOnce(new TenantApiError("conflict", "DOCUMENT_NOT_REVOCABLE"));
    const env = withDocuments({ [orgA]: "DISPATCHER" }, {
      list: vi.fn(async () => page([{ driver_id: first.driver_id, documents: slots(licence()) }])),
      record,
      revoke,
    });
    await env.controller.start();
    const input = { documentType: "DRIVER_LICENSE", decision: "VALID", permanentLicense: false, printedExpiry: "2020-01-01" } as const;
    expect(await env.controller.recordDocument(first.driver_id, input, "drv-doc-001-web-key-00000000000003")).toEqual({
      ok: false,
      message: "La fecha ya pasó o está a más de 10 años; si ya venció, recházalo como Vencido",
      retryable: false,
    });
    expect(await env.controller.recordDocument(first.driver_id, input, "drv-doc-001-web-key-00000000000003")).toMatchObject({
      ok: false,
      retryable: true,
    });
    expect(await env.controller.recordDocument(first.driver_id, input, "drv-doc-001-web-key-00000000000004")).toEqual({
      ok: false,
      message: documentNotFoundMessage,
      retryable: false,
    });
    expect(await env.controller.revokeDocument(first.driver_id, licence().id)).toEqual({
      ok: false,
      message: "Ese documento ya no es el vigente. Actualiza la lista.",
      retryable: false,
    });
    expect(env.controller.getSnapshot().documents[first.driver_id]).toEqual(slots(licence()));
  });

  it("clears everything when a write is refused for the role", async () => {
    const env = withDocuments({ [orgA]: "DISPATCHER" }, {
      list: vi.fn(async () => page([{ driver_id: first.driver_id, documents: slots(licence()) }])),
      revoke: vi.fn().mockRejectedValue(new TenantApiError("forbidden")),
    });
    await env.controller.start();
    expect(await env.controller.revokeDocument(first.driver_id, licence().id)).toMatchObject({ ok: false, retryable: false });
    expect(env.controller.getSnapshot()).toMatchObject({ phase: "access_unavailable", drivers: [], documents: {} });
  });
});
