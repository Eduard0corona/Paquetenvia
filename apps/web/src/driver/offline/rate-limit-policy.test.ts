import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

/**
 * SEC-004-HEAVY-ENDPOINT-MAPPING-2026-10-12: the driver app's answer to a 429
 * is the one AI-07 records (screen_contracts.driver_stop.rate_limit), and the
 * photo steps it applies to are the ones AI-05 limits.
 */
const repository = (path: string) => readFileSync(resolve(process.cwd(), "../..", path), "utf8");
const uiContracts = repository("docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml").replace(/\s+/g, " ");
const openApi = repository("docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml");
const api = repository("apps/web/src/driver/offline/driver-sync-api.ts");
const scheduler = repository("apps/web/src/driver/offline/driver-sync-scheduler.ts");

describe("driver photo rate limit policy (SEC-004)", () => {
  it("records the owner's answers and the behavior in AI-07", () => {
    expect(uiContracts).toContain("rate_limit: decision: SEC-004-HEAVY-ENDPOINT-MAPPING-2026-10-12");
    expect(uiContracts).toContain("createProofUploadSession and finalizeProof, 60 per minute each per driver");
    expect(uiContracts).toContain("la cola espera lo que indique el servidor y reintenta sin mensaje");
    expect(uiContracts).toContain("the driver sees no message");
  });

  it("limits exactly the two photo calls the queue sends", () => {
    for (const operation of ["createProofUploadSession", "finalizeProof"]) {
      const start = openApi.indexOf(`operationId: ${operation}`);
      const next = openApi.indexOf("operationId:", start + 1);
      expect(openApi.slice(start, next)).toContain("$ref: '#/components/responses/TooManyRequests'");
    }
  });

  it("reads the 429 as a wait, never as rejected evidence or a conflict", () => {
    expect(api).toMatch(/response\.status === 429\) \{\s*const seconds = readRetryAfterSeconds\(response\.headers\);/);
    expect(scheduler).toMatch(/error\.category === "rate-limited"\) \{\s*await this\.waitForRateLimit\(/);
    const wait = scheduler.slice(
      scheduler.indexOf("private async waitForRateLimit("),
      scheduler.indexOf("private async deferRestConfirmation("),
    );
    expect(wait).toContain('status: "RETRY_WAIT"');
    expect(wait).toContain("safeError: null");
    expect(wait).not.toContain("NEEDS_ATTENTION");
    expect(wait).not.toContain("EVIDENCE_REJECTED");
  });
});
