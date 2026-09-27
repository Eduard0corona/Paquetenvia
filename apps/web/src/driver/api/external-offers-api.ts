import { ExternalOfferContractError, parseExternalOfferPage, type ExternalOfferPage } from "../contracts/external-offer";
import type { DriverSession } from "../session/driver-session";
import { resolveRequestAuthorization, type RequestAuthorization } from "../../auth/request-credentials";

export type ExternalOffersApiFailure = "unauthorized" | "forbidden" | "conflict" | "recoverable" | "invalid-contract" | "cancelled";

export class ExternalOffersApiError extends Error {
  public constructor(public readonly category: ExternalOffersApiFailure) {
    super("No fue posible procesar las ofertas externas.");
    this.name = "ExternalOffersApiError";
  }
}

export interface ExternalOffersApi {
  list(cursor?: string, signal?: AbortSignal): Promise<ExternalOfferPage>;
  accept(offerId: string, idempotencyKey: string, signal?: AbortSignal): Promise<void>;
}

export function createExternalOffersApi(baseUrl: string, session: DriverSession, fetcher: typeof fetch = fetch): ExternalOffersApi {
  async function request(path: string, init: RequestInit, signal?: AbortSignal): Promise<Response> {
    let authorization: RequestAuthorization;
    try {
      authorization = await resolveRequestAuthorization(session, init.method ?? "GET");
    } catch {
      throw new ExternalOffersApiError("unauthorized");
    }
    try {
      const response = await fetcher(new URL(path, baseUrl), {
        ...init,
        signal,
        cache: "no-store",
        credentials: authorization.credentials,
        headers: {
          Accept: "application/json",
          ...authorization.headers,
          "X-Organization-Id": session.organizationId,
          ...init.headers,
        },
      });
      if (response.status === 401) throw new ExternalOffersApiError("unauthorized");
      if (response.status === 403) throw new ExternalOffersApiError("forbidden");
      if (response.status === 409) throw new ExternalOffersApiError("conflict");
      if (response.status >= 500) throw new ExternalOffersApiError("recoverable");
      if (!response.ok) throw new ExternalOffersApiError("invalid-contract");
      return response;
    } catch (error) {
      if (error instanceof ExternalOffersApiError) throw error;
      if (signal?.aborted) throw new ExternalOffersApiError("cancelled");
      throw new ExternalOffersApiError("recoverable");
    }
  }

  return {
    async list(cursor, signal) {
      const search = cursor ? `?cursor=${encodeURIComponent(cursor)}` : "";
      const response = await request(`/api/v1/driver/me/external-offers${search}`, { method: "GET" }, signal);
      try {
        return parseExternalOfferPage(await response.json());
      } catch (error) {
        if (error instanceof ExternalOfferContractError) throw new ExternalOffersApiError("invalid-contract");
        throw new ExternalOffersApiError("invalid-contract");
      }
    },
    async accept(offerId, idempotencyKey, signal) {
      await request(`/api/v1/external-offers/${encodeURIComponent(offerId)}/accept`, {
        method: "POST",
        headers: { "Idempotency-Key": idempotencyKey },
      }, signal);
    },
  };
}
