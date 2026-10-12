/**
 * UI-PHASE3-INBOX-2026-10-10: the dashboard ("Tablero") shows its orders as a list or as positions, and
 * the view lives in its URL (`/ops/dashboard?view=positions`). "Mapa de posiciones" in the inbox opens
 * the positions view directly, and a later navigation to the dashboard with another query shows the
 * view that query names.
 */
export type DashboardView = "list" | "positions";

export const dashboardPath = "/ops/dashboard";

const viewParameter = "view";

/** The view a dashboard URL names: `view=positions` is the positions view, anything else the list. */
export function parseDashboardView(search: { get(name: string): string | null }): DashboardView {
  return search.get(viewParameter) === "positions" ? "positions" : "list";
}

/**
 * The dashboard URL that shows `view`, keeping any other query parameter of `current`. The list is the
 * canonical URL without `view`.
 */
export function dashboardViewHref(view: DashboardView, current: { toString(): string } = ""): string {
  const search = new URLSearchParams(current.toString());
  if (view === "positions") search.set(viewParameter, "positions");
  else search.delete(viewParameter);
  const query = search.toString();
  return query === "" ? dashboardPath : `${dashboardPath}?${query}`;
}
