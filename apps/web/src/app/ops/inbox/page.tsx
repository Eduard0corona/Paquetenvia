import { Suspense } from "react";
import { OperationsInboxShell } from "@/operations/components/operations-inbox-shell";

/**
 * UI-PHASE3-INBOX-2026-10-10: "Bandeja de trabajo". The selected queue and filters live in the
 * URL query, read on the client (useSearchParams), so the page renders inside Suspense.
 */
export default function OperationsInboxPage() {
  return (
    <Suspense fallback={<p className="live">Cargando la bandeja de trabajo…</p>}>
      <OperationsInboxShell />
    </Suspense>
  );
}
