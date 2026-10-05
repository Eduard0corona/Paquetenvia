/** First 8 characters of an identifier, for display only; links and requests use the full id. */
export function shortId(value: string): string {
  return value.slice(0, 8);
}
