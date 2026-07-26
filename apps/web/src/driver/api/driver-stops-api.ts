import {
  DriverStopsContractError,
  parseDriverStops,
  type DriverStop,
} from "../contracts/driver-stop";
import type { DriverSession } from "../session/driver-session";

export interface DriverStopsApi {
  listStops(signal?: AbortSignal): Promise<readonly DriverStop[]>;
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

  return {
    async listStops(signal?: AbortSignal): Promise<readonly DriverStop[]> {
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
        const token = await options.session.getAccessToken();
        if (typeof token !== "string" || token.length < 1) {
          throw new DriverStopsApiError("unauthorized");
        }

        const response = await fetchImplementation(endpoint, {
          method: "GET",
          cache: "no-store",
          signal: controller.signal,
          headers: {
            Accept: "application/json",
            Authorization: `Bearer ${token}`,
            "X-Organization-Id": options.session.organizationId,
          },
        });

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
          return parseDriverStops(payload);
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
