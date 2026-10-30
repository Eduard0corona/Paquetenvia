import { readRetryAfterSeconds } from "../../lib/retry-after";
import { rateLimitFallbackSeconds } from "../../lib/rate-limit-wait";
import { validateApiBaseUrl } from "../../tracking/api/public-tracking-api";

/** What one press of the button ended in (AI-05 unsubscribeRecipientNotices). */
export type RecipientUnsubscribeOutcome =
  | { readonly kind: "recorded" }
  | { readonly kind: "invalid" }
  | { readonly kind: "wait"; readonly seconds: number }
  | { readonly kind: "failed" };

export interface RecipientUnsubscribeApi {
  unsubscribe(token: string, signal?: AbortSignal): Promise<RecipientUnsubscribeOutcome>;
}

/**
 * NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (AI-05 unsubscribeRecipientNotices, O11 choice 7): one POST with the JSON
 * object {} and the token of the link, without credentials, referrer or cache. 204 is recorded (also when it already
 * was); 404 is the invalid link; 429 is a wait of its Retry-After (60 seconds when it says nothing usable); anything
 * else, a network failure or a timeout is the failure message. Nothing is stored and the token is never logged.
 */
export function createRecipientUnsubscribeApi(options: {
  readonly baseUrl: string;
  readonly timeoutMilliseconds?: number;
}): RecipientUnsubscribeApi {
  const baseUrl = validateApiBaseUrl(options.baseUrl);
  const timeoutMilliseconds = options.timeoutMilliseconds ?? 8_000;
  if (!Number.isInteger(timeoutMilliseconds) || timeoutMilliseconds < 1_000 || timeoutMilliseconds > 30_000) {
    throw new Error("Unsubscribe timeout is outside the supported range.");
  }

  return {
    async unsubscribe(token, signal) {
      if (!/^[A-Za-z0-9_-]{88}$/.test(token)) return { kind: "invalid" };
      const timeout = AbortSignal.timeout(timeoutMilliseconds);
      let response: Response;
      try {
        response = await fetch(new URL(`/api/v1/recipient-notices/unsubscribe/${token}`, baseUrl), {
          method: "POST",
          headers: { Accept: "application/problem+json", "Content-Type": "application/json" },
          body: "{}",
          cache: "no-store",
          credentials: "omit",
          redirect: "error",
          referrerPolicy: "no-referrer",
          signal: signal === undefined ? timeout : AbortSignal.any([signal, timeout]),
        });
      } catch {
        return { kind: "failed" };
      }

      if (response.status === 204) return { kind: "recorded" };
      if (response.status === 404) return { kind: "invalid" };
      if (response.status === 429) {
        return { kind: "wait", seconds: readRetryAfterSeconds(response.headers) ?? rateLimitFallbackSeconds };
      }
      return { kind: "failed" };
    },
  };
}
