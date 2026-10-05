import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const globals = readFileSync("src/app/globals.css", "utf8").replace(/\/\*[\s\S]*?\*\//g, "");
const driver = readFileSync("src/driver/components/driver-stops.module.css", "utf8").replace(/\/\*[\s\S]*?\*\//g, "");

/** Body of the first `{ ... }` block that starts at `start`, honoring nested braces. */
function blockAt(source: string, start: number): string {
  const open = source.indexOf("{", start);
  let depth = 0;
  for (let index = open; index < source.length; index += 1) {
    if (source[index] === "{") depth += 1;
    if (source[index] === "}" && --depth === 0) return source.slice(open + 1, index);
  }
  throw new Error("Unbalanced CSS block.");
}

const rootBlock = blockAt(globals, globals.indexOf(":root"));
const darkStart = globals.indexOf("@media screen and (prefers-color-scheme: dark)");
const darkBlock = blockAt(globals, darkStart);
const darkRoot = blockAt(darkBlock, darkBlock.indexOf(":root"));
const declarations = (block: string) =>
  block
    .split(";")
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
const rootTokens = new Set(
  declarations(rootBlock)
    .filter((line) => line.startsWith("--"))
    .map((line) => line.slice(0, line.indexOf(":")).trim()),
);

/** The stylesheet with the token blocks removed: what every component rule is made of. */
const rules = globals.replace(rootBlock, "").replace(darkBlock, "");

describe("design tokens", () => {
  it("defines the whole token set on :root and lets the browser pick light or dark", () => {
    for (const token of [
      "--ink", "--muted", "--background", "--surface", "--surface-2", "--line",
      "--accent", "--accent-strong", "--accent-ink", "--accent-soft",
      "--ok", "--ok-soft", "--warn", "--warn-soft", "--crit", "--crit-soft", "--info", "--info-soft",
      "--focus", "--radius-sm", "--radius", "--space-1", "--space-4", "--target",
      "--font-sans", "--text-xs", "--text-sm", "--text-md", "--text-lg", "--text-xl", "--text-2xl",
    ]) expect(rootTokens, token).toContain(token);
    expect(rootBlock).toContain("color-scheme: light dark");
    expect(globals).not.toContain("color-scheme: light;");
    expect(rootBlock).toContain("--radius-sm: 6px");
    expect(rootBlock).toContain("--radius: 10px");
    expect(rootBlock).toMatch(/--accent: #0f766e/);
  });

  it("uses only tokens that :root defines", () => {
    for (const [name, source] of [["globals.css", globals], ["driver-stops.module.css", driver]] as const) {
      const used = new Set([...source.matchAll(/var\((--[A-Za-z0-9-]+)/g)].map((match) => match[1]));
      expect(used.size, name).toBeGreaterThan(5);
      for (const token of used) expect(rootTokens, `${name} uses ${token}`).toContain(token);
    }
  });

  it("redefines only existing tokens in the dark block", () => {
    expect(darkStart).toBeGreaterThan(0);
    // The block holds :root and nothing else.
    expect(darkBlock.replace(darkRoot, "").replace(/\s|:root|\{|\}/g, "")).toBe("");
    const lines = declarations(darkRoot);
    expect(lines.length).toBeGreaterThan(10);
    for (const line of lines) {
      const name = line.slice(0, line.indexOf(":")).trim();
      expect(name.startsWith("--"), line).toBe(true);
      expect(rootTokens, line).toContain(name);
    }
  });

  it("hard-codes no color outside the token blocks", () => {
    const color = /#[0-9a-fA-F]{3,8}\b|\brgba?\(|\bhsla?\(/;
    expect(rules).not.toMatch(color);
    expect(driver).not.toMatch(color);
  });

  it("keeps !important only for the reduced-motion reset and no global paragraph size", () => {
    const motion = blockAt(globals, globals.indexOf("@media (prefers-reduced-motion: reduce)"));
    expect(globals.replace(motion, "")).not.toContain("!important");
    expect(blockAt(globals, globals.search(/\np\s*\{/))).not.toContain("font-size");
  });

  it("keeps 44px targets, a visible focus ring, reduced motion and tracking print styles", () => {
    expect(rootBlock).toContain("--target: 44px");
    expect(rules).toMatch(/:focus-visible\s*\{\s*outline: 3px solid var\(--focus\)/);
    expect(globals).toContain("@media (prefers-reduced-motion: reduce)");
    expect(blockAt(globals, globals.indexOf("@media print"))).toContain(".trackingCard button");
    expect(globals).not.toMatch(/font-family:\s*Arial/);
  });
});
