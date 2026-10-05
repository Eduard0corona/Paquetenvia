import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

// AI-01 §4.15 / CLAUDE.md invariant 4: money is integer cents; no screen may turn
// an amount into a floating-point value, neither to send it nor to display it.
function sourceFiles(directory: string): string[] {
  return readdirSync(directory).flatMap((entry) => {
    const path = join(directory, entry);
    if (statSync(path).isDirectory()) return sourceFiles(path);
    return /\.(ts|tsx)$/.test(entry) && !/\.test\.tsx?$/.test(entry) ? [path] : [];
  });
}

const floatingMoney = [
  /cents\s*\/\s*100(?!n)\b/i,
  /\*\s*100\s*\)/,
  /parseFloat\(/,
  /Number\(\s*data\.get\(/,
];

describe("integer-cents money in every screen", () => {
  it.each(sourceFiles("src"))("%s converts no amount through floating point", (file) => {
    const source = readFileSync(file, "utf8");
    for (const pattern of floatingMoney) expect(source).not.toMatch(pattern);
  });
});
