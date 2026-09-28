import { describe, expect, it, vi } from "vitest";
import type { CsvImportApi } from "../api/csv-import-api";
import { TenantApiError } from "../api/tenant-request";
import { parseCsvImportCommit, parseCsvImportPreview } from "../contracts/csv-import";
import {
  bearerSession,
  commitResponse,
  invalidPreviewResponse,
  orgA,
  orgB,
  previewResponse,
  syntheticDigest,
} from "../contracts/ui-001-screens.fixtures";
import type { OperationsSession } from "../session/operations-session";
import { CsvImportController } from "./csv-import-controller";
import { PendingSubmissions } from "./pending-submissions";

const file = () => new Blob(["quote_id,payer_type,terms_version\n"]);

function setup(roles: Record<string, string>, overrides: Partial<CsvImportApi> = {}) {
  let current: OperationsSession | null = bearerSession(orgA);
  const api: CsvImportApi = {
    preview: vi.fn(async () => parseCsvImportPreview(previewResponse())),
    commit: vi.fn(async () => ({ kind: "committed" as const, commit: parseCsvImportCommit(commitResponse()) })),
    ...overrides,
  };
  const pending = new PendingSubmissions();
  const controller = new CsvImportController(
    {
      readSession: () => current,
      createApi: () => api,
      loadRole: async (session) => roles[session.organizationId] ?? null,
    },
    pending,
  );
  return {
    controller,
    api,
    pending,
    switchTo(organizationId: string | null) {
      current = organizationId === null ? null : bearerSession(organizationId);
    },
  };
}

async function previewed(roles: Record<string, string>, overrides: Partial<CsvImportApi> = {}) {
  const context = setup(roles, overrides);
  await context.controller.start();
  await context.controller.selectFile(file());
  await context.controller.preview();
  return context;
}

describe("CSV import capability gating (D5-CAPABILITY-MATRIX)", () => {
  it.each(["DISPATCHER", "PLATFORM_ADMIN"])("admits %s", async (role) => {
    const { controller } = setup({ [orgA]: role });
    await controller.start();
    expect(controller.getSnapshot()).toMatchObject({ phase: "ready", canCommit: true });
  });

  it.each(["VIEWER", "FINANCE", "DRIVER", "CUSTOMER_SUPPORT"])("never uploads for %s", async (role) => {
    const { controller, api } = setup({ [orgA]: role });
    await controller.start();
    await controller.selectFile(file());
    await controller.preview();
    expect(controller.getSnapshot().phase).toBe("access_unavailable");
    expect(api.preview).not.toHaveBeenCalled();
  });
});

