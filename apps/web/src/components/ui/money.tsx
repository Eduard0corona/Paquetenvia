import { formatMxnCents, formatMxnCentsWithCurrency } from "../../operations/contracts/money";

/**
 * Integer MXN cents for display (AI-01 §4.15): formatting is delegated to formatMxnCents,
 * which never turns an amount into a floating-point value.
 */
export function Money({
  cents,
  currency = true,
  strong = false,
}: {
  readonly cents: number;
  /** Append "MXN" (default) or show only the amount. */
  readonly currency?: boolean;
  readonly strong?: boolean;
}) {
  const text = currency ? formatMxnCentsWithCurrency(cents) : formatMxnCents(cents);
  return strong ? <strong className="tabular">{text}</strong> : <span className="tabular">{text}</span>;
}
