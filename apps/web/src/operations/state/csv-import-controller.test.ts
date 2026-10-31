import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RateLimitWaits } from "../../lib/rate-limit-wait";
import type { CsvImportApi } from "../api/csv-import-api";
import { TenantApiError } from "../api/tenant-request";
import {
  csvRestrictedGoodsRequiredMessage,
  parseCsvImportCommit,
  parseCsvImportPreview,
} from "../contracts/csv-import";
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
import { CsvImportController, csvImportRateLimit } from "./csv-import-controller";
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
    await controller.commit(true);
    const [sent, digest] = vi.mocked(api.commit).mock.calls[0];
    expect(await (sent as Blob).text()).toBe("quote_id,payer_type,terms_version\n");
    expect(digest).toBe(syntheticDigest());
    expect(controller.getSnapshot().commit?.created_rows).toBe(1);
  });

  it("does not commit until the dispatcher confirms no prohibited goods (ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02)", async () => {
    const { controller, api } = await previewed({ [orgA]: "DISPATCHER" });
    await controller.commit(false);
    expect(api.commit).not.toHaveBeenCalled();
    expect(controller.getSnapshot().errors).toEqual([csvRestrictedGoodsRequiredMessage]);
    await controller.commit(true);
    expect(api.commit).toHaveBeenCalledTimes(1);
  });

  it("does not commit a preview with invalid rows", async () => {
    const { controller, api } = await previewed(
      { [orgA]: "DISPATCHER" },
      { preview: vi.fn(async () => parseCsvImportPreview(invalidPreviewResponse())) },
    );
    await controller.commit(true);
    expect(api.commit).not.toHaveBeenCalled();
    expect(controller.getSnapshot().message).toContain("Ninguna orden se creó");
  });

  it("replaces the preview with the 422 report and creates nothing", async () => {
    const { controller } = await previewed(
      { [orgA]: "DISPATCHER" },
      { commit: vi.fn(async () => ({ kind: "rejected" as const, preview: parseCsvImportPreview(invalidPreviewResponse()) })) },
    );
    await controller.commit(true);
    expect(controller.getSnapshot().preview?.invalid_rows).toBe(1);
    expect(controller.getSnapshot().commit).toBeNull();
  });

  it("reuses the batch Idempotency-Key when retrying after a network failure", async () => {
    const commit = vi
      .fn<CsvImportApi["commit"]>()
      .mockRejectedValueOnce(new TenantApiError("network"))
      .mockResolvedValueOnce({ kind: "committed", commit: parseCsvImportCommit(commitResponse()) });
    const { controller } = await previewed({ [orgA]: "DISPATCHER" }, { commit });
    await controller.commit(true);
    await controller.commit(true);
    expect(commit.mock.calls[0][2]).toBe(commit.mock.calls[1][2]);
    expect(controller.getSnapshot().commit).not.toBeNull();
  });

  it("uses a new key after a definitive answer", async () => {
    const commit = vi
      .fn<CsvImportApi["commit"]>()
      .mockRejectedValueOnce(new TenantApiError("conflict", "CONFLICT"))
      .mockResolvedValueOnce({ kind: "committed", commit: parseCsvImportCommit(commitResponse()) });
    const { controller } = await previewed({ [orgA]: "DISPATCHER" }, { commit });
    await controller.commit(true);
    await controller.commit(true);
    expect(commit.mock.calls[0][2]).not.toBe(commit.mock.calls[1][2]);
  });

  it("passes the file's WhatsApp consent box to the commit, unticked by default (NTF-RECIPIENT-NOTICES-2026-10-20)", async () => {
    const { controller, api } = await previewed({ [orgA]: "DISPATCHER" });
    await controller.commit(true, true);
    expect(vi.mocked(api.commit).mock.calls[0][4]).toBe(true);
    const unticked = await previewed({ [orgA]: "DISPATCHER" });
    await unticked.controller.commit(true);
    expect(vi.mocked(unticked.api.commit).mock.calls[0][4]).toBe(false);
  });

  it("keeps the batch key for a retry with the same box and uses another key for the other answer", async () => {
    const commit = vi
      .fn<CsvImportApi["commit"]>()
      .mockRejectedValueOnce(new TenantApiError("network"))
      .mockRejectedValueOnce(new TenantApiError("network"))
      .mockResolvedValueOnce({ kind: "committed", commit: parseCsvImportCommit(commitResponse()) });
    const { controller } = await previewed({ [orgA]: "DISPATCHER" }, { commit });
    await controller.commit(true, true);
    await controller.commit(true, true);
    await controller.commit(true, false);
    const keys = commit.mock.calls.map((call) => call[2]);
    expect(keys[0]).toBe(keys[1]);
    // The server binds the box to the batch fingerprint (N3 choice 3): the other answer is another batch.
    expect(keys[2]).not.toBe(keys[0]);
    expect(commit.mock.calls.map((call) => call[4])).toEqual([true, true, false]);
  });

  it("explains a reused batch key", async () => {
    const { controller } = await previewed(
      { [orgA]: "DISPATCHER" },
      { commit: vi.fn(async () => { throw new TenantApiError("conflict", "IDEMPOTENCY_CONFLICT"); }) },
    );
    await controller.commit(true);
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
    await controller.commit(true);
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
    await context.controller.commit(true);
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

describe("CSV import under the SEC-004 limit (owner answers Q1, Q6)", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-13T12:00:00Z"));
  });
  afterEach(() => vi.useRealTimers());

  function limitedImport() {
    const waits = new RateLimitWaits();
    const state = { limited: 0 };
    const limit = () => {
      waits.record("csv_import", state.limited);
      return new TenantApiError("rate_limited", null, false, state.limited);
    };
    const api: CsvImportApi = {
      preview: vi.fn(async () => {
        if (state.limited > 0) throw limit();
        return parseCsvImportPreview(previewResponse());
      }),
      commit: vi.fn(async () => {
        if (state.limited > 0) throw limit();
        return { kind: "committed" as const, commit: parseCsvImportCommit(commitResponse()) };
      }),
    };
    const controller = new CsvImportController({
      readSession: () => bearerSession(orgA),
      createApi: () => api,
      loadRole: async () => "DISPATCHER",
      waits,
    });
    return { controller, api, waits, state };
  }

  it("shares one wait between preview and commit (one budget)", () => {
    expect(csvImportRateLimit).toEqual(["csv_import"]);
  });

  it("keeps the file, the report and the batch key on a 429, sends nothing during the wait and never by itself", async () => {
    const env = limitedImport();
    await env.controller.start();
    await env.controller.selectFile(file());
    await env.controller.preview();
    const preview = env.controller.getSnapshot().preview;
    expect(preview).not.toBeNull();

    env.state.limited = 2;
    await env.controller.commit(true);
    expect(env.api.commit).toHaveBeenCalledTimes(1);
    const firstKey = vi.mocked(env.api.commit).mock.calls[0][2];
    expect(env.controller.getSnapshot()).toMatchObject({ busy: false, message: null, stepUpHref: null, preview, commit: null });
    expect(env.controller.getSnapshot().fileBytes).not.toBeNull();

    // Both buttons are held: neither a second commit nor a new preview is sent during the wait.
    env.state.limited = 0;
    await env.controller.commit(true);
    await env.controller.preview();
    expect(env.api.commit).toHaveBeenCalledTimes(1);
    expect(env.api.preview).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(env.api.commit).toHaveBeenCalledTimes(1);

    // Pressed again after the wait: the same Idempotency-Key, since the handler never ran.
    await env.controller.commit(true);
    expect(env.api.commit).toHaveBeenCalledTimes(2);
    expect(vi.mocked(env.api.commit).mock.calls[1][2]).toBe(firstKey);
    expect(env.controller.getSnapshot().commit).not.toBeNull();
  });

  it("re-enables the preview when the wait ends", async () => {
    const env = limitedImport();
    await env.controller.start();
    await env.controller.selectFile(file());
    env.state.limited = 1;
    await env.controller.preview();
    expect(env.controller.getSnapshot()).toMatchObject({ message: null, preview: null });
    env.state.limited = 0;
    await env.controller.preview();
    expect(env.api.preview).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1_000);
    await env.controller.preview();
    expect(env.api.preview).toHaveBeenCalledTimes(2);
    expect(env.controller.getSnapshot().preview).not.toBeNull();
  });
});