describe("CSV import flow", () => {
  it("previews, then commits the exact previewed digest", async () => {
    const { controller, api } = await previewed({ [orgA]: "DISPATCHER" });
    expect(controller.getSnapshot().preview?.valid_rows).toBe(1);
    await controller.commit();
    const [sent, digest] = vi.mocked(api.commit).mock.calls[0];
    expect(await (sent as Blob).text()).toBe("quote_id,payer_type,terms_version\n");
    expect(digest).toBe(syntheticDigest());
    expect(controller.getSnapshot().commit?.created_rows).toBe(1);
  });

  it("does not commit a preview with invalid rows", async () => {
    const { controller, api } = await previewed(
      { [orgA]: "DISPATCHER" },
      { preview: vi.fn(async () => parseCsvImportPreview(invalidPreviewResponse())) },
    );
    await controller.commit();
    expect(api.commit).not.toHaveBeenCalled();
    expect(controller.getSnapshot().message).toContain("Ninguna orden se creó");
  });

  it("replaces the preview with the 422 report and creates nothing", async () => {
    const { controller } = await previewed(
      { [orgA]: "DISPATCHER" },
      { commit: vi.fn(async () => ({ kind: "rejected" as const, preview: parseCsvImportPreview(invalidPreviewResponse()) })) },
    );
    await controller.commit();
    expect(controller.getSnapshot().preview?.invalid_rows).toBe(1);
    expect(controller.getSnapshot().commit).toBeNull();
  });

  it("reuses the batch Idempotency-Key when retrying after a network failure", async () => {
    const commit = vi
      .fn<CsvImportApi["commit"]>()
      .mockRejectedValueOnce(new TenantApiError("network"))
      .mockResolvedValueOnce({ kind: "committed", commit: parseCsvImportCommit(commitResponse()) });
    const { controller } = await previewed({ [orgA]: "DISPATCHER" }, { commit });
    await controller.commit();
    await controller.commit();
    expect(commit.mock.calls[0][2]).toBe(commit.mock.calls[1][2]);
    expect(controller.getSnapshot().commit).not.toBeNull();
  });

  it("uses a new key after a definitive answer", async () => {
    const commit = vi
      .fn<CsvImportApi["commit"]>()
      .mockRejectedValueOnce(new TenantApiError("conflict", "CONFLICT"))
      .mockResolvedValueOnce({ kind: "committed", commit: parseCsvImportCommit(commitResponse()) });
    const { controller } = await previewed({ [orgA]: "DISPATCHER" }, { commit });
    await controller.commit();
    await controller.commit();
    expect(commit.mock.calls[0][2]).not.toBe(commit.mock.calls[1][2]);
  });

  it("explains a reused batch key", async () => {
    const { controller } = await previewed(
      { [orgA]: "DISPATCHER" },
      { commit: vi.fn(async () => { throw new TenantApiError("conflict", "IDEMPOTENCY_CONFLICT"); }) },
    );
    await controller.commit();
    expect(controller.getSnapshot().message).toContain("otro contenido");
  });

  it("refuses an empty file before calling the API", async () => {
    const { controller, api } = setup({ [orgA]: "DISPATCHER" });
    await controller.start();
    await controller.selectFile(new Blob([]));
    await controller.preview();
    expect(api.preview).not.toHaveBeenCalled();
    expect(controller.getSnapshot().errors).toHaveLength(1);
  });

  it("selecting another file discards the preview and the batch key", async () => {
    const { controller, pending } = await previewed(
      { [orgA]: "DISPATCHER" },
      { commit: vi.fn(async () => { throw new TenantApiError("network"); }) },
    );
    await controller.commit();
    expect(pending.size).toBe(1);
    await controller.selectFile(file());
    expect(pending.size).toBe(0);
    expect(controller.getSnapshot().preview).toBeNull();
  });
});

describe("CSV import tenant switch", () => {
  it("clears the file, report and pending key", async () => {
    const context = await previewed(
      { [orgA]: "DISPATCHER", [orgB]: "DISPATCHER" },
      { commit: vi.fn(async () => { throw new TenantApiError("network"); }) },
    );
    await context.controller.commit();
    context.switchTo(orgB);
    await context.controller.start();
    expect(context.pending.size).toBe(0);
    expect(context.controller.getSnapshot()).toMatchObject({ preview: null, commit: null, fileBytes: null });
    await context.controller.preview();
    expect(context.api.preview).toHaveBeenCalledOnce();
  });

  it("ignores a preview that resolves after the switch", async () => {
    let resolve!: (value: ReturnType<typeof parseCsvImportPreview>) => void;
    const context = setup(
      { [orgA]: "DISPATCHER", [orgB]: "DISPATCHER" },
      { preview: vi.fn(() => new Promise<ReturnType<typeof parseCsvImportPreview>>((done) => { resolve = done; })) },
    );
    await context.controller.start();
    await context.controller.selectFile(file());
    const inFlight = context.controller.preview();
    context.switchTo(orgB);
    await context.controller.start();
    resolve(parseCsvImportPreview(previewResponse()));
    await inFlight;
    expect(context.controller.getSnapshot().preview).toBeNull();
  });
});
