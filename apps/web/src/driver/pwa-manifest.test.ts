import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

interface ManifestIcon {
  readonly src: string;
  readonly sizes: string;
  readonly type: string;
  readonly purpose: string;
}

const manifest = JSON.parse(readFileSync("public/manifest.webmanifest", "utf8")) as {
  readonly icons?: readonly ManifestIcon[];
};

function pngSize(path: string): { readonly width: number; readonly height: number } {
  const bytes = readFileSync(path);
  expect(bytes.subarray(0, 8).toString("hex")).toBe("89504e470d0a1a0a");
  expect(bytes.subarray(12, 16).toString("ascii")).toBe("IHDR");
  return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) };
}

describe("PWA manifest icons", () => {
  it("declares 192 and 512 PNG icons for any and maskable purposes", () => {
    const icons = manifest.icons ?? [];
    for (const purpose of ["any", "maskable"])
      for (const size of ["192x192", "512x512"])
        expect(icons.some((icon) => icon.purpose === purpose && icon.sizes === size && icon.type === "image/png"))
          .toBe(true);
  });

  it("serves every icon same-origin from /icons/ with the declared dimensions", () => {
    for (const icon of manifest.icons ?? []) {
      expect(icon.src).toMatch(/^\/icons\/[a-z0-9-]+\.png$/);
      const [width, height] = icon.sizes.split("x").map(Number);
      expect(pngSize(`public${icon.src}`)).toEqual({ width, height });
    }
  });

  it("keeps the icons inside the CSP, the static-asset cache and outside the nonce proxy", () => {
    expect(readFileSync("src/security/security-headers.ts", "utf8")).toContain("img-src 'self'");
    expect(readFileSync("public/sw.js", "utf8")).toContain('url.pathname.startsWith("/icons/")');
    expect(readFileSync("src/proxy.ts", "utf8")).toContain("icons/|");
  });
});
