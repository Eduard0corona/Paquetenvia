import {
  OrganizationRolesContractError,
  parseMyOrganizationRoles,
} from "../contracts/organization-roles";
import type { DriverSession } from "../session/driver-session";
import {
  resolveRequestAuthorization,
  type RequestAuthorization,
} from "../../auth/request-credentials";

/** AI-05 getMyOrganizationRoles (ORG-MY-ROLES-2026-10-12). */
export const MyOrganizationRolesPath = "/api/v1/me/organization-roles";

/**
 * - `unauthorized` / `forbidden`: 401 or 403, the session is no longer a member there;
 * - `not-found`: 404, an API older than this app, without the operation;
 * - `recoverable`: no answer, a timeout or a 5xx;
 * - `invalid-contract`: any other answer, or one that breaks the contract;
 * - `cancelled`: the caller aborted.
 */
export type DriverOrganizationRolesApiFailure =
  | "unauthorized"
  | "forbidden"
  | "not-found"
  | "recoverable"
  | "invalid-contract"
  | "cancelled";

export class DriverOrganizationRolesApiError extends Error {
  public constructor(public readonly category: DriverOrganizationRolesApiFailure) {
    super("No fue posible consultar los roles de la organización.");
    this.name = "DriverOrganizationRolesApiError";
  }
}

export interface DriverOrganizationRolesApi {
  /** The person's own roles in the session's organization, read strictly. */
  getMyRoles(signal?: AbortSignal): Promise<readonly string[]>;
}

export interface DriverOrganizationRolesApiOptions {
  readonly baseUrl: string;
  readonly session: DriverSession;
  readonly timeoutMilliseconds?: number;
  readonly fetch?: typeof fetch;
}

/** A GET with the session's credentials and organization, no-store, bounded by a timeout; never queued offline. */
export function createDriverOrganizationRolesApi(
  options: DriverOrganizationRolesApiOptions,
): DriverOrganizationRolesApi {
  const fetchImplementation = options.fetch ?? fetch;
  const timeoutMilliseconds = options.timeoutMilliseconds ?? 10_000;
  const endpoint = new URL(MyOrganizationRolesPath, options.baseUrl);

  return {
    async getMyRoles(signal?: AbortSignal): Promise<readonly string[]> {
      const controller = new AbortController();
      const onAbort = () => controller.abort();
      signal?.addEventListener("abort", onAbort, { once: true });
      const timeout = globalThis.setTimeout(() => controller.abort(), timeoutMilliseconds);

      try {
        if (signal?.aborted) {
          throw new DriverOrganizationRolesApiError("cancelled");
        }
        let authorization: RequestAuthorization;
        try {
          authorization = await resolveRequestAuthorization(options.session, "GET");
        } catch {
          throw new DriverOrganizationRolesApiError("unauthorized");
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

        if (response.status === 401) throw new DriverOrganizationRolesApiError("unauthorized");
        if (response.status === 403) throw new DriverOrganizationRolesApiError("forbidden");
        if (response.status === 404) throw new DriverOrganizationRolesApiError("not-found");
        if (response.status >= 500) throw new DriverOrganizationRolesApiError("recoverable");
        if (response.status !== 200) throw new DriverOrganizationRolesApiError("invalid-contract");
        const contentType = response.headers.get("content-type") ?? "";
        if (!contentType.toLowerCase().startsWith("application/json")) {
          throw new DriverOrganizationRolesApiError("invalid-contract");
        }
        try {
          return parseMyOrganizationRoles(await response.json());
        } catch (error) {
          if (error instanceof OrganizationRolesContractError || error instanceof SyntaxError) {
            throw new DriverOrganizationRolesApiError("invalid-contract");
          }
          throw error;
        }
      } catch (error) {
        if (error instanceof DriverOrganizationRolesApiError) throw error;
        if (controller.signal.aborted) {
          throw new DriverOrganizationRolesApiError(signal?.aborted ? "cancelled" : "recoverable");
        }
        throw new DriverOrganizationRolesApiError("recoverable");
      } finally {
        globalThis.clearTimeout(timeout);
        signal?.removeEventListener("abort", onAbort);
      }
    },
  };
}
