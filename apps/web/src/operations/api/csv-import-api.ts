import { recipientWhatsAppConsentVersion } from "../contracts/create-order";
import {
  checkCsvFile,
  csvRecipientConsentField,
  csvRecipientConsentVersionField,
  csvRestrictedGoodsField,
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
  /**
   * @param recipientWhatsAppConsent NTF-RECIPIENT-NOTICES-2026-10-20: the dispatcher ticked the one WhatsApp consent box
   * of the file; only then are the two consent fields sent.
   */
  commit(
    file: Blob,
    contentDigest: string,
    idempotencyKey: string,
    signal?: AbortSignal,
    recipientWhatsAppConsent?: boolean,
  ): Promise<CsvCommitResult>;
}

function upload(file: Blob, contentDigest?: string, recipientWhatsAppConsent = false): FormData {
  if (checkCsvFile(file.size) !== null) throw new TenantApiError("invalid");
  const form = new FormData();
  // A neutral filename: the local file name never leaves the browser.
  form.append("file", new Blob([file], { type: "text/csv" }), csvUploadFilename);
  if (contentDigest !== undefined) {
    form.append("content_digest", contentDigest);
    // ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02: a commit always carries the dispatcher's confirmation; the
    // controller only commits after the dispatcher ticked it, and the server rejects a commit without it.
    form.append(csvRestrictedGoodsField, "true");
    // NTF-RECIPIENT-NOTICES-2026-10-20 (AI-05 part NTF-004): the consent box of the file, only when ticked.
    if (recipientWhatsAppConsent) {
      form.append(csvRecipientConsentField, "true");
      form.append(csvRecipientConsentVersionField, recipientWhatsAppConsentVersion);
    }
  }
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
        rateLimit: "csv_import",
      });
      return (await readJson(response, parseCsvImportPreview)) as CsvImportPreview;
    },
    async commit(file, contentDigest, idempotencyKey, signal, recipientWhatsAppConsent = false) {
      const response = await send({
        method: "POST",
        path: "/api/v1/orders/csv/commit",
        form: upload(file, contentDigest, recipientWhatsAppConsent),
        idempotencyKey,
        passThroughStatuses: [422],
        signal,
        // SEC-004 (owner answer Q1): the commit spends the preview's budget, and one commit runs at a time.
        rateLimit: "csv_import",
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
