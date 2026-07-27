const orderDetailPattern =
  /^\/ops\/orders\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\/?$/;

export function currentOperationsOrderId(pathname: string): string | null {
  const match = orderDetailPattern.exec(pathname);
  if (
    match === null ||
    match[1] === "00000000-0000-0000-0000-000000000000"
  )
    return null;
  return match[1];
}

export function operationsOrderHref(orderId: string): string {
  if (!orderDetailPattern.test(`/ops/orders/${orderId}`))
    throw new Error("Invalid order id.");
  return `/ops/orders/${orderId}`;
}
