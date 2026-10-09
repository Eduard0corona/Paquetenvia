/**
 * AI-02 meta.timezone: the pilot works on America/Mazatlan wall-clock time. A value typed
 * in a `datetime-local` input is read in that zone, never the browser's, and an instant
 * written back into such an input is shown in that zone, so a screen behaves the same on
 * any device clock.
 */
export const pilotTimeZone = "America/Mazatlan";

const wallTimePattern = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?$/;

const zoneFormatter = new Intl.DateTimeFormat("en-CA", {
  timeZone: pilotTimeZone,
  year: "numeric",
  month: "2-digit",
  day: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
  hourCycle: "h23",
});

/** Wall-clock time of `instant` in America/Mazatlan, encoded as if it were UTC milliseconds. */
function zoneWallClockMs(instant: number): number {
  const parts: Record<string, number> = {};
  for (const part of zoneFormatter.formatToParts(new Date(instant))) {
    if (part.type !== "literal") parts[part.type] = Number(part.value);
  }
  return Date.UTC(
    parts.year!,
    parts.month! - 1,
    parts.day!,
    parts.hour! % 24,
    parts.minute!,
    parts.second!,
  );
}

/**
 * Converts a `datetime-local` value (`YYYY-MM-DDTHH:mm[:ss]`) read as America/Mazatlan
 * wall-clock time to its instant. Returns `null` for malformed text, impossible dates
 * and wall-clock times that do not exist in the zone.
 */
export function mazatlanWallTimeToInstant(text: string): Date | null {
  const match = wallTimePattern.exec(text.trim());
  if (match === null) return null;
  const [year, month, day, hour, minute] = match.slice(1, 6).map(Number) as [number, number, number, number, number];
  const second = match[6] === undefined ? 0 : Number(match[6]);
  const wall = Date.UTC(year, month - 1, day, hour, minute, second);
  const check = new Date(wall);
  if (
    check.getUTCFullYear() !== year ||
    check.getUTCMonth() !== month - 1 ||
    check.getUTCDate() !== day ||
    check.getUTCHours() !== hour ||
    check.getUTCMinutes() !== minute ||
    check.getUTCSeconds() !== second
  )
    return null;
  // Two passes settle the zone offset around a transition; the final check rejects a
  // wall-clock time skipped by the zone.
  let instant = wall - (zoneWallClockMs(wall) - wall);
  instant = wall - (zoneWallClockMs(instant) - instant);
  return zoneWallClockMs(instant) === wall ? new Date(instant) : null;
}

/**
 * The `datetime-local` value (`YYYY-MM-DDTHH:mm`) showing `instant` in America/Mazatlan;
 * an empty string for a missing or unreadable instant.
 */
export function instantToMazatlanWallTime(instant: Date | string | undefined): string {
  if (instant === undefined) return "";
  const milliseconds = instant instanceof Date ? instant.getTime() : Date.parse(instant);
  if (Number.isNaN(milliseconds)) return "";
  return new Date(zoneWallClockMs(milliseconds)).toISOString().slice(0, 16);
}
