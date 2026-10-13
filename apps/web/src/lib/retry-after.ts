/** The longest wait a Retry-After may impose, so a broken header cannot park a queue or a button for hours. */
export const maximumRetryAfterSeconds = 3_600;

/**
 * SEC-004: the wait a 429 asks for, from its Retry-After header. The API always
 * sends whole seconds, at least 1 (AI-05 TooManyRequests). Anything else (an
 * HTTP date, a fraction, zero, a sign or a missing header) is null, and a value
 * above {@link maximumRetryAfterSeconds} is capped at it.
 */
export function readRetryAfterSeconds(headers: Headers): number | null {
  const value = headers.get("Retry-After")?.trim();
  if (value === undefined || !/^[0-9]{1,9}$/.test(value)) return null;
  const seconds = Number(value);
  if (seconds < 1) return null;
  return Math.min(seconds, maximumRetryAfterSeconds);
}
