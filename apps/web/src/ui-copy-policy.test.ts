import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import { describe, expect, it } from "vitest";

/** The /dev portal is an English, development-only page; it stays out of this policy. */
const excludedDirectories = ["src/app/dev", "src/dev"];

function uiSources(directory: string): string[] {
  return readdirSync(directory).flatMap((name) => {
    const path = join(directory, name);
    if (excludedDirectories.includes(relative(process.cwd(), path))) return [];
    if (statSync(path).isDirectory()) return uiSources(path);
    return name.endsWith(".tsx") && !name.includes(".test.") ? [path] : [];
  });
}

/**
 * What a person can read on the page: JSX text and string literals (labels, messages,
 * attributes such as aria-label). Comments and import specifiers are dropped so identifiers
 * and module paths never count as copy.
 */
function visibleCopy(source: string): string[] {
  const code = source
    .replace(/\/\*[\s\S]*?\*\//g, " ")
    .replace(/^\s*\/\/.*$/gm, " ")
    .replace(/^\s*import\s[\s\S]*?from\s+["'][^"']+["'];?\s*$/gm, " ")
    .replace(/^\s*import\s+["'][^"']+["'];?\s*$/gm, " ");
  const literals = [...code.matchAll(/"(?:[^"\\\n]|\\.)*"|`(?:[^`\\]|\\.)*`/g)].map((match) => match[0].slice(1, -1));
  const jsxText = [...code.matchAll(/>([^<>{}]*[A-Za-zÁÉÍÓÚáéíóúÑñ][^<>{}]*)</g)].map((match) => match[1]);
  return [...literals, ...jsxText];
}

const forbiddenPhrases = [
  "con la API como autoridad",
  "como autoridad",
  "Recuperacion REST",
  "estado autoritativo",
  "Tal como las devolvió el servidor",
  "centavos exactos",
  "Proyección pendiente",
  "Acciones futuras",
  "Crear DRAFT",
  "Agregar DELIVERY",
  "CSV-001",
  "INC-001",
  "RTE-001",
  "EXT-001",
  "(UUID",
];
const forbiddenWords = ["Timeline", "Proofs", "Owner", "Operator", "Up", "Down"];

function violations(copy: readonly string[]): string[] {
  const found: string[] = [];
  for (const text of copy) {
    for (const phrase of forbiddenPhrases) if (text.includes(phrase)) found.push(`${phrase} in "${text.trim()}"`);
    for (const word of forbiddenWords)
      if (new RegExp(`(^|[^A-Za-z0-9_-])${word}($|[^A-Za-z0-9_-])`).test(text)) found.push(`${word} in "${text.trim()}"`);
  }
  return found;
}

describe("UI copy policy (es-MX, no engineering jargon)", () => {
  it("finds jargon in labels, JSX text and attributes but not in identifiers or imports", () => {
    const sample = [
      'import { publicTimelineLabels } from "./timeline";',
      "// Owner of the order (comment)",
      "const operatorOrganizationId = order.operatorOrganizationId;",
      '<ol className="opsTimeline">',
      '<h2 id="timeline-title">Timeline</h2>',
      '<label>Orden (UUID)<input /></label>',
      '<Row label="Owner" value={owner} />',
      "<button type=\"button\">Up</button>",
      "<p>Disponible después de INC-001.</p>",
    ].join("\n");
    expect(violations(visibleCopy(sample)).sort()).toEqual(
      [
        'Timeline in "Timeline"',
        '(UUID in "Orden (UUID)"',
        'Owner in "Owner"',
        'Up in "Up"',
        'INC-001 in "Disponible después de INC-001."',
      ].sort(),
    );
  });

  it("keeps internal ticket ids, server jargon and English leaks out of visible copy", () => {
    const sources = uiSources("src");
    expect(sources.length).toBeGreaterThan(20);
    const found = sources.flatMap((path) => violations(visibleCopy(readFileSync(path, "utf8"))).map((item) => `${path}: ${item}`));
    expect(found).toEqual([]);
  });

  it("never falls back to the browser's native confirm", () => {
    for (const path of uiSources("src")) {
      expect(readFileSync(path, "utf8"), path).not.toMatch(/window\.confirm|\bconfirm\(\s*["'`]/);
    }
  });
});
