import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const read = (path: string) => readFileSync(path, "utf8");

/** Every source of "Cuenta > Documentos" (DRV-DOC-001-IN-PERSON-2026-11-02, AI-07 driver_account documents). */
const sources = [
  "src/driver/api/driver-documents-api.ts",
  "src/driver/components/driver-documents-list.tsx",
  "src/driver/contracts/driver-documents.ts",
  "src/driver/state/driver-documents-controller.ts",
];

const strip = (source: string) => source.replace(/\/\*[\s\S]*?\*\//g, "").replace(/^\s*\/\/.*$/gm, "");

describe("driver documents policy (DRV-DOC-001-IN-PERSON-2026-11-02)", () => {
  it("keeps the documents in memory only, out of the offline queue, and never logs them", () => {
    for (const path of sources) {
      const source = strip(read(path));
      expect(source, path).not.toMatch(/localStorage|sessionStorage|indexedDB|caches\.|console\.|sendBeacon/);
      expect(source, path).not.toMatch(/from "\.\.\/offline|from "\.\.\/cache|driver-stops-cache|enqueue/);
    }
  });

  it("only reads the driver's own documents: one GET, no write, no upload (Y3, Y8)", () => {
    const paths = new Set<string>();
    for (const path of sources) for (const match of read(path).matchAll(/["`](\/api\/v1\/[^"`?$]*)/g)) paths.add(match[1]);
    expect([...paths]).toEqual(["/api/v1/driver/me/documents"]);
    const api = strip(read("src/driver/api/driver-documents-api.ts"));
    expect(api).toContain('method: "GET"');
    expect(api).not.toMatch(/method: "(POST|PUT|PATCH|DELETE)"|Idempotency-Key|^\s+body[,:]/m);
    const list = strip(read("src/driver/components/driver-documents-list.tsx"));
    expect(list.match(/<button\b/g)).toHaveLength(1);
    expect(list).toContain('"Actualizar documentos"');
    expect(list).not.toMatch(/<input|<form|<select|<textarea|type="file"|capture=|<img|<video|<canvas/);
    expect(list).not.toMatch(/n[uú]mero|folio|foto|photo|object_key|sha256|reviewed_by|\.id\b/i);
  });

  it("is read when Cuenta opens, never otherwise, and shows nothing while documents are off", () => {
    const account = read("src/driver/components/driver-account.tsx");
    expect(account).toContain("{accountOpen ? <DriverDocumentsList session={session} /> : null}");
    const list = read("src/driver/components/driver-documents-list.tsx");
    expect(list).toContain('if (state.phase === "hidden") return null;');
    expect(list).toContain("void controller.load(navigator.onLine);");
    for (const path of sources) expect(read(path), path).not.toMatch(/setInterval|HubConnection|signalr|serviceWorker/i);
  });
});
