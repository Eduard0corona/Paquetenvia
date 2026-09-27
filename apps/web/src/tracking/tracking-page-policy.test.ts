import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

const config = readFileSync(resolve(process.cwd(), "next.config.ts"), "utf8");
const proxy = readFileSync(resolve(process.cwd(), "src/proxy.ts"), "utf8");
const csp = readFileSync(
  resolve(process.cwd(), "src/security/security-headers.ts"),
  "utf8",
);

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
      expect(`${config}\n${proxy}\n${csp}`).toContain(header);
    }
    expect(csp).toContain("default-src 'self'");
    expect(csp).toContain("object-src 'none'");
    expect(csp).toContain("frame-ancestors 'none'");
    expect(csp).toContain("connect-src");
    expect(csp).not.toContain("connect-src *");
  });

  it("redacts incoming tracking paths and rewrites to a generic shell", () => {
    expect(config).toContain("ignore: [/^\\/track(?:\\/|$)/]");
    expect(config).toContain('source: "/track/:token"');
    expect(config).toContain('destination: "/track"');
  });
});
