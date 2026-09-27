import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it, vi } from "vitest";
import {
  buildContentSecurityPolicy,
  createNonce,
  isPrivateNoStorePath,
  resolveConfiguredConnectSources,
  resolveConnectSources,
} from "./security-headers";

const config = readFileSync(resolve(process.cwd(), "next.config.ts"), "utf8");
const proxy = readFileSync(resolve(process.cwd(), "src/proxy.ts"), "utf8");
const layout = readFileSync(resolve(process.cwd(), "src/app/layout.tsx"), "utf8");
const instrumentation = readFileSync(
  resolve(process.cwd(), "src/instrumentation.ts"),
  "utf8",
);
const readApp = (file: string) =>
  readFileSync(resolve(process.cwd(), "src/app", file), "utf8");

function directives(policy: string): Map<string, string> {
  return new Map(
    policy.split("; ").map((directive) => {
      const [name, ...values] = directive.split(" ");
      return [name, values.join(" ")];
    }),
  );
}

const production = (pathname: string) =>
  buildContentSecurityPolicy({
    nonce: "c3ludGhldGljLW5vbmNlLTE=",
    pathname,
    development: false,
    connectSources: ["'self'", "https://api.synthetic.test"],
  });

describe("content security policy", () => {
  it.each(["/", "/driver/stops", "/driver/stops/abc", "/ops/dashboard", "/track", "/dev"])(
    "is nonce-based with no unsafe-inline or unsafe-eval in production for %s",
    (pathname) => {
      const policy = production(pathname);
      expect(policy).not.toContain("unsafe-inline");
      expect(policy).not.toContain("unsafe-eval");
      expect(policy).not.toContain("*");
      const map = directives(policy);
      expect(map.get("script-src")).toBe(
        "'self' 'nonce-c3ludGhldGljLW5vbmNlLTE=' 'strict-dynamic'",
      );
      expect(map.get("style-src")).toBe("'self' 'nonce-c3ludGhldGljLW5vbmNlLTE='");
      expect(map.get("frame-ancestors")).toBe("'none'");
      expect(map.get("object-src")).toBe("'none'");
      expect(map.get("base-uri")).toBe("'none'");
      expect(map.get("worker-src")).toBe("'self'");
      expect(map.get("default-src")).toBe("'self'");
      expect(map.get("connect-src")).toBe("'self' https://api.synthetic.test");
    },
  );

  it("forbids form submission from public tracking only", () => {
    expect(directives(production("/track")).get("form-action")).toBe("'none'");
    expect(directives(production("/track/opaque")).get("form-action")).toBe("'none'");
    expect(directives(production("/ops/dashboard")).get("form-action")).toBe("'self'");
  });

  it("allows eval only for the development runtime", () => {
    const policy = buildContentSecurityPolicy({
      nonce: createNonce(),
      pathname: "/driver/stops",
      development: true,
      connectSources: ["'self'"],
    });
    expect(directives(policy).get("script-src")).toContain("'unsafe-eval'");
  });

  it("creates unpredictable base64 nonces", () => {
    const nonces = new Set(Array.from({ length: 50 }, () => createNonce()));
    expect(nonces.size).toBe(50);
    for (const nonce of nonces) expect(nonce).toMatch(/^[A-Za-z0-9+/]{22}==$/);
  });

  it("rejects a nonce that could inject directives", () => {
    expect(() =>
      buildContentSecurityPolicy({
        nonce: "abc'; script-src *",
        pathname: "/",
        development: false,
        connectSources: ["'self'"],
      }),
    ).toThrow();
  });
});

