import { describe, expect, it, vi } from "vitest";
import type { IncidentsApi } from "../api/incidents-api";
import { TenantApiError } from "../api/tenant-request";
import { parseIncident, type OpenIncidentDraft } from "../contracts/incident";
import {
  bearerSession,
  incidentId,
  incidentResponse,
  orderId,
  orgA,
  orgB,
  proofId,
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
});
