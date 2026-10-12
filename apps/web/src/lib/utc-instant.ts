/**
 * A strict RFC 3339 instant in UTC, as the API serializes one: `YYYY-MM-DDTHH:mm:ss`, an optional fraction of 1 to 7
 * digits and `Z` or `+00:00` (what System.Text.Json writes for a UTC DateTimeOffset), nothing around it, and a real
 * calendar date and time of day. Anything else is not an instant.
 */
const utcInstantPattern = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d{1,7})?(?:Z|\+00:00)$/;

export function isStrictUtcInstant(value: unknown): value is string {
  if (typeof value !== "string") return false;
  const match = utcInstantPattern.exec(value);
  if (match === null) return false;
  const [year, month, day, hour, minute, second] = match.slice(1, 7).map(Number) as [
    number,
    number,
    number,
    number,
    number,
    number,
  ];
  if (hour > 23 || minute > 59 || second > 59) return false;
  const date = new Date(0);
  date.setUTCFullYear(year, month - 1, day);
  return date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day;
}

/**
 * UI-PHASE3-ORDERS-LIST-2026-10-12 (owner, "En dos pasos (Recommended)"): the optional `created_at` of the AI-05 Order.
 * The API adds it in the next change; until then it is absent. Absent reads as null; present, it must be a strict UTC
 * instant, or `fail` is called. Every web reader of the Order shape uses this one rule.
 */
export function optionalOrderCreatedAt(object: Readonly<Record<string, unknown>>, fail: () => never): string | null {
  if (!Object.hasOwn(object, "created_at")) return null;
  const value = object.created_at;
  if (!isStrictUtcInstant(value)) fail();
  return value;
}

/** The Order member every web reader accepts besides the ones AI-05 lists today. */
export const orderCreatedAtField = "created_at";