describe("connect sources", () => {
  it("adds the API origin and its WebSocket origin", () => {
    expect(
      resolveConnectSources("https://api.synthetic.test/base", undefined, true),
    ).toEqual(["'self'", "https://api.synthetic.test", "wss://api.synthetic.test"]);
    expect(resolveConnectSources(undefined, undefined, true)).toEqual(["'self'"]);
  });

  it("adds exact runtime origins such as the signed-upload storage", () => {
    expect(
      resolveConnectSources(
        undefined,
        " https://storage.synthetic.test  https://127.0.0.1:9443 ",
        true,
      ),
    ).toEqual(["'self'", "https://storage.synthetic.test", "https://127.0.0.1:9443"]);
  });

  it.each([
    "*",
    "https://*.synthetic.test",
    "https://storage.synthetic.test/bucket",
    "http://storage.synthetic.test",
    "javascript:alert(1)",
    "storage.synthetic.test",
  ])("rejects the unsafe runtime origin %s in production", (entry) => {
    expect(() => resolveConnectSources(undefined, entry, true)).toThrow();
  });

  it("validates the runtime configuration once, failing closed with a clear message", () => {
    vi.stubEnv("NODE_ENV", "production");
    vi.stubEnv("NEXT_PUBLIC_API_BASE_URL", "https://api.synthetic.test");
    vi.stubEnv("PAQUETERIA_CSP_CONNECT_SOURCES", "https://storage.synthetic.test");
    try {
      expect(resolveConfiguredConnectSources()).toEqual([
        "'self'",
        "https://api.synthetic.test",
        "wss://api.synthetic.test",
        "https://storage.synthetic.test",
      ]);
      vi.stubEnv("PAQUETERIA_CSP_CONNECT_SOURCES", "https://*.synthetic.test");
      expect(() => resolveConfiguredConnectSources()).toThrow(
        /Invalid Content-Security-Policy configuration.*PAQUETERIA_CSP_CONNECT_SOURCES/,
      );
    } finally {
      vi.unstubAllEnvs();
    }
    expect(instrumentation).toContain("resolveConfiguredConnectSources()");
    expect(proxy).toMatch(
      /^const connectSources = resolveConfiguredConnectSources\(\);$/m,
    );
  });

  it("accepts plain http origins only outside production", () => {
    expect(
      resolveConnectSources(undefined, "http://127.0.0.1:9000", false),
    ).toEqual(["'self'", "http://127.0.0.1:9000"]);
  });
});

describe("header wiring", () => {
  it("emits the CSP from the proxy for every page, including /driver", () => {
    expect(proxy).toContain('response.headers.set("Content-Security-Policy"');
    expect(proxy).toContain('requestHeaders.set("Content-Security-Policy"');
    expect(proxy).toContain("matcher");
  });

  it("forwards same-origin API routes and static assets without a page CSP", () => {
    const matcher = /"\/\(\(\?!([^)]*)\)\.\*\)"/.exec(proxy)?.[1] ?? "";
    const excluded = matcher.split("|");
    for (const prefix of [
      "api/",
      "hubs/",
      "auth/",
      "signin-authcenter",
      "icons/",
      "_next/static",
      "sw.js",
      "manifest.webmanifest",
    ]) {
      expect(excluded).toContain(prefix);
    }
    const pattern = new RegExp(`^/((?!${matcher}).*)$`);
    expect(pattern.test("/auth/callback")).toBe(false);
    expect(pattern.test("/signin-authcenter")).toBe(false);
    expect(pattern.test("/driver/stops")).toBe(true);
    expect(pattern.test("/track/abc")).toBe(true);
  });

  it("replaces the framework 404 and global error pages, which inline styles", () => {
    for (const file of ["not-found.tsx", "global-error.tsx"]) {
      const source = readApp(file).replace(/\/\*[\s\S]*?\*\//g, "");
      expect(source).toContain("className=");
      expect(source).not.toMatch(/style=|<style/);
    }
    expect(readApp("global-error.tsx")).toContain('import "./globals.css"');
  });

  it("never emits a second CSP from next.config.ts", () => {
    expect(config).not.toMatch(/key: "Content-Security-Policy"/i);
    expect(config).not.toContain("unsafe-inline");
  });

  it("sends the static security headers on every route", () => {
    expect(config).toContain('source: "/:path*"');
    for (const header of [
      "Referrer-Policy",
      "X-Content-Type-Options",
      "Permissions-Policy",
      "X-Frame-Options",
    ]) {
      expect(config).toContain(header);
    }
    expect(config).toContain('source: "/driver/:path*"');
  });

  it("renders every page dynamically so each response carries its nonce", () => {
    expect(layout).toContain("await connection()");
  });

  it("keeps private no-store only on tracking and operations", () => {
    expect(isPrivateNoStorePath("/track")).toBe(true);
    expect(isPrivateNoStorePath("/track/abc")).toBe(true);
    expect(isPrivateNoStorePath("/ops/dashboard")).toBe(true);
    expect(isPrivateNoStorePath("/driver/stops")).toBe(false);
    expect(isPrivateNoStorePath("/tracking")).toBe(false);
  });
});
