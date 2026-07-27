import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

const config = readFileSync(resolve(process.cwd(), "next.config.ts"), "utf8");
const proxy = readFileSync(resolve(process.cwd(), "src/proxy.ts"), "utf8");

describe("public tracking page privacy policy", () => {
  it("sets the complete tracking headers and a bounded CSP", () => {
    for (const header of [
      "no-store, private",
      "no-cache",
      "no-referrer",
      "noindex, nofollow, noarchive",
      "nosniff",
      "geolocation=(), camera=(), microphone=(), payment=(), usb=()",
    ]) {
      expect(`${config}\n${proxy}`).toContain(header);
    }
    expect(config).toContain("default-src 'self'");
    expect(config).toContain("object-src 'none'");
    expect(config).toContain("frame-ancestors 'none'");
    expect(config).toContain("connect-src");
    expect(config).not.toContain("connect-src *");
  });

  it("redacts incoming tracking paths and rewrites to a generic shell", () => {
    expect(config).toContain("ignore: [/^\\/track(?:\\/|$)/]");
    expect(config).toContain('source: "/track/:token"');
    expect(config).toContain('destination: "/track"');
  });
});
