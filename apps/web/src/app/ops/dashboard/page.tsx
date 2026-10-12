import { Suspense } from "react";
import { OperationsDashboardShell } from "@/operations/components/operations-dashboard-shell";

/**
 * "Tablero". Its Lista/Posiciones view lives in the URL query (`?view=positions`), read on the client
 * (useSearchParams), so the page renders inside Suspense (UI-PHASE3-INBOX-2026-10-10).
 */
export default function OperationsDashboardPage() {
  return (
    <Suspense fallback={<p className="live">Cargando el tablero…</p>}>
      <OperationsDashboardShell />
    </Suspense>
  );
}
