/**
 * Conversions for the dashboard's `datetime-local` creation-date filters. The input value is
 * wall-clock time in the browser's zone; the filter keeps the UTC instant the API receives.
 * Both directions are needed so the inputs can be controlled by the filter state (and so
 * "Limpiar filtros" empties them).
 */
export function dateTimeLocalToUtc(value: string): string | undefined {
  if (value === "") return undefined;
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? undefined : parsed.toISOString();
}

export function utcToDateTimeLocal(value: string | undefined): string {
  if (value === undefined) return "";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "";
  const pad = (part: number) => String(part).padStart(2, "0");
  return (
    `${String(date.getFullYear()).padStart(4, "0")}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}` +
    `T${pad(date.getHours())}:${pad(date.getMinutes())}`
  );
}
