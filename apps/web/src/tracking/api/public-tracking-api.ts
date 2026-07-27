import {
  parsePublicTrackingProjection,
  PublicTrackingContractError,
  type PublicTrackingProjection,
} from "../contracts/public-tracking";

export type PublicTrackingApiErrorCategory =
  | "not-found"
  | "rate-limited"
  | "unavailable"
  | "network"
  | "invalid-response";

export class PublicTrackingApiError extends Error {
  public constructor(
    public readonly category: PublicTrackingApiErrorCategory,
  ) {
    super("Public tracking request failed.");
    this.name = "PublicTrackingApiError";
  }
}

export interface PublicTrackingApi {
  getProjection(
    token: string,
    signal?: AbortSignal,
  ): Promise<PublicTrackingProjection>;
}

export interface PublicTrackingApiOptions {
  readonly baseUrl: string;
  readonly timeoutMilliseconds?: number;
}

export function createPublicTrackingApi(
  options: PublicTrackingApiOptions,
): PublicTrackingApi {
  const baseUrl = validateApiBaseUrl(options.baseUrl);
  const timeoutMilliseconds = options.timeoutMilliseconds ?? 8_000;
  if (
    !Number.isInteger(timeoutMilliseconds) ||
    timeoutMilliseconds < 1_000 ||
    timeoutMilliseconds > 30_000
  ) {
    throw new Error("Public tracking timeout is outside the supported range.");
  }

  return {
    async getProjection(token, signal) {
      const timeout = AbortSignal.timeout(timeoutMilliseconds);
      const combinedSignal =
        signal === undefined ? timeout : AbortSignal.any([signal, timeout]);
      let response: Response;
      try {
        response = await fetch(
          new URL(`/api/v1/tracking/${token}`, baseUrl),
          {
            method: "GET",
            headers: { Accept: "application/json" },
            cache: "no-store",
            credentials: "omit",
            referrerPolicy: "no-referrer",
            signal: combinedSignal,
          },
        );
      } catch {
        throw new PublicTrackingApiError("network");
      }

      if (response.status === 404) {
        throw new PublicTrackingApiError("not-found");
      }
      if (response.status === 429) {
        throw new PublicTrackingApiError("rate-limited");
      }
      if (response.status >= 500) {
        throw new PublicTrackingApiError("unavailable");
      }
      if (!response.ok || !isJson(response.headers.get("content-type"))) {
        throw new PublicTrackingApiError("invalid-response");
      }

      try {
        return parsePublicTrackingProjection(await response.json());
      } catch (error: unknown) {
        if (error instanceof PublicTrackingContractError) {
          throw new PublicTrackingApiError("invalid-response");
        }
        throw new PublicTrackingApiError("invalid-response");
      }
    },
  };
}

export function validateApiBaseUrl(value: string): string {
  const url = new URL(value);
  if (url.protocol !== "https:" && url.protocol !== "http:") {
    throw new Error("Public API URL must use HTTP or HTTPS.");
  }
  if (process.env.NODE_ENV === "production" && url.protocol !== "https:") {
    throw new Error("Public API URL must use HTTPS in production.");
  }
  url.pathname = "/";
  url.search = "";
  url.hash = "";
  return url.toString();
}

function isJson(contentType: string | null): boolean {
  return (
    contentType !== null &&
    /^application\/json(?:\s*;\s*charset=[\w-]+)?$/i.test(contentType)
  );
}
