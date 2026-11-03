import { ContractViolationError } from "../../operations/contracts/strict-json";
import { parseMyDriverDocuments, type MyDriverDocuments } from "../contracts/driver-documents";
import type { DriverSession } from "../session/driver-session";
import { resolveRequestAuthorization, type RequestAuthorization } from "../../auth/request-credentials";

/** AI-05 listMyDriverDocuments (DRV-DOC-001-IN-PERSON-2026-11-02). */
export const MyDriverDocumentsPath = "/api/v1/driver/me/documents";

/**
 * - `unauthorized`: 401, or the session has no usable credential, so nothing was sent;
 * - `forbidden`: 403, the person is not a driver with a profile in the selected organization;
 * - `unavailable`: a 5xx (the store is unavailable);
 * - `network`: no answer reached the PWA, or the session is only remembered (nothing is sent before it is confirmed);
 * - `invalid-contract`: any other answer, or one that breaks the contract;
 * - `cancelled`: the caller aborted.
 */
export type DriverDocumentsApiFailure =
  | "unauthorized"
  | "forbidden"
  | "unavailable"
  | "network"
  | "invalid-contract"
  | "cancelled";

export class DriverDocumentsApiError extends Error {
  public constructor(public readonly category: DriverDocumentsApiFailure) {
    super("No fue posible consultar tus documentos.");
    this.name = "DriverDocumentsApiError";
  }
}

export interface DriverDocumentsApi {
  /** The driver's own documents in the session's organization; a GET only, never queued offline. */
  getMyDocuments(signal?: AbortSignal): Promise<MyDriverDocuments>;
}

export function createDriverDocumentsApi(
  baseUrl: string,
  session: DriverSession,
  fetcher: typeof fetch = fetch,
): DriverDocumentsApi {
  return {
    async getMyDocuments(signal) {
      // DRV-SESSION-EXPIRY-OFFLINE-2026-10-19: never with a session the server has not confirmed.
      if (session.remembered === true) throw new DriverDocumentsApiError("network");
      let authorization: RequestAuthorization;
      try {
        authorization = await resolveRequestAuthorization(session, "GET");
      } catch {
        throw new DriverDocumentsApiError("unauthorized");
      }

      let response: Response;
      try {
        response = await fetcher(new URL(MyDriverDocumentsPath, baseUrl), {
          method: "GET",
          signal,
          cache: "no-store",
          credentials: authorization.credentials,
          referrerPolicy: "no-referrer",
          headers: {
            Accept: "application/json",
            ...authorization.headers,
            "X-Organization-Id": session.organizationId,
          },
        });
      } catch {
        throw new DriverDocumentsApiError(signal?.aborted ? "cancelled" : "network");
      }

      if (response.status === 401) throw new DriverDocumentsApiError("unauthorized");
      if (response.status === 403) throw new DriverDocumentsApiError("forbidden");
      if (response.status >= 500) throw new DriverDocumentsApiError("unavailable");
      if (response.status !== 200 || !(response.headers.get("content-type") ?? "").toLowerCase().includes("application/json"))
        throw new DriverDocumentsApiError("invalid-contract");
      let body: unknown;
      try {
        body = await response.json();
      } catch {
        throw new DriverDocumentsApiError(signal?.aborted ? "cancelled" : "invalid-contract");
      }
      try {
        return parseMyDriverDocuments(body);
      } catch (error) {
        if (error instanceof ContractViolationError) throw new DriverDocumentsApiError("invalid-contract");
        throw error;
      }
    },
  };
}
