import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import {
  formatMxnCents,
  formatMxnCentsWithCurrency,
  InvalidCentsError,
  parseMxnToCents,
  sumCents,
} from "./money";

describe("integer cents formatting", () => {
  it.each([
    [0, "$0.00"],
    [5, "$0.05"],
    [5_200, "$52.00"],
    [123_456, "$1,234.56"],
    [-1_005, "-$10.05"],
    [100_000_000_01, "$100,000,000.01"],
    [Number.MAX_SAFE_INTEGER, "$90,071,992,547,409.91"],
    [-Number.MAX_SAFE_INTEGER, "-$90,071,992,547,409.91"],
  ])("formats %i cents as %s", (cents, expected) => {
    expect(formatMxnCents(cents)).toBe(expected);
  });

  it("appends the currency code", () => {
    expect(formatMxnCentsWithCurrency(4_500)).toBe("$45.00 MXN");
  });

  it.each([0.5, 1.1, Number.NaN, Number.POSITIVE_INFINITY, Number.MAX_SAFE_INTEGER + 1])(
    "refuses a non-integer or unsafe amount %s",
    (value) => {
      expect(() => formatMxnCents(value)).toThrow(InvalidCentsError);
    },
  );

  it("keeps floating-point division out of the money module", () => {
    const source = readFileSync("src/operations/contracts/money.ts", "utf8");
    expect(source).not.toMatch(/\/\s*100(?!n)\b/);
    expect(source).not.toMatch(/parseFloat|toFixed|Math\.round/);
  });
});

describe("typed MXN amounts to cents", () => {
  it.each([
    ["52", 5_200],
    ["52.5", 5_250],
    ["52.05", 5_205],
    ["0.1", 10],
    [" 1234.56 ", 123_456],
    ["0", 0],
    ["19.99", 1_999],
    ["0.29", 29],
  ])("parses %s exactly", (text, cents) => {
    expect(parseMxnToCents(text)).toBe(cents);
  });

  it.each(["", "abc", "1.234", "1,000.00", "1e3", ".5", "5.", "--1", "+1", "99999999999999"])(
    "refuses %s",
    (text) => {
      expect(parseMxnToCents(text)).toBeNull();
    },
  );

  it("accepts negatives only when allowed and can refuse zero", () => {
    expect(parseMxnToCents("-10.05")).toBeNull();
    expect(parseMxnToCents("-10.05", { allowNegative: true })).toBe(-1_005);
    expect(parseMxnToCents("0.00", { allowZero: false })).toBeNull();
    expect(parseMxnToCents("-0", { allowNegative: true })).toBe(0);
    expect(Object.is(parseMxnToCents("-0", { allowNegative: true }), -0)).toBe(false);
  });

  it("sums cents exactly and refuses overflow", () => {
    expect(sumCents([10, -3, 5_200])).toBe(5_207);
    expect(sumCents([])).toBe(0);
    expect(sumCents([Number.MAX_SAFE_INTEGER, 1])).toBeNull();
    expect(sumCents([0.5])).toBeNull();
  });
});
