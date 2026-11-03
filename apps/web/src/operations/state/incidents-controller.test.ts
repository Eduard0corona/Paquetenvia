import { describe, expect, it, vi } from "vitest";
import type { IncidentsApi } from "../api/incidents-api";
import { TenantApiError } from "../api/tenant-request";
import { parseIncident, parseProof, type OpenIncidentDraft } from "../contracts/incident";
import {
  bearerSession,
  incidentId,
  incidentResponse,
  orderId,
  orgA,
  orgB,
  proofId,
  proofResponse,
  syntheticUuid,
} from "../contracts/ui-001-screens.fixtures";
import type { OperationsSession } from "../session/operations-session";
import { IncidentsController } from "./incidents-controller";
import { PendingSubmissions } from "./pending-submissions";

const draft: OpenIncidentDraft = {
  orderId,
  type: "FAILED_ATTEMPT",
  severity: "LOW",
  reasonCode: "ADDRESS_NOT_FOUND",
  nextAction: "RETURNING",
  description: "Domicilio inexistente.",
  occurredAtLocal: "2026-09-28T09:00",
  evidence: proofId,
};

function setup(roles: Record<string, string>, overrides: Partial<IncidentsApi> = {}) {
  let current: OperationsSession | null = bearerSession(orgA);
  const api: IncidentsApi = {
    list: vi.fn(async () => ({ items: [], next_cursor: null })),
    get: vi.fn(async () => parseIncident(incidentResponse())),
    listOrderProofs: vi.fn(async () => ({ items: [], next_cursor: null })),
    open: vi.fn(async () => parseIncident(incidentResponse())),
    resolve: vi.fn(async () => parseIncident(incidentResponse({ status: "RESOLVED" }))),
    ...overrides,
  };
  const pending = new PendingSubmissions();
  const controller = new IncidentsController(
    {
      readSession: () => current,
      createApi: () => api,
      loadRole: async (session) => roles[session.organizationId] ?? null,
      now: () => new Date("2026-09-28T17:00:00Z"),
    },
    pending,
  );
  return {
    controller,
    api,
    pending,
    switchTo(organizationId: string) {
      current = bearerSession(organizationId);
    },
  };
}

describe("incident capability gating", () => {
  it("admits DISPATCHER without an MFA hint", async () => {
    const { controller } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    expect(controller.getSnapshot()).toMatchObject({ phase: "ready", canOpen: true, canResolve: true, mfaHint: null });
  });

  it("admits PLATFORM_ADMIN with the MFA hint", async () => {
    const { controller } = setup({ [orgA]: "PLATFORM_ADMIN" });
    await controller.start();
    expect(controller.getSnapshot().mfaHint).toContain("MFA");
  });

  it.each(["VIEWER", "FINANCE", "DRIVER", "CUSTOMER_SUPPORT"])("never writes for %s", async (role) => {
    const { controller, api } = setup({ [orgA]: role });
    await controller.start();
    await controller.open(draft);
    await controller.resolve(incidentId, "RESOLVED", "ok");
    expect(controller.getSnapshot().phase).toBe("access_unavailable");
    expect(api.open).not.toHaveBeenCalled();
    expect(api.resolve).not.toHaveBeenCalled();
    await controller.refresh(null);
    await controller.loadProofs(orderId);
    expect(api.list).not.toHaveBeenCalled();
    expect(api.listOrderProofs).not.toHaveBeenCalled();
  });
});

