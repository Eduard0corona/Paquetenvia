import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, resolve } from "node:path";
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

function sources(directory: string): string[] {
  return readdirSync(directory).flatMap((name) => {
    const path = join(directory, name);
    if (statSync(path).isDirectory()) return sources(path);
    return /\.(ts|tsx|css)$/.test(name) && !name.includes(".test.") ? [path] : [];
  });
}

const read = (path: string) => readFileSync(resolve(process.cwd(), path), "utf8");

// GATE-001-NEUTRAL-PUBLIC-BRAND-2026-10-10: the commercial name is not
// validated before IMPI, so nothing a recipient reaches through a tracking link
// names it until GATE-001 closes.
describe("public tracking neutral branding", () => {
  it("never names the commercial name in a public tracking source", () => {
    const files = [
      ...sources(resolve(process.cwd(), "src/tracking")),
      ...sources(resolve(process.cwd(), "src/app/track")),
      resolve(process.cwd(), "src/app/not-found.tsx"),
      resolve(process.cwd(), "src/app/global-error.tsx"),
    ];
    expect(files.length).toBeGreaterThan(10);
    for (const file of files) {
      expect(readFileSync(file, "utf8"), file).not.toMatch(/paquetenv/i);
    }
  });

  it("gives /track and the not-found page a neutral head without the driver PWA manifest", () => {
    const page = read("src/app/track/page.tsx");
    expect(page).toContain("export const metadata: Metadata = publicTrackingMetadata;");
    const notFound = read("src/app/not-found.tsx");
    expect(notFound).toContain('title: "Página no encontrada",');
    expect(notFound).toContain("manifest: null,");
  });

  it("styles the heading, not a brand", () => {
    const css = read("src/app/globals.css");
    expect(css).toContain(".trackingCard .trackingHeading {");
    expect(css).not.toContain("trackingBrand");
  });

  it("is not configured with the commercial name by any build or deployment", () => {
    const files = [
      ...readdirSync(resolve(process.cwd(), "../../.github/workflows")).map(
        (name) => `../../.github/workflows/${name}`,
      ),
      "../../deploy/azure/Dockerfile.web",
      "../../deploy/azure/pilot/Dockerfile.web",
      "../../deploy/azure/pilot/apps.bicep",
    ];
    for (const file of files) {
      for (const line of read(file).split("\n")) {
        const at = line.indexOf("NEXT_PUBLIC_TRACKING_BRAND_NAME");
        if (at >= 0) expect(line.slice(at), file).not.toMatch(/paquetenv/i);
      }
    }
    expect(read("../../.github/workflows/deploy-azure-pilot.yml")).not.toContain(
      "--build-arg NEXT_PUBLIC_TRACKING_BRAND_NAME",
    );
  });

  it("has no support link by default while GATE-001 is open", () => {
    const workflow = read("../../.github/workflows/deploy-azure-pilot.yml");
    expect(workflow).toContain('support_url="${PILOT_TRACKING_SUPPORT_URL:-}"');
    expect(workflow).not.toContain("PILOT_TRACKING_SUPPORT_URL:-https://");
    const aiSeven = read("../../docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml");
    expect(aiSeven).toContain("GATE-001-TRACKING-SUPPORT-LINK-2026-10-11");
  });

  it("follows the AI-07 public tracking branding contract", () => {
    const aiSeven = read("../../docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml");
    const contract = aiSeven.slice(aiSeven.indexOf("  public_tracking:"));
    expect(contract).toContain("decision: GATE-001-NEUTRAL-PUBLIC-BRAND-2026-10-10");
    expect(contract).toContain("heading: Seguimiento de envío");
  });
});
