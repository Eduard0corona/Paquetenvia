import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

const source = readFileSync(resolve(process.cwd(), "public/sw.js"), "utf8");

describe("public tracking Service Worker privacy policy", () => {
  it("keeps every public tracking surface network-only", () => {
    expect(source).toContain('url.pathname === "/track"');
    expect(source).toContain('url.pathname.startsWith("/track/")');
    expect(source).toContain('url.pathname.startsWith("/api/v1/")');
    expect(source).toContain('url.pathname.startsWith("/hubs/")');
    expect(source).toContain('url.searchParams.has("access_token")');
    expect(source).toContain('request.headers.has("Authorization")');
  });

  it("does not use the driver shell as a tracking fallback", () => {
    const driverNavigationPolicy = source.match(
      /function isDriverStopsNavigation\(url\) \{([\s\S]*?)\n\}/,
    )?.[1];
    expect(driverNavigationPolicy).toBeDefined();
    expect(driverNavigationPolicy).not.toContain("/track");
    expect(source).not.toContain('INSTALL_ASSETS = ["/track');
  });
});
