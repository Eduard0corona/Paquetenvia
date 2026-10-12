import { OperationsOrderDetailShell } from "@/operations/components/operations-order-detail-shell";
import { inboxReturnHref, inboxReturnParameter } from "@/operations/contracts/inbox";

export default async function OperationsOrderPage({
  params,
  searchParams,
}: {
  readonly params: Promise<{ readonly orderId: string }>;
  readonly searchParams: Promise<Readonly<Record<string, string | string[] | undefined>>>;
}) {
  const [{ orderId }, query] = await Promise.all([params, searchParams]);
  // UI-PHASE3-INBOX-2026-10-10: opened from the inbox, the detail returns to the same view.
  return (
    <OperationsOrderDetailShell
      orderId={orderId}
      inboxHref={inboxReturnHref(query[inboxReturnParameter])}
    />
  );
}
