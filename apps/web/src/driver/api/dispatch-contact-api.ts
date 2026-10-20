import {
  DispatchContactContractError,
  parseMyDispatchContact,
} from "../contracts/dispatch-contact";
import type { DriverSession } from "../session/driver-session";
import {
  resolveRequestAuthorization,
  type RequestAuthorization,
} from "../../auth/request-credentials";

/** AI-05 getMyDispatchContact (DRV-DISPATCH-CONTACT-2026-10-12). */
export const MyDispatchContactPath = "/api/v1/driver/me/dispatch-contact";

/**
 * - `session-ended`: 401, the session ended; the saved answer stays (DRV-SESSION-EXPIRY-OFFLINE-2026-10-19);
 * - `unauthorized`: the session has no usable credential, so nothing was sent;
 * - `forbidden`: 403, the session is no longer a driver there;
 * - `not-found`: 404, an API older than this app, without the operation;
 * - `recoverable`: no answer, a timeout or a 5xx;
 * - `invalid-contract`: any other answer, or one that breaks the contract;
 * - `cancelled`: the caller aborted.
 */
export type DriverDispatchContactApiFailure =
  | "session-ended"
  | "unauthorized"
  | "forbidden"
  | "not-found"
  | "recoverable"
  | "invalid-contract"
  | "cancelled";

export class DriverDispatchContactApiError extends Error {
  public constructor(public readonly category: DriverDispatchContactApiFailure) {
    super("No fue posible consultar el teléfono de despacho.");
    this.name = "DriverDispatchContactApiError";
  }
}

export interface DriverDispatchContactApi {
  /** The dispatch number of the session's organization, or null when it has none; read strictly. */
  getMyDispatchContact(signal?: AbortSignal): Promise<string | null>;
}

export interface DriverDispatchContactApiOptions {
  readonly baseUrl: string;
  readonly session: DriverSession;
  readonly timeoutMilliseconds?: number;
  readonly fetch?: typeof fetch;
}

/** A GET with the session's credentials and organization, no-store, bounded by a timeout; never queued offline. */
export function createDriverDispatchContactApi(
  options: DriverDispatchContactApiOptions,
): DriverDispatchContactApi {
  const fetchImplementation = options.fetch ?? fetch;
  const timeoutMilliseconds = options.timeoutMilliseconds ?? 10_000;
  const endpoint = new URL(MyDispatchContactPath, options.baseUrl);

  return {
    async getMyDispatchContact(signal?: AbortSignal): Promise<string | null> {
      const controller = new AbortController();
      const onAbort = () => controller.abort();
      signal?.addEventListener("abort", onAbort, { once: true });
      const timeout = globalThis.setTimeout(() => controller.abort(), timeoutMilliseconds);

      try {
        if (signal?.aborted) {
          throw new DriverDispatchContactApiError("cancelled");
        }
        // DRV-SESSION-EXPIRY-OFFLINE-2026-10-19: a remembered session is not
        // confirmed by the server, so it never reaches the network (the
        // cookie may belong to someone else); the caller falls back to what
        // the partition saved.
        if (options.session.remembered === true) {
          throw new DriverDispatchContactApiError("recoverable");
        }
        let authorization: RequestAuthorization;
        try {
          authorization = await resolveRequestAuthorization(options.session, "GET");
        } catch {
          throw new DriverDispatchContactApiError("unauthorized");
        }

        const response = await fetchImplementation(endpoint, {
          method: "GET",
          cache: "no-store",
          credentials: authorization.credentials,
          signal: controller.signal,
          headers: {
            Accept: "application/json",
            ...authorization.headers,
            "X-Organization-Id": options.session.organizationId,
          },
        });

        if (response.status === 401) throw new DriverDispatchContactApiError("session-ended");
        if (response.status === 403) throw new DriverDispatchContactApiError("forbidden");
        if (response.status === 404) throw new DriverDispatchContactApiError("not-found");
        if (response.status >= 500) throw new DriverDispatchContactApiError("recoverable");
        if (response.status !== 200) throw new DriverDispatchContactApiError("invalid-contract");
        const contentType = response.headers.get("content-type") ?? "";
        if (!contentType.toLowerCase().startsWith("application/json")) {
          throw new DriverDispatchContactApiError("invalid-contract");
        }
        try {
          return parseMyDispatchContact(await response.json());
        } catch (error) {
          if (error instanceof DispatchContactContractError || error instanceof SyntaxError) {
            throw new DriverDispatchContactApiError("invalid-contract");
          }
          throw error;
        }
      } catch (error) {
        if (error instanceof DriverDispatchContactApiError) throw error;
        if (controller.signal.aborted) {
          throw new DriverDispatchContactApiError(signal?.aborted ? "cancelled" : "recoverable");
        }
        throw new DriverDispatchContactApiError("recoverable");
      } finally {
        globalThis.clearTimeout(timeout);
        signal?.removeEventListener("abort", onAbort);
      }
    },
  };
}
