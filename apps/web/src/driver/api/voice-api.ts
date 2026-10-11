import {
  driverPhoneConsentVersion,
  isRecipientCallConflictCode,
  parseDriverPhoneStatus,
  parseRecipientCallAvailability,
  parseRecipientCallRequest,
  VoiceContractError,
  type DriverPhoneStatus,
  type RecipientCallAvailability,
  type RecipientCallConflictCode,
  type RecipientCallRequest,
} from "../contracts/voice";
import type { DriverSession } from "../session/driver-session";
import { resolveRequestAuthorization, type RequestAuthorization } from "../../auth/request-credentials";

/**
 * - `network`: no answer reached the PWA (the same tap may be retried with the same Idempotency-Key);
 * - `unavailable`: the API answered 503 (the bridge is off or failed; nothing to retry with the same key).
 */
export type DriverVoiceApiFailure =
  | "unauthorized"
  | "forbidden"
  | "not-found"
  | "conflict"
  | "rate-limited"
  | "unavailable"
  | "network"
  | "invalid-contract"
  | "cancelled";

export class DriverVoiceApiError extends Error {
  public constructor(
    public readonly category: DriverVoiceApiFailure,
    public readonly code: RecipientCallConflictCode | null = null,
    public readonly retryAfterSeconds: number | null = null,
  ) {
    super("No fue posible procesar la llamada.");
    this.name = "DriverVoiceApiError";
  }
}

export interface DriverVoiceApi {
  getPhone(signal?: AbortSignal): Promise<DriverPhoneStatus>;
  /** `phoneDigits` are the ten normalized digits; the API answers only whether a number is stored. */
  registerPhone(phoneDigits: string, signal?: AbortSignal): Promise<DriverPhoneStatus>;
  removePhone(signal?: AbortSignal): Promise<DriverPhoneStatus>;
  getRecipientCallAvailability(orderId: string, signal?: AbortSignal): Promise<RecipientCallAvailability>;
  /** No body and no number: the server knows both phones. */
  requestRecipientCall(orderId: string, idempotencyKey: string, signal?: AbortSignal): Promise<RecipientCallRequest>;
}

export function createDriverVoiceApi(
  baseUrl: string,
  session: DriverSession,
  fetcher: typeof fetch = fetch,
): DriverVoiceApi {
  async function send(path: string, init: RequestInit, signal?: AbortSignal): Promise<unknown> {
    let authorization: RequestAuthorization;
    try {
      authorization = await resolveRequestAuthorization(session, init.method ?? "GET");
    } catch {
      throw new DriverVoiceApiError("unauthorized");
    }

    let response: Response;
    try {
      response = await fetcher(new URL(path, baseUrl), {
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
    } catch {
      throw new DriverVoiceApiError(signal?.aborted ? "cancelled" : "network");
    }

    if (response.status === 401) throw new DriverVoiceApiError("unauthorized");
    if (response.status === 403) throw new DriverVoiceApiError("forbidden");
    if (response.status === 404) throw new DriverVoiceApiError("not-found");
    if (response.status === 409) throw new DriverVoiceApiError("conflict", await conflictCode(response));
    if (response.status === 429) throw new DriverVoiceApiError("rate-limited", null, retryAfter(response));
    if (response.status >= 500) throw new DriverVoiceApiError("unavailable");
    if (!response.ok) throw new DriverVoiceApiError("invalid-contract");
    try {
      return await response.json();
    } catch {
      throw new DriverVoiceApiError(signal?.aborted ? "cancelled" : "invalid-contract");
    }
  }

  function parsed<T>(parse: (value: unknown) => T, value: unknown): T {
    try {
      return parse(value);
    } catch (error) {
      if (error instanceof VoiceContractError) throw new DriverVoiceApiError("invalid-contract");
      throw error;
    }
  }

  const phonePath = "/api/v1/driver/me/phone";
  const callPath = (orderId: string) => `/api/v1/driver/me/stops/${encodeURIComponent(orderId)}/recipient-call`;

  return {
    async getPhone(signal) {
      return parsed(parseDriverPhoneStatus, await send(phonePath, { method: "GET" }, signal));
    },
    async registerPhone(phoneDigits, signal) {
      const body = JSON.stringify({
        phone: phoneDigits,
        consent_accepted: true,
        consent_version: driverPhoneConsentVersion,
      });
      return parsed(
        parseDriverPhoneStatus,
        await send(phonePath, { method: "PUT", body, headers: { "Content-Type": "application/json" } }, signal),
      );
    },
    async removePhone(signal) {
      return parsed(parseDriverPhoneStatus, await send(phonePath, { method: "DELETE" }, signal));
    },
    async getRecipientCallAvailability(orderId, signal) {
      return parsed(parseRecipientCallAvailability, await send(callPath(orderId), { method: "GET" }, signal));
    },
    async requestRecipientCall(orderId, idempotencyKey, signal) {
      return parsed(
        parseRecipientCallRequest,
        await send(callPath(orderId), { method: "POST", headers: { "Idempotency-Key": idempotencyKey } }, signal),
      );
    },
  };
}

async function conflictCode(response: Response): Promise<RecipientCallConflictCode | null> {
  try {
    const body: unknown = await response.json();
    const code = typeof body === "object" && body !== null ? (body as Record<string, unknown>).code : null;
    return isRecipientCallConflictCode(code) ? code : null;
  } catch {
    return null;
  }
}

function retryAfter(response: Response): number | null {
  const value = response.headers.get("Retry-After");
  if (value === null || !/^[0-9]{1,6}$/.test(value)) return null;
  return Number(value);
}
