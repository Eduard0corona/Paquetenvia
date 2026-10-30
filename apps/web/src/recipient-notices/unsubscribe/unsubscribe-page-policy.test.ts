import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { skipsBffSessionBootstrap } from "../../auth/components/bff-session-bootstrap";
import { registersServiceWorker } from "../../components/service-worker-registration";
import { buildContentSecurityPolicy, isPrivateNoStorePath } from "../../security/security-headers";
import { recipientUnsubscribeMetadata } from "./unsubscribe-metadata";
import { recipientUnsubscribeTexts } from "./unsubscribe-texts";

const read = (path: string) => readFileSync(resolve(process.cwd(), path), "utf8");

function sources(directory: string): string[] {
  return readdirSync(directory).flatMap((name) => {
    const path = join(directory, name);
    if (statSync(path).isDirectory()) return sources(path);
    return /\.(ts|tsx)$/.test(name) && !name.includes(".test.") ? [path] : [];
  });
}

const pageSources = [
  ...sources(resolve(process.cwd(), "src/recipient-notices/unsubscribe")),
  ...sources(resolve(process.cwd(), "src/app/avisos")),
];

describe("the e-mail unsubscribe page privacy policy (AI-07 /avisos/baja/:token)", () => {
  it("serves one generic page for every token, never logs the address and is private and not indexed", () => {
    const config = read("next.config.ts");
    expect(config).toContain('source: "/avisos/baja/:token"');
    expect(config).toContain('destination: "/avisos/baja"');
    expect(config).toContain("/^\\/avisos\\/baja(?:\\/|$)/");
    expect(config).toContain('{ source: "/avisos/baja/:path*", headers: privateNoStore }');
    expect(isPrivateNoStorePath("/avisos/baja")).toBe(true);
    expect(isPrivateNoStorePath(`/avisos/baja/${"D".repeat(88)}`)).toBe(true);
    expect(isPrivateNoStorePath("/avisos")).toBe(false);
    const csp = buildContentSecurityPolicy({
      nonce: "AAAAAAAAAAAAAAAAAAAAAA==",
      pathname: `/avisos/baja/${"D".repeat(88)}`,
      development: false,
      connectSources: ["'self'"],
    });
    expect(csp).toContain("form-action 'none'");
    const worker = read("public/sw.js");
    expect(worker).toContain('url.pathname === "/avisos/baja"');
    expect(worker).toContain('url.pathname.startsWith("/avisos/baja/")');
  });

  it("has a neutral head without the driver PWA manifest", () => {
    expect(recipientUnsubscribeMetadata.title).toBe("Seguimiento de envío");
    expect(recipientUnsubscribeMetadata.manifest).toBeNull();
    expect(recipientUnsubscribeMetadata.robots).toEqual({ index: false, follow: false });
    expect(read("src/app/avisos/baja/page.tsx")).toContain(
      "export const metadata: Metadata = recipientUnsubscribeMetadata;",
    );
  });

  it("keeps the root layout's session bootstrap, service worker and manifest off the page", () => {
    // The root layout renders both components and the manifest link on every page; the page opts out of all three.
    const layout = read("src/app/layout.tsx");
    expect(layout).toContain("<ServiceWorkerRegistration />");
    expect(layout).toContain("<BffSessionBootstrap />");
    expect(layout).toContain('manifest: "/manifest.webmanifest",');
    const token = "D".repeat(88);
    for (const path of ["/avisos/baja", "/avisos/baja/", `/avisos/baja/${token}`, "/avisos/baja/x"]) {
      expect(skipsBffSessionBootstrap(path), path).toBe(true);
      expect(registersServiceWorker(path), path).toBe(false);
    }
    for (const path of ["/", "/ops", "/driver/stops", "/avisos", "/avisos/bajas"]) {
      expect(skipsBffSessionBootstrap(path), path).toBe(false);
      expect(registersServiceWorker(path), path).toBe(true);
    }
    expect(read("src/auth/components/bff-session-bootstrap.tsx")).toContain("if (skipsBffSessionBootstrap(path)) {");
    expect(read("src/components/service-worker-registration.tsx")).toContain(
      'if ("serviceWorker" in navigator && registersServiceWorker(window.location.pathname)) {',
    );
    // The page's metadata replaces the root manifest with null, so Next emits no manifest link for it.
    expect(recipientUnsubscribeMetadata.manifest).toBeNull();
  });

  it("uses no session, cookie, browser storage or brand", () => {
    expect(pageSources.length).toBeGreaterThanOrEqual(6);
    for (const file of pageSources) {
      const source = readFileSync(file, "utf8");
      for (const forbidden of [
        /localStorage|sessionStorage|indexedDB|document\.cookie|caches\./,
        /credentials:\s*"(?:include|same-origin)"/,
        /paquetenv/i,
        /console\./,
      ]) {
        expect(source, file).not.toMatch(forbidden);
      }
    }
  });

  it("shows exactly the texts the owner approved in AI-07 (O9)", () => {
    const aiSeven = read("../../docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml");
    const page = aiSeven.slice(aiSeven.indexOf("  /avisos/baja/:token:"));
    const texts = page.slice(page.indexOf("    texts:"), page.indexOf("\n  /", 1));
    expect(texts).toContain("decision: NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25");
    expect(texts).toContain(`heading: "${recipientUnsubscribeTexts.heading}"`);
    expect(texts).toContain(`button: "${recipientUnsubscribeTexts.button}"`);
    expect(texts).toContain(`success: "${recipientUnsubscribeTexts.success}"`);
    expect(texts).toContain(`invalid_link: "${recipientUnsubscribeTexts.invalidLink}"`);
    expect(texts).toContain('wait: "Demasiadas solicitudes. Espera N segundos e intenta de nuevo."');
    expect(texts).toContain(`failure: "${recipientUnsubscribeTexts.failure}"`);
    expect(page).toContain("status: DECIDED, implemented (AI-08 NTF-006)");
  });
});
