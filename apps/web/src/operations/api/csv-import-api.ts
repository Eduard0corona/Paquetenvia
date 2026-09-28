import {
  checkCsvFile,
  csvUploadFilename,
  parseCsvImportCommit,
  parseCsvImportPreview,
  type CsvImportCommit,
  type CsvImportPreview,
} from "../contracts/csv-import";
import type { OperationsSession } from "../session/operations-session";
import { createTenantRequester, readJson, TenantApiError } from "./tenant-request";

export type CsvCommitResult =
  | { readonly kind: "committed"; readonly commit: CsvImportCommit }
  /** 422: at least one row failed prevalidation; nothing was created. */
  | { readonly kind: "rejected"; readonly preview: CsvImportPreview };

/** AI-05 previewOrderCsv and commitOrderCsv (CSV-001), multipart/form-data only. */
export interface CsvImportApi {
  preview(file: Blob, signal?: AbortSignal): Promise<CsvImportPreview>;
  commit(file: Blob, contentDigest: string, idempotencyKey: string, signal?: AbortSignal): Promise<CsvCommitResult>;
}

function upload(file: Blob, contentDigest?: string): FormData {
  if (checkCsvFile(file.size) !== null) throw new TenantApiError("invalid");
  const form = new FormData();
  // A neutral filename: the local file name never leaves the browser.
  form.append("file", new Blob([file], { type: "text/csv" }), csvUploadFilename);
  if (contentDigest !== undefined) form.append("content_digest", contentDigest);
  return form;
}

export function createCsvImportApi(baseUrl: string, session: OperationsSession): CsvImportApi {
  const send = createTenantRequester(baseUrl, session, 60_000);
  return {
    async preview(file, signal) {
      const response = await send({
        method: "POST",
        path: "/api/v1/orders/csv/preview",
        form: upload(file),
        signal,
      });
      return (await readJson(response, parseCsvImportPreview)) as CsvImportPreview;
    },
    async commit(file, contentDigest, idempotencyKey, signal) {
      const response = await send({
        method: "POST",
        path: "/api/v1/orders/csv/commit",
        form: upload(file, contentDigest),
        idempotencyKey,
        passThroughStatuses: [422],
        signal,
      });
      if (response.status === 422) {
        const preview = (await readJson(response, parseCsvImportPreview)) as CsvImportPreview;
        return { kind: "rejected", preview };
      }
      const commit = (await readJson(response, parseCsvImportCommit)) as CsvImportCommit;
      // The batch must be the one previewed; anything else fails closed.
      if (commit.content_digest !== contentDigest) throw new TenantApiError("invalid");
      return { kind: "committed", commit };
    },
  };
}
