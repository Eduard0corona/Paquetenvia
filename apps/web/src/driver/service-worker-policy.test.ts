import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

const source = readFileSync(resolve(process.cwd(), "public/sw.js"), "utf8");

describe("driver Service Worker policy", () => {
  it("uses a versioned driver-owned cache", () => {
    expect(source).toContain('CACHE_NAME = "paquetenvia-driver-shell-v1"');
    expect(source).toContain('"paquetenvia-driver-shell-"');
  });

  it("keeps API and hubs network-only", () => {
    expect(source).toContain('url.pathname.startsWith("/api/v1/")');
    expect(source).toContain('url.pathname.startsWith("/hubs/")');
  });

  it("does not cache Authorization, cross-origin or signed URL requests", () => {
    expect(source).toContain('request.headers.has("Authorization")');
    expect(source).toContain("url.origin !== self.location.origin");
    expect(source).toContain('"x-amz-signature"');
    expect(source).toContain('"x-goog-signature"');
    expect(source).toContain('"signature"');
  });

  it("uses network-first driver shells and cache-first static assets", () => {
    expect(source).toContain("networkFirst(request)");
    expect(source).toContain("cacheFirst(request)");
    expect(source).toContain('url.pathname.startsWith("/_next/static/")');
  });

  it("never precaches authenticated stop data", () => {
    const installAssets = source.match(/const INSTALL_ASSETS = \[(.*?)\];/s)?.[1];
    expect(installAssets).toContain("/manifest.webmanifest");
    expect(installAssets).not.toContain("/api/");
    expect(installAssets).not.toContain("/driver/stops");
  });

  it("deletes only older Paquetenvia caches and claims clients", () => {
    expect(source).toContain("OWNED_CACHE_PREFIXES.some");
    expect(source).toContain("self.clients.claim()");
    expect(source).not.toContain("keys.filter((key) => key !== CACHE_NAME)");
  });

  it("does not cache Set-Cookie or opaque responses", () => {
    expect(source).toContain('response.headers.has("Set-Cookie")');
    expect(source).toContain('response.type !== "opaque"');
  });
});
