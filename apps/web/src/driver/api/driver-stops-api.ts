import {
  DriverStopsContractError,
  parseDriverStopList,
  type DriverStopList,
} from "../contracts/driver-stop";
import type { DriverSession } from "../session/driver-session";
import {
  resolveRequestAuthorization,
  type RequestAuthorization,
} from "../../auth/request-credentials";

export interface DriverStopsListOptions {
  /**
   * false asks for the stops without their protected details
   * (`?details=false`, UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11): the server
   * reads no personal value, so confirming queued actions never depends on
   * the key service and never transfers addresses or names.
   */
  readonly details?: boolean;
}

export interface DriverStopsApi {
  listStops(
    signal?: AbortSignal,
    options?: DriverStopsListOptions,
  ): Promise<DriverStopList>;
}

export type DriverStopsApiFailure =
  | "unauthorized"
  | "forbidden"
  | "recoverable"
  | "invalid-contract"
  | "cancelled";

export class DriverStopsApiError extends Error {
  public constructor(public readonly category: DriverStopsApiFailure) {
    super("No fue posible obtener las paradas.");
    this.name = "DriverStopsApiError";
  }
}

export interface DriverStopsApiOptions {
  readonly baseUrl: string;
  readonly session: DriverSession;
  readonly timeoutMilliseconds?: number;
  readonly fetch?: typeof fetch;
}

export function createDriverStopsApi(
  options: DriverStopsApiOptions,
): DriverStopsApi {
  const fetchImplementation = options.fetch ?? fetch;
  const timeoutMilliseconds = options.timeoutMilliseconds ?? 10_000;
  const endpoint = new URL("/api/v1/driver/me/stops", options.baseUrl);
  const withoutDetails = new URL(endpoint);
  withoutDetails.searchParams.set("details", "false");

  return {
    async listStops(
      signal?: AbortSignal,
      listOptions?: DriverStopsListOptions,
    ): Promise<DriverStopList> {
      const controller = new AbortController();
      const onAbort = () => controller.abort();
      signal?.addEventListener("abort", onAbort, { once: true });
      const timeout = globalThis.setTimeout(
        () => controller.abort(),
        timeoutMilliseconds,
      );

      try {
        if (signal?.aborted) {
          throw new DriverStopsApiError("cancelled");
        }
        let authorization: RequestAuthorization;
        try {
          authorization = await resolveRequestAuthorization(options.session, "GET");
        } catch {
          throw new DriverStopsApiError("unauthorized");
        }

        const response = await fetchImplementation(
          listOptions?.details === false ? withoutDetails : endpoint,
          {
            method: "GET",
            cache: "no-store",
            credentials: authorization.credentials,
            signal: controller.signal,
            headers: {
              Accept: "application/json",
              ...authorization.headers,
              "X-Organization-Id": options.session.organizationId,
            },
          },
        );

        if (response.status === 401) {
          throw new DriverStopsApiError("unauthorized");
        }
        if (response.status === 403) {
          throw new DriverStopsApiError("forbidden");
        }
        if (response.status >= 500) {
          throw new DriverStopsApiError("recoverable");
        }
        if (response.status !== 200) {
          throw new DriverStopsApiError("invalid-contract");
        }
        const contentType = response.headers.get("content-type") ?? "";
        if (!contentType.toLowerCase().startsWith("application/json")) {
          throw new DriverStopsApiError("invalid-contract");
        }

        let payload: unknown;
        try {
          payload = await response.json();
          return parseDriverStopList(payload);
        } catch (error) {
          if (error instanceof DriverStopsContractError) {
            throw new DriverStopsApiError("invalid-contract");
          }
          if (error instanceof DriverStopsApiError) {
            throw error;
          }
          throw new DriverStopsApiError("invalid-contract");
        }
      } catch (error) {
        if (error instanceof DriverStopsApiError) {
          throw error;
        }
        if (controller.signal.aborted) {
          throw new DriverStopsApiError(signal?.aborted ? "cancelled" : "recoverable");
        }
        throw new DriverStopsApiError("recoverable");
      } finally {
        globalThis.clearTimeout(timeout);
        signal?.removeEventListener("abort", onAbort);
      }
    },
  };
}
