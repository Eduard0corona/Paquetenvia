/**
 * Fail-closed readers for AI-05 JSON responses. Every helper throws
 * {@link ContractViolationError} on the first value that does not match the
 * contract; callers never receive a partially trusted object.
 */
export class ContractViolationError extends Error {
  public constructor() {
    super("La respuesta no cumple el contrato.");
    this.name = "ContractViolationError";
  }
}

export function fail(): never {
  throw new ContractViolationError();
}

const uuidPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

export function isCanonicalUuid(value: string): boolean {
  return uuidPattern.test(value) && value !== "00000000-0000-0000-0000-000000000000";
}

/** An object with exactly `required` keys plus, optionally, any of `optional`. */
export function exactObject(
  value: unknown,
  required: readonly string[],
  optional: readonly string[] = [],
): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) fail();
  const object = value as Record<string, unknown>;
  const keys = Object.keys(object);
  for (const key of required) if (!Object.hasOwn(object, key)) fail();
  for (const key of keys) if (!required.includes(key) && !optional.includes(key)) fail();
  return object;
}

export function array(value: unknown, maximum: number): readonly unknown[] {
  if (!Array.isArray(value) || value.length > maximum) fail();
  return value;
}

export function boundedString(value: unknown, minimum: number, maximum: number): string {
  if (typeof value !== "string" || value.length < minimum || value.length > maximum) fail();
  return value;
}

export function uuid(value: unknown): string {
  const text = boundedString(value, 36, 36);
  if (!isCanonicalUuid(text)) fail();
  return text;
}

export function integer(value: unknown, minimum = -Number.MAX_SAFE_INTEGER): number {
  if (!Number.isSafeInteger(value) || (value as number) < minimum) fail();
  return value as number;
}

export function boolean(value: unknown): boolean {
  if (typeof value !== "boolean") fail();
  return value;
}

/** RFC 3339 timestamp with an explicit offset. */
export function timestamp(value: unknown): string {
  const text = boundedString(value, 20, 40);
  if (!/(?:Z|[+-]\d{2}:\d{2})$/.test(text) || Number.isNaN(Date.parse(text))) fail();
  return text;
}

export function nullable<T>(value: unknown, read: (value: unknown) => T): T | null {
  return value === null ? null : read(value);
}

export function oneOf<const T extends readonly string[]>(value: unknown, values: T): T[number] {
  if (typeof value !== "string" || !values.includes(value)) fail();
  return value as T[number];
}
