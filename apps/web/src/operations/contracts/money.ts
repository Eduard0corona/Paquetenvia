/**
 * Money is always integer MXN cents (AI-01 §4.15, AI-05 x-runtime-contracts.money).
 * Every conversion here is integer arithmetic over BigInt; no floating-point value
 * ever represents an amount. Formatting exists only for display.
 */

const maxSafe = BigInt(Number.MAX_SAFE_INTEGER);
const groupFormatter = new Intl.NumberFormat("es-MX", {
  useGrouping: true,
  maximumFractionDigits: 0,
});

export class InvalidCentsError extends Error {
  public constructor() {
    super("The amount is not an exact integer number of cents.");
    this.name = "InvalidCentsError";
  }
}

/** Displays integer cents as `$1,234.56` (or `-$1,234.56`) without floating-point math. */
export function formatMxnCents(cents: number): string {
  if (!Number.isSafeInteger(cents)) throw new InvalidCentsError();
  const value = BigInt(cents);
  const negative = value < 0n;
  const absolute = negative ? -value : value;
  const whole = absolute / 100n;
  const fraction = (absolute % 100n).toString().padStart(2, "0");
  return `${negative ? "-" : ""}$${groupFormatter.format(whole)}.${fraction}`;
}

/** Same as {@link formatMxnCents} with an explicit currency code. */
export function formatMxnCentsWithCurrency(cents: number): string {
  return `${formatMxnCents(cents)} MXN`;
}

export interface ParseMxnOptions {
  readonly allowNegative?: boolean;
  readonly allowZero?: boolean;
}

const amountPattern = /^(-)?(\d{1,13})(?:\.(\d{1,2}))?$/;

/**
 * Parses a typed MXN amount such as `52`, `52.5` or `-10.05` into exact integer
 * cents. Returns `null` for anything that is not a plain decimal with at most two
 * fraction digits, or that falls outside the safe-integer range.
 */
export function parseMxnToCents(
  text: string,
  options: ParseMxnOptions = {},
): number | null {
  const match = amountPattern.exec(text.trim());
  if (match === null) return null;
  const [, sign, whole, fraction = ""] = match;
  let cents = BigInt(whole) * 100n + BigInt(fraction.padEnd(2, "0"));
  if (sign === "-") cents = -cents;
  if (cents > maxSafe || cents < -maxSafe) return null;
  if (cents < 0n && options.allowNegative !== true) return null;
  if (cents === 0n && options.allowZero === false) return null;
  // Normalise -0 to 0.
  return cents === 0n ? 0 : Number(cents);
}

/** Exact sum of integer cents; `null` when any value or the result is not a safe integer. */
export function sumCents(values: readonly number[]): number | null {
  let total = 0n;
  for (const value of values) {
    if (!Number.isSafeInteger(value)) return null;
    total += BigInt(value);
  }
  if (total > maxSafe || total < -maxSafe) return null;
  return Number(total);
}
