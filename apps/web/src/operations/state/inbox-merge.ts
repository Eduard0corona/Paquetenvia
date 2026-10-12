import type { OperationsDashboardOrder } from "../contracts/operations-dashboard";

/**
 * UI-PHASE3-INBOX-2026-10-10: one inbox queue can be several dashboard queries (one per
 * status). Each query pages by its own cursor in the server order `updated_at DESC, order_id
 * DESC`; this module merges what was loaded into one list in that same order.
 *
 * A merged row is shown only when no query that still has pages could hold a newer row: every
 * row at or above the oldest row loaded by each unfinished query is complete, anything below it
 * waits for "Cargar más". So the list never shows a row out of order and never skips one.
 */
export interface InboxSourcePage {
  /** Everything this query returned so far, in server order. */
  readonly items: readonly OperationsDashboardOrder[];
  /** null once the query has no more pages. */
  readonly nextCursor: string | null;
}

export interface InboxMergeResult {
  readonly rows: readonly OperationsDashboardOrder[];
  /** Some query still has pages ("Cargar más"). */
  readonly hasMore: boolean;
}

interface SortKey {
  readonly milliseconds: number;
  /** Fraction of the second beyond milliseconds (the server keeps microseconds). */
  readonly remainder: number;
}

function sortKey(instant: string): SortKey {
  const fraction = /\.(\d+)/.exec(instant)?.[1] ?? "";
  return {
    milliseconds: Date.parse(instant),
    remainder: Number((fraction.slice(3) + "0000").slice(0, 4)),
  };
}

/** Server order: newer `updated_at` first, then the greater order id; negative when `left` goes first. */
export function compareInboxRows(left: OperationsDashboardOrder, right: OperationsDashboardOrder): number {
  const a = sortKey(left.updated_at);
  const b = sortKey(right.updated_at);
  if (a.milliseconds !== b.milliseconds) return b.milliseconds - a.milliseconds;
  if (a.remainder !== b.remainder) return b.remainder - a.remainder;
  if (left.order_id === right.order_id) return 0;
  return left.order_id < right.order_id ? 1 : -1;
}

export function mergeInboxSources(sources: readonly InboxSourcePage[]): InboxMergeResult {
  // The same order can come back from two queries when its status changed between them;
  // the higher aggregate version is the newer snapshot.
  const latest = new Map<string, OperationsDashboardOrder>();
  for (const source of sources) {
    for (const item of source.items) {
      const prior = latest.get(item.order_id);
      if (prior === undefined || item.aggregate_version > prior.aggregate_version) latest.set(item.order_id, item);
    }
  }

  let boundary: OperationsDashboardOrder | null = null;
  for (const source of sources) {
    const last = source.items.at(-1);
    if (source.nextCursor === null || last === undefined) continue;
    if (boundary === null || compareInboxRows(last, boundary) < 0) boundary = last;
  }

  const ordered = [...latest.values()].sort(compareInboxRows);
  const rows = boundary === null ? ordered : ordered.filter((row) => compareInboxRows(row, boundary) <= 0);
  return { rows, hasMore: sources.some((source) => source.nextCursor !== null) };
}

/** Aggregate versions of every loaded order, for the realtime resynchronization snapshot. */
export function inboxAggregateVersions(sources: readonly InboxSourcePage[]): Readonly<Record<string, number>> {
  const versions: Record<string, number> = {};
  for (const source of sources) {
    for (const item of source.items) {
      const prior = versions[item.order_id];
      if (prior === undefined || item.aggregate_version > prior) versions[item.order_id] = item.aggregate_version;
    }
  }
  return versions;
}
