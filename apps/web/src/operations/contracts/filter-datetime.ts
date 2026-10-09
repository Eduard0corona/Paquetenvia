import { instantToMazatlanWallTime, mazatlanWallTimeToInstant } from "../../lib/mazatlan-time";

/**
 * Conversions for the dashboard's `datetime-local` creation-date filters. The input value is
 * wall-clock time in America/Mazatlan (AI-02 meta.timezone), never the browser's zone; the
 * filter keeps the UTC instant the API receives. Both directions are needed so the inputs can
 * be controlled by the filter state (and so "Limpiar filtros" empties them).
 */
export function dateTimeLocalToUtc(value: string): string | undefined {
  if (value === "") return undefined;
  return mazatlanWallTimeToInstant(value)?.toISOString();
}

export function utcToDateTimeLocal(value: string | undefined): string {
  return instantToMazatlanWallTime(value);
}
