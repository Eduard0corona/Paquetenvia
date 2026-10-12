import { Suspense } from "react";
import { OperationsOrdersShell } from "@/operations/components/operations-orders-shell";

/**
 * UI-PHASE3-ORDERS-LIST-2026-10-12: "Órdenes", every order of the selected organization. The status and creation
 * period live in the URL query, read on the client (useSearchParams), so the page renders inside Suspense.
 */
export default function OperationsOrdersPage() {
  return (
    <Suspense fallback={<p className="live">Cargando órdenes…</p>}>
      <OperationsOrdersShell />
    </Suspense>
  );
}
