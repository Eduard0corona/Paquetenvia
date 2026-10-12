import { instantToMazatlanWallTime } from "../../lib/mazatlan-time";

/**
 * UI-PHASE3-DRIVERS-2026-10-11: the period of "Calcular liquidación". A settlement covers whole operating days of
 * the pilot zone (America/Mazatlan, AI-05 createSettlement), so every preset is computed from today's calendar date
 * in Mazatlán, never from the device's zone, and both presets end before today: they are always closed periods.
 *
 * - Semana pasada: Monday to Sunday of the week before the current one.
 * - Quincena pasada: the half month before the current one, the 1st to the 15th or the 16th to the month's end.
 * - Fechas a mano: the dates the person types.
 */
export const settlementPeriodPresets = ["last_week", "last_fortnight", "manual"] as const;
export type SettlementPeriodPreset = (typeof settlementPeriodPresets)[number];

export const settlementPeriodPresetLabels: Readonly<Record<SettlementPeriodPreset, string>> = {
  last_week: "Semana pasada",
  last_fortnight: "Quincena pasada",
  manual: "Fechas a mano",
};

export interface SettlementPeriod {
  /** First operating day, inclusive (`YYYY-MM-DD`). */
  readonly period_from: string;
  /** Last operating day, inclusive (`YYYY-MM-DD`). */
  readonly period_to: string;
}

const dayMilliseconds = 86_400_000;

/** Today's calendar date in America/Mazatlan (`YYYY-MM-DD`), whatever the device's zone. */
export function mazatlanToday(now: Date): string {
  const wall = instantToMazatlanWallTime(now);
  if (wall === "") throw new RangeError("A valid instant is required.");
  return wall.slice(0, 10);
}

/** Monday to Sunday of the week before the one `now` falls in, in Mazatlán time. */
export function lastWeekPeriod(now: Date): SettlementPeriod {
  const today = civilDay(mazatlanToday(now));
  // getUTCDay of a civil date: 0 is Sunday; days since Monday is 0 for Monday and 6 for Sunday.
  const sinceMonday = (new Date(today).getUTCDay() + 6) % 7;
  const thisMonday = today - sinceMonday * dayMilliseconds;
  return {
    period_from: isoDay(thisMonday - 7 * dayMilliseconds),
    period_to: isoDay(thisMonday - dayMilliseconds),
  };
}

/**
 * The half month before the one `now` falls in, in Mazatlán time: from the 1st to the 15th of the month, the
 * previous month's 16th to its last day; from the 16th on, the 1st to the 15th of the same month.
 */
export function lastFortnightPeriod(now: Date): SettlementPeriod {
  const [year, month, day] = mazatlanToday(now).split("-").map(Number) as [number, number, number];
  if (day >= 16) {
    return { period_from: isoDay(Date.UTC(year, month - 1, 1)), period_to: isoDay(Date.UTC(year, month - 1, 15)) };
  }
  // Day 0 of the current month is the last day of the previous one (28, 29, 30 or 31).
  const lastOfPrevious = Date.UTC(year, month - 1, 0);
  const previous = new Date(lastOfPrevious);
  return {
    period_from: isoDay(Date.UTC(previous.getUTCFullYear(), previous.getUTCMonth(), 16)),
    period_to: isoDay(lastOfPrevious),
  };
}

/** The period a preset names at `now`; null for "Fechas a mano", whose dates are typed. */
export function presetPeriod(preset: SettlementPeriodPreset, now: Date): SettlementPeriod | null {
  switch (preset) {
    case "last_week":
      return lastWeekPeriod(now);
    case "last_fortnight":
      return lastFortnightPeriod(now);
    case "manual":
      return null;
  }
}

const monthAbbreviations = ["ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic"] as const;

/**
 * A period in readable es-MX dates, as the approved design writes it: "1–7 sep 2026", "28 sep – 4 oct 2026",
 * "29 dic 2025 – 4 ene 2026" or "1 sep 2026" for a single day. Calendar dates are written as they are: no zone
 * offset can move them to another day. An unreadable date is returned as written.
 */
export function formatPeriodRange(periodFrom: string, periodTo: string): string {
  const from = calendarParts(periodFrom);
  const to = calendarParts(periodTo);
  if (from === null || to === null) return `${periodFrom} a ${periodTo}`;
  const month = (value: CalendarParts) => monthAbbreviations[value.month - 1];
  if (periodFrom === periodTo) return `${from.day} ${month(from)} ${from.year}`;
  if (from.year === to.year && from.month === to.month) return `${from.day}–${to.day} ${month(to)} ${to.year}`;
  if (from.year === to.year) return `${from.day} ${month(from)} – ${to.day} ${month(to)} ${to.year}`;
  return `${from.day} ${month(from)} ${from.year} – ${to.day} ${month(to)} ${to.year}`;
}

interface CalendarParts {
  readonly year: number;
  readonly month: number;
  readonly day: number;
}

function calendarParts(value: string): CalendarParts | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (match === null) return null;
  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])];
  const date = new Date(Date.UTC(year, month - 1, day));
  if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day) return null;
  return { year, month, day };
}

/** A civil `YYYY-MM-DD` date as UTC midnight milliseconds; only used for calendar arithmetic. */
function civilDay(value: string): number {
  const [year, month, day] = value.split("-").map(Number) as [number, number, number];
  return Date.UTC(year, month - 1, day);
}

function isoDay(milliseconds: number): string {
  return new Date(milliseconds).toISOString().slice(0, 10);
}
