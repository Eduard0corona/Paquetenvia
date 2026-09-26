import { existsSync, readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { resolvePageExtensions } from "../../next.config";

const config = readFileSync(join(process.cwd(), "next.config.ts"), "utf8");

describe("development portal build exclusion", () => {
  it("registers the dev.tsx page extension only when /dev is included", () => {
    expect(resolvePageExtensions(false)).toEqual(["tsx", "ts", "jsx", "js"]);
    expect(resolvePageExtensions(true)).toEqual([
      "dev.tsx",
      "tsx",
      "ts",
      "jsx",
      "js",
    ]);
  });

  it("includes /dev only for next dev or an exact explicit build opt-in", () => {
    expect(config).toContain("pageExtensions: resolvePageExtensions(");
    expect(config).toContain('process.env.NODE_ENV === "development" ||');
    expect(config).toContain(
      'process.env.PAQUETERIA_DEV_PORTAL_BUILD === "true"',
    );
  });

  it("keeps every /dev route file behind the dev.tsx extension", () => {
    const directory = join(process.cwd(), "src/app/dev");
    const routeFiles = readdirSync(directory).filter((file) =>
      /^(page|layout|route|template|loading|error|not-found|default)\./.test(
        file,
      ),
    );
    expect(routeFiles).toEqual(["page.dev.tsx"]);
    expect(existsSync(join(directory, "page.tsx"))).toBe(false);
  });
});