describe("incident desk reads (API-INC-LIST-PROOFS-2026-09-29)", () => {
  const second = syntheticUuid(0x302);

  it("lists the organization incidents on start and pages with next_cursor", async () => {
    const list = vi
      .fn<IncidentsApi["list"]>()
      .mockResolvedValueOnce({ items: [parseIncident(incidentResponse())], next_cursor: "c1" })
      .mockResolvedValueOnce({
        items: [parseIncident(incidentResponse()), parseIncident(incidentResponse({ id: second, status: "RESOLVED" }))],
        next_cursor: null,
      });
    const { controller } = setup({ [orgA]: "DISPATCHER" }, { list });
    await controller.start();
    expect(list.mock.calls[0].slice(0, 2)).toEqual([{}, null]);
    expect(controller.getSnapshot()).toMatchObject({ canList: true, canListProofs: true, nextCursor: "c1" });
    await controller.loadMore();
    expect(list.mock.calls[1].slice(0, 2)).toEqual([{}, "c1"]);
    // An incident already shown is never duplicated by a later page.
    expect(controller.getSnapshot().incidents.map((incident) => incident.id)).toEqual([incidentId, second]);
    expect(controller.getSnapshot().nextCursor).toBeNull();
    await controller.loadMore();
    expect(list).toHaveBeenCalledTimes(2);
  });

  it("filters by status and ignores a status outside the vocabulary", async () => {
    const { controller, api } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    await controller.refresh("OPEN");
    expect(vi.mocked(api.list).mock.calls.at(-1)?.slice(0, 2)).toEqual([{ status: "OPEN" }, null]);
    expect(controller.getSnapshot().statusFilter).toBe("OPEN");
    await controller.refresh("CLOSED" as never);
    expect(api.list).toHaveBeenCalledTimes(2);
  });

  it("filters by order and status and keeps both filters for the next page (AI-07 incident_desk)", async () => {
    const list = vi
      .fn<IncidentsApi["list"]>()
      .mockResolvedValueOnce({ items: [], next_cursor: null })
      .mockResolvedValueOnce({ items: [parseIncident(incidentResponse())], next_cursor: "c1" })
      .mockResolvedValue({ items: [], next_cursor: null });
    const { controller } = setup({ [orgA]: "DISPATCHER" }, { list });
    await controller.start();
    expect(controller.getSnapshot().orderFilter).toBeNull();
    await controller.refresh("OPEN", orderId);
    expect(list.mock.calls[1].slice(0, 2)).toEqual([{ status: "OPEN", orderId }, null]);
    expect(controller.getSnapshot()).toMatchObject({ statusFilter: "OPEN", orderFilter: orderId, nextCursor: "c1" });
    await controller.loadMore();
    expect(list.mock.calls[2].slice(0, 2)).toEqual([{ status: "OPEN", orderId }, "c1"]);
    // "Actualizar" without arguments keeps the filters in effect.
    await controller.refresh();
    expect(list.mock.calls[3].slice(0, 2)).toEqual([{ status: "OPEN", orderId }, null]);
    // The order alone, then every order and status again.
    await controller.refresh(null, orderId);
    expect(list.mock.calls[4].slice(0, 2)).toEqual([{ orderId }, null]);
    await controller.refresh(null, null);
    expect(list.mock.calls[5].slice(0, 2)).toEqual([{}, null]);
    expect(controller.getSnapshot()).toMatchObject({ statusFilter: null, orderFilter: null });
  });

  it.each([
    "ORD_CobroPlaywright0000001",
    syntheticUuid(0xabc).toUpperCase(),
    "00000000-0000-0000-0000-000000000000",
    ` ${orderId}`,
  ])("refuses the order filter %j before any request", async (typed) => {
    const { controller, api } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    await controller.refresh("OPEN", orderId);
    await controller.refresh(null, typed);
    expect(api.list).toHaveBeenCalledTimes(2);
    expect(controller.getSnapshot()).toMatchObject({
      errors: ["El ID de la orden no es válido."],
      statusFilter: "OPEN",
      orderFilter: orderId,
    });
  });

  it("offers the proofs of the typed order and never calls the API for a malformed order", async () => {
    const listOrderProofs = vi
      .fn<IncidentsApi["listOrderProofs"]>()
      .mockResolvedValue({ items: [parseProof(proofResponse())], next_cursor: null });
    const { controller } = setup({ [orgA]: "DISPATCHER" }, { listOrderProofs });
    await controller.start();
    await controller.loadProofs("../orders");
    expect(listOrderProofs).not.toHaveBeenCalled();
    expect(controller.getSnapshot().errors.length).toBeGreaterThan(0);
    await controller.loadProofs(orderId);
    expect(listOrderProofs.mock.calls[0].slice(0, 2)).toEqual([orderId, null]);
    expect(controller.getSnapshot()).toMatchObject({ proofsOrderId: orderId, proofsCursor: null });
    expect(controller.getSnapshot().proofs.map((proof) => proof.id)).toEqual([proofId]);
    // The opening consumes the picked evidence.
    await controller.open(draft);
    expect(controller.getSnapshot()).toMatchObject({ proofsOrderId: null, proofs: [] });
  });

  it("offers the MFA step-up when the listing needs it", async () => {
    const { controller } = setup(
      { [orgA]: "PLATFORM_ADMIN" },
      { list: vi.fn(async () => { throw new TenantApiError("forbidden", "MFA_REQUIRED", true); }) },
    );
    await controller.start();
    expect(controller.getSnapshot()).toMatchObject({ phase: "ready", incidents: [], listing: false });
    expect(controller.getSnapshot().stepUpHref).toContain("mfa=required");
  });

  it("drops a listing that arrives after a tenant switch", async () => {
    let release: (value: Awaited<ReturnType<IncidentsApi["list"]>>) => void = () => undefined;
    const list = vi
      .fn<IncidentsApi["list"]>()
      .mockImplementationOnce(() => new Promise((resolve) => { release = resolve; }))
      .mockResolvedValue({ items: [], next_cursor: null });
    const context = setup({ [orgA]: "DISPATCHER", [orgB]: "DISPATCHER" }, { list });
    const first = context.controller.start();
    await vi.waitFor(() => expect(list).toHaveBeenCalledTimes(1));
    context.switchTo(orgB);
    await context.controller.start();
    release({ items: [parseIncident(incidentResponse())], next_cursor: "stale" });
    await first;
    expect(context.controller.getSnapshot()).toMatchObject({ incidents: [], nextCursor: null });
  });
});

