import { OperationsOrderDetailShell } from "@/operations/components/operations-order-detail-shell";

export default async function OperationsOrderPage({
  params,
}: {
  readonly params: Promise<{ readonly orderId: string }>;
}) {
  const { orderId } = await params;
  return <OperationsOrderDetailShell orderId={orderId} />;
}
