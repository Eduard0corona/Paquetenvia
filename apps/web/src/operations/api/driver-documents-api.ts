import {
  driverDocumentSearch,
  parseDriverDocument,
  parseDriverDocumentPage,
  recordDriverDocumentBody,
  type DriverDocument,
  type DriverDocumentPage,
  type RecordDriverDocumentInput,
} from "../contracts/driver-document";
import type { OperationsSession } from "../session/operations-session";
import { assertUuid, createTenantRequester, readJson, TenantApiError } from "./tenant-request";

/**
 * DRV-DOC-001-IN-PERSON-2026-11-02: AI-05 listDriverDocuments, recordDriverDocument and revokeDriverDocument for
 * the "Documentos" column and dialogs of "Repartidores". JSON only; a document file never travels (Y3). Failures are
 * {@link TenantApiError}s carrying only a category and the stable problem code; no identifier or server text reaches
 * the message.
 */
export interface DriverDocumentsApi {
  list(cursor?: string | null, signal?: AbortSignal): Promise<DriverDocumentPage>;
  record(
    driverId: string,
    input: RecordDriverDocumentInput,
    idempotencyKey: string,
    signal?: AbortSignal,
  ): Promise<DriverDocument>;
  revoke(driverId: string, documentId: string, signal?: AbortSignal): Promise<DriverDocument>;
}

export function createDriverDocumentsApi(baseUrl: string, session: OperationsSession): DriverDocumentsApi {
  const send = createTenantRequester(baseUrl, session);
  return {
    async list(cursor, signal) {
      let search: URLSearchParams;
      try {
        search = driverDocumentSearch(cursor);
      } catch {
        throw new TenantApiError("invalid");
      }
      const response = await send({ method: "GET", path: "/api/v1/drivers/documents", search, signal });
      if (response.status !== 200) throw new TenantApiError("invalid");
      return (await readJson(response, parseDriverDocumentPage)) as DriverDocumentPage;
    },
    async record(driverId, input, idempotencyKey, signal) {
      assertUuid(driverId);
      let body;
      try {
        body = recordDriverDocumentBody(input);
      } catch {
        throw new TenantApiError("invalid");
      }
      const response = await send({
        method: "POST",
        path: `/api/v1/drivers/${encodeURIComponent(driverId)}/documents`,
        body,
        idempotencyKey,
        signal,
      });
      if (response.status !== 201) throw new TenantApiError("invalid");
      const document = (await readJson(response, parseDriverDocument)) as DriverDocument;
      if (document.document_type !== input.documentType || document.status !== input.decision)
        throw new TenantApiError("invalid");
      return document;
    },
    async revoke(driverId, documentId, signal) {
      assertUuid(driverId);
      assertUuid(documentId);
      const response = await send({
        method: "POST",
        path: `/api/v1/drivers/${encodeURIComponent(driverId)}/documents/${encodeURIComponent(documentId)}/revoke`,
        signal,
      });
      if (response.status !== 200) throw new TenantApiError("invalid");
      const document = (await readJson(response, parseDriverDocument)) as DriverDocument;
      if (document.id !== documentId || document.status !== "REVOKED") throw new TenantApiError("invalid");
      return document;
    },
  };
}