describe("incident flows", () => {
  it("shows the incident the API returned", async () => {
    const { controller, api } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    await controller.open(draft);
    expect(vi.mocked(api.open).mock.calls[0][1]).toMatchObject({ occurred_at: "2026-09-28T16:00:00.000Z" });
    expect(controller.getSnapshot().incidents.map((incident) => incident.id)).toEqual([incidentId]);
    await controller.resolve(incidentId, "RESOLVED", "Entregado en segundo intento");
    expect(controller.getSnapshot().incidents).toHaveLength(1);
    expect(controller.getSnapshot().incidents[0].status).toBe("RESOLVED");
  });

  it("replaces a returned incident where it is listed; only a newly opened one goes first (AI-07 newest first)", async () => {
    const newer = syntheticUuid(0x311);
    const opened = syntheticUuid(0x312);
    const resolve = vi
      .fn<IncidentsApi["resolve"]>()
      .mockResolvedValueOnce(parseIncident(incidentResponse({ status: "RESOLVED" })))
      .mockResolvedValueOnce(parseIncident(incidentResponse({ id: syntheticUuid(0x399), status: "REJECTED" })));
    const open = vi
      .fn<IncidentsApi["open"]>()
      .mockResolvedValueOnce(parseIncident(incidentResponse({ id: opened })))
      .mockResolvedValueOnce(parseIncident(incidentResponse({ id: newer, severity: "HIGH" })));
    const list = vi.fn<IncidentsApi["list"]>().mockResolvedValue({
      items: [parseIncident(incidentResponse({ id: newer })), parseIncident(incidentResponse())],
      next_cursor: null,
    });
    const { controller } = setup({ [orgA]: "DISPATCHER" }, { list, resolve, open });
    await controller.start();
    const ids = () => controller.getSnapshot().incidents.map((incident) => incident.id);
    // Resolving the older incident keeps it second.
    await controller.resolve(incidentId, "RESOLVED", "Entregado en segundo intento");
    expect(ids()).toEqual([newer, incidentId]);
    expect(controller.getSnapshot().incidents[1].status).toBe("RESOLVED");
    // A resolution of an incident that is not listed adds nothing.
    await controller.resolve(syntheticUuid(0x399), "REJECTED", "Duplicada");
    expect(ids()).toEqual([newer, incidentId]);
    // A newly opened incident goes first; an answer for one already listed stays where it is.
    await controller.open(draft);
    expect(ids()).toEqual([opened, newer, incidentId]);
    await controller.open({ ...draft, severity: "HIGH" });
    expect(ids()).toEqual([opened, newer, incidentId]);
    expect(controller.getSnapshot().incidents[1].severity).toBe("HIGH");
  });

  it("adds a newly opened incident only when it matches the filters in effect (Z14)", async () => {
    const listed = syntheticUuid(0x321);
    const otherOrder = syntheticUuid(0x111);
    const openedIds = [0x322, 0x323, 0x324, 0x325].map(syntheticUuid);
    const open = vi.fn<IncidentsApi["open"]>();
    for (const id of openedIds) open.mockResolvedValueOnce(parseIncident(incidentResponse({ id })));
    const list = vi.fn<IncidentsApi["list"]>().mockResolvedValue({
      items: [parseIncident(incidentResponse({ id: listed }))],
      next_cursor: null,
    });
    const { controller } = setup({ [orgA]: "DISPATCHER" }, { list, open });
    await controller.start();
    const ids = () => controller.getSnapshot().incidents.map((incident) => incident.id);

    // An OPEN incident does not match a RESOLVED filter, nor an order filter of another order: the list stays.
    await controller.refresh("RESOLVED", null);
    await controller.open(draft);
    expect(ids()).toEqual([listed]);
    expect(controller.getSnapshot().message).toBe("Incidencia abierta y registrada.");
    await controller.refresh(null, otherOrder);
    await controller.open({ ...draft, description: "Segunda apertura." });
    expect(ids()).toEqual([listed]);
    expect(controller.getSnapshot().message).toBe("Incidencia abierta y registrada.");

    // Matching both filters, or with none in effect, it goes first.
    await controller.refresh("OPEN", orderId);
    await controller.open({ ...draft, description: "Tercera apertura." });
    expect(ids()).toEqual([openedIds[2], listed]);
    await controller.refresh(null, null);
    await controller.open({ ...draft, description: "Cuarta apertura." });
    expect(ids()).toEqual([openedIds[3], listed]);
    expect(open).toHaveBeenCalledTimes(4);
  });

  it("does not call the API for an invalid draft", async () => {
    const { controller, api } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    await controller.open({ ...draft, evidence: "" });
    expect(api.open).not.toHaveBeenCalled();
    expect(controller.getSnapshot().errors.length).toBeGreaterThan(0);
  });

  it("reuses the Idempotency-Key on a retry after a network failure", async () => {
    const open = vi
      .fn<IncidentsApi["open"]>()
      .mockRejectedValueOnce(new TenantApiError("unavailable"))
      .mockResolvedValueOnce(parseIncident(incidentResponse()));
    const { controller } = setup({ [orgA]: "DISPATCHER" }, { open });
    await controller.start();
    await controller.open(draft);
    await controller.open(draft);
    expect(open.mock.calls[0][2]).toBe(open.mock.calls[1][2]);
    expect(open.mock.calls[0][1]).toEqual(open.mock.calls[1][1]);
  });

  it("fails closed on a response for another order", async () => {
    const { controller } = setup(
      { [orgA]: "DISPATCHER" },
      { open: vi.fn(async () => parseIncident(incidentResponse({ order_id: syntheticUuid(0x999) }))) },
    );
    await controller.start();
    await controller.open(draft);
    expect(controller.getSnapshot().incidents).toEqual([]);
    expect(controller.getSnapshot().message).toContain("servidor");
  });

  it("explains an expired attempt", async () => {
    const { controller } = setup(
      { [orgA]: "DISPATCHER" },
      { open: vi.fn(async () => { throw new TenantApiError("conflict", "OFFLINE_OPERATION_EXPIRED"); }) },
    );
    await controller.start();
    await controller.open(draft);
    expect(controller.getSnapshot().message).toContain("ventana permitida");
  });

  it("offers the MFA step-up for PLATFORM_ADMIN without MFA", async () => {
    const { controller } = setup(
      { [orgA]: "PLATFORM_ADMIN" },
      { resolve: vi.fn(async () => { throw new TenantApiError("forbidden", "MFA_REQUIRED", true); }) },
    );
    await controller.start();
    await controller.resolve(incidentId, "REJECTED", "Duplicada");
    expect(controller.getSnapshot().stepUpHref).toContain("mfa=required");
  });
});

describe("incident tenant switch", () => {
  it("clears incidents and pending keys", async () => {
    const context = setup(
      { [orgA]: "DISPATCHER", [orgB]: "DISPATCHER" },
      { resolve: vi.fn(async () => { throw new TenantApiError("network"); }) },
    );
    await context.controller.start();
    await context.controller.open(draft);
    await context.controller.resolve(incidentId, "RESOLVED", "ok");
    expect(context.pending.size).toBe(1);
    context.switchTo(orgB);
    await context.controller.start();
    expect(context.pending.size).toBe(0);
    expect(context.controller.getSnapshot().incidents).toEqual([]);
  });

  it("clears the list filters, so the other organization is listed unfiltered", async () => {
    const context = setup({ [orgA]: "DISPATCHER", [orgB]: "DISPATCHER" });
    await context.controller.start();
    await context.controller.refresh("INVESTIGATING", orderId);
    context.switchTo(orgB);
    await context.controller.start();
    expect(context.controller.getSnapshot()).toMatchObject({ statusFilter: null, orderFilter: null });
    expect(vi.mocked(context.api.list).mock.calls.at(-1)?.slice(0, 2)).toEqual([{}, null]);
  });
});
