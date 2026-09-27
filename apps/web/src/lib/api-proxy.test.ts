import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import {
  assertSameOriginApiForBff,
  buildApiProxyRewrites,
  parseApiProxyOrigin,
} from "../../next.config";

describe("API same-origin proxy", () => {
  it("is disabled unless an origin is configured", () => {
    expect(parseApiProxyOrigin(undefined)).toBeUndefined();
    expect(parseApiProxyOrigin("  ")).toBeUndefined();
    expect(buildApiProxyRewrites(undefined)).toEqual([]);
  });

  it("rewrites the API, hubs, BFF and OIDC callback prefixes only", () => {
    const origin = parseApiProxyOrigin("http://api:8080");
    expect(buildApiProxyRewrites(origin)).toEqual([
      { source: "/api/:path*", destination: "http://api:8080/api/:path*" },
      { source: "/hubs/:path*", destination: "http://api:8080/hubs/:path*" },
      { source: "/auth/:path*", destination: "http://api:8080/auth/:path*" },
      { source: "/signin-authcenter", destination: "http://api:8080/signin-authcenter" },
    ]);
  });

  it("rejects origins with credentials, paths, queries or other schemes", () => {
    for (const value of [
      "api:8080",
      "ftp://api",
      "https://user:pass@api",
      "https://api/base",
      "https://api/?x=1",
      "https://api/#x",
    ]) {
      expect(() => parseApiProxyOrigin(value)).toThrow();
    }
  });

  it("forbids a cross-origin public API base in BFF mode", () => {
    expect(() => assertSameOriginApiForBff("bff", "https://api.example.test")).toThrow();
    expect(() => assertSameOriginApiForBff("bff", undefined)).not.toThrow();
    expect(() => assertSameOriginApiForBff(undefined, "https://api.example.test")).not.toThrow();
  });

  it("keeps next.config self-contained for the runtime image", () => {
    // deploy/azure/Dockerfile.web copies only .next, node_modules, public and
    // next.config.ts into the runtime stage; `next start` evaluates the config,
    // so any relative import would crash the container at startup.
    const source = readFileSync("next.config.ts", "utf8");
    const specifiers = [...source.matchAll(/\bfrom\s+["']([^"']+)["']|\bimport\s*\(\s*["']([^"']+)["']|\brequire\(\s*["']([^"']+)["']/g)]
      .map((match) => match[1] ?? match[2] ?? match[3]);
    expect(specifiers).toEqual(["next"]);
    const dockerfile = readFileSync("../../deploy/azure/Dockerfile.web", "utf8");
    expect(dockerfile).toContain("COPY --from=build /app/next.config.ts ./next.config.ts");
  });

  it("is wired into next.config without exposing the proxy origin publicly", () => {
    const source = readFileSync("next.config.ts", "utf8");
    expect(source).toContain("buildApiProxyRewrites(");
    expect(source).toContain("process.env.PAQUETENVIA_API_PROXY_ORIGIN");
    expect(source).not.toContain("NEXT_PUBLIC_API_PROXY");
    expect(source).toContain("assertSameOriginApiForBff(");
  });
});
