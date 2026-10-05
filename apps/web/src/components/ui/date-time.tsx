import { formatMazatlanTime } from "@/operations/contracts/operations-formatters";

/** A point in time shown in Mazatlán time, with the machine-readable instant in dateTime. */
export function DateTime({ value }: { readonly value: string | Date }) {
  const instant = typeof value === "string" ? value : value.toISOString();
  return <time dateTime={instant}>{formatMazatlanTime(value)}</time>;
}
