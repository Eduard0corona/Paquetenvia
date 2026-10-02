import { afterEach, describe, expect, it, vi } from "vitest";
import { maximumCsvBytes } from "../contracts/csv-import";
import {
  bearerSession,
  commitResponse,
  invalidPreviewResponse,
  jsonResponse,
  orgA,
  previewResponse,
  problem,
  syntheticDigest,
  syntheticKey,
} from "../contracts/ui-001-screens.fixtures";
import { createCsvImportApi } from "./csv-import-api";
import { TenantApiError } from "./tenant-request";

const csv = () => new Blob(["quote_id,payer_type\n"], { type: "text/csv" });
const key = syntheticKey(1);

function lastCall(fetchMock: ReturnType<typeof vi.fn>): [URL, RequestInit] {
  return fetchMock.mock.calls.at(-1) as [URL, RequestInit];
}

afterEach(() => vi.unstubAllGlobals());

describe("CSV import api", () => {
  it("previews with a multipart file part under a neutral name and no idempotency key", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(200, previewResponse()));
    vi.stubGlobal("fetch", fetchMock);
    const preview = await createCsvImportApi("https://api.synthetic.test", bearerSession()).preview(csv());
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe("/api/v1/orders/csv/preview");
    const headers = init.headers as Record<string, string>;
    expect(headers["X-Organization-Id"]).toBe(orgA);
    // The browser sets the multipart boundary; the client never forces a JSON type.
    expect(headers["Content-Type"]).toBeUndefined();
    expect(headers["Idempotency-Key"]).toBeUndefined();
    expect(init).toMatchObject({ method: "POST", cache: "no-store", referrerPolicy: "no-referrer" });
    const form = init.body as FormData;
    expect(form).toBeInstanceOf(FormData);
    const file = form.get("file") as File;
    expect(file.name).toBe("orders.csv");
    expect(await file.text()).toBe("quote_id,payer_type\n");
    expect(form.get("content_digest")).toBeNull();
    expect(form.get("restricted_goods_acknowledged")).toBeNull();
    expect(preview.valid_rows).toBe(1);
  });

  it("commits the same bytes with the echoed digest and the batch Idempotency-Key", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(200, commitResponse()));
    vi.stubGlobal("fetch", fetchMock);
    const result = await createCsvImportApi("https://api.synthetic.test", bearerSession()).commit(
      csv(),
      syntheticDigest(),
      key,
    );
    const [url, init] = lastCall(fetchMock);
    expect(url.pathname).toBe("/api/v1/orders/csv/commit");
    expect((init.headers as Record<string, string>)["Idempotency-Key"]).toBe(key);
    const form = init.body as FormData;
    expect(form.get("content_digest")).toBe(syntheticDigest());
    // ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02: exactly one confirmation field on commit.
    expect(form.getAll("restricted_goods_acknowledged")).toEqual(["true"]);
    expect(result).toMatchObject({ kind: "committed" });
  });

  it("returns the 422 per-row report instead of throwing", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(422, invalidPreviewResponse())));
    const result = await createCsvImportApi("https://api.synthetic.test", bearerSession()).commit(
      csv(),
      syntheticDigest(),
      key,
    );
    expect(result.kind).toBe("rejected");
  });

  it("fails closed when the commit names another batch", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(200, commitResponse({ content_digest: syntheticDigest("e") }))));
    const error = await createCsvImportApi("https://api.synthetic.test", bearerSession())
      .commit(csv(), syntheticDigest(), key)
      .catch((caught: unknown) => caught);
    expect(error).toMatchObject({ category: "invalid" });
  });

  it("reports a reused batch key by its code", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(409, "IDEMPOTENCY_CONFLICT")));
    const error = await createCsvImportApi("https://api.synthetic.test", bearerSession())
      .commit(csv(), syntheticDigest(), key)
      .catch((caught: unknown) => caught);
    expect(error).toBeInstanceOf(TenantApiError);
    expect(error).toMatchObject({ category: "conflict", code: "IDEMPOTENCY_CONFLICT" });
  });

  it("refuses an oversized or empty file without calling the API", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const api = createCsvImportApi("https://api.synthetic.test", bearerSession());
    await expect(api.preview(new Blob([new Uint8Array(maximumCsvBytes + 1)]))).rejects.toMatchObject({ category: "invalid" });
    await expect(api.preview(new Blob([]))).rejects.toMatchObject({ category: "invalid" });
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
