import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it, vi } from "vitest";
import type { DriverSession } from "../session/driver-session";
import { createDriverSyncApi } from "./driver-sync-api";
import {
  OfflineOperationExpiredCode,
  OfflineOperationMaximumAgeHours,
  OfflineOperationMaximumAgeMilliseconds,
  isOfflineOperationExpired,
  isOfflineOperationExpiredCode,
} from "./offline-operation-age";
import {
  createDriverOfflineOperation,
  parseDriverOfflineOperation,
} from "./operation-contract";

const openApi = readFileSync(
  resolve(process.cwd(), "../../docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml"),
  "utf8",
);
const session: DriverSession = {
  organizationId: "11111111-1111-4111-8111-111111111111",
  cacheNamespace: "opaque-synthetic-session",
  getAccessToken: () => "memory-only-token",
};
const capturedAt = "2026-07-26T12:00:00.000Z";
const hash = "a".repeat(64);

describe("OPS-003 offline operation age", () => {
  it("keeps the single client constant aligned with AI-05 x-offline-operation-age", () => {
    const section = sliceBetween(
      openApi,
      "x-offline-operation-age:",
      "x-runtime-contracts:",
    );
    expect(section).toContain(`maximum_age: PT${OfflineOperationMaximumAgeHours}H`);
    expect(section).toContain("maximum_age_configurable: false");
    expect(section).toContain("transitionOrder: client_occurred_at (optional)");
    expect(section).toContain(
      "createProofUploadSession: client_occurred_at (optional)",
    );
    expect(section).toContain("finalizeProof: captured_at (required)");
    expect(OfflineOperationMaximumAgeMilliseconds).toBe(
      OfflineOperationMaximumAgeHours * 3_600_000,
    );
    for (const [start, end] of [
      ["    TransitionConflictProblem:", "    ProofConflictProblem:"],
      ["    ProofConflictProblem:", "    FinanceConflictProblem:"],
    ] as const) {
      expect(sliceBetween(openApi, start, end)).toContain(
        `- ${OfflineOperationExpiredCode}`,
      );
    }
  });

  it("accepts exactly the maximum age and expires anything older, as the server does", () => {
    const captured = Date.parse(capturedAt);
    const at = (offset: number) =>
      new Date(captured + OfflineOperationMaximumAgeMilliseconds + offset);
    expect(isOfflineOperationExpired(capturedAt, at(-1))).toBe(false);
    expect(isOfflineOperationExpired(capturedAt, at(0))).toBe(false);
    expect(isOfflineOperationExpired(capturedAt, at(1))).toBe(true);
    expect(isOfflineOperationExpired(capturedAt, new Date(captured - 60_000)))
      .toBe(false);
    expect(isOfflineOperationExpired("not-a-timestamp", at(1))).toBe(false);
  });

  it("recognizes only the stable expired code", () => {
    expect(isOfflineOperationExpiredCode("OFFLINE_OPERATION_EXPIRED")).toBe(true);
    for (const code of [null, undefined, "INVALID_REQUEST", "CONFLICT", ""]) {
      expect(isOfflineOperationExpiredCode(code)).toBe(false);
    }
  });

  it("preserves the capture instant across a queue reload and replays it unchanged", async () => {
    const captured = createDriverOfflineOperation({
      partitionKey: "C".repeat(43),
      orderId: "22222222-2222-4222-8222-222222222222",
      kind: "PICKUP_PROOF",
      expectedVersion: 4,
      proof: { contentType: "image/png", sizeBytes: 4, sha256: hash },
      now: () => new Date(capturedAt),
      randomUuid: () => "33333333-3333-4333-8333-333333333333",
    });
    // IndexedDB stores a structured clone; listOperations parses it back.
    const reloaded = parseDriverOfflineOperation(
      structuredClone({ ...captured, attemptCount: 3, status: "RETRY_WAIT" }),
      captured.partitionKey,
    );
    expect(reloaded.clientOccurredAt).toBe(capturedAt);
    expect(reloaded.capturedAt).toBe(capturedAt);

    const bodies: unknown[] = [];
    const fetch = vi.fn<typeof globalThis.fetch>(async (_url, init) => {
      bodies.push(JSON.parse(init?.body as string));
      return problem(503, {});
    });
    const api = createDriverSyncApi({
      baseUrl: "https://api.synthetic.test",
      session,
      fetch,
    });
    await expect(api.transitionOrder(reloaded)).rejects.toBeDefined();
    await expect(
      api.createProofUploadSession(reloaded, {
        contentType: "image/png",
        sizeBytes: 4,
        sha256: hash,
      }),
    ).rejects.toBeDefined();
    await expect(
      api.finalizeProof(reloaded, "44444444-4444-4444-8444-444444444444", hash),
    ).rejects.toBeDefined();

    expect(bodies).toEqual([
      expect.objectContaining({ client_occurred_at: capturedAt }),
      expect.objectContaining({ client_occurred_at: capturedAt }),
      expect.objectContaining({ captured_at: capturedAt }),
    ]);
    expect(bodies[2]).not.toHaveProperty("client_occurred_at");
  });

  it.each([
    ["transitionOrder", { code: OfflineOperationExpiredCode }, OfflineOperationExpiredCode],
    ["transitionOrder", {}, null],
    ["createProofUploadSession", { code: OfflineOperationExpiredCode }, OfflineOperationExpiredCode],
    ["createProofUploadSession", { code: "INVALID_REQUEST" }, "INVALID_REQUEST"],
    ["finalizeProof", { code: OfflineOperationExpiredCode }, OfflineOperationExpiredCode],
    ["finalizeProof", { code: "INVALID_REQUEST" }, "INVALID_REQUEST"],
  ] as const)(
    "surfaces the %s 409 code %j without reinterpreting it",
    async (operationName, body, expected) => {
      const operation = createDriverOfflineOperation({
        partitionKey: "C".repeat(43),
        orderId: "22222222-2222-4222-8222-222222222222",
        kind: "DELIVERY_PROOF",
        expectedVersion: 9,
        proof: { contentType: "image/png", sizeBytes: 4, sha256: hash },
        now: () => new Date(capturedAt),
        randomUuid: () => "33333333-3333-4333-8333-333333333333",
      });
      const api = createDriverSyncApi({
        baseUrl: "https://api.synthetic.test",
        session,
        fetch: vi.fn<typeof globalThis.fetch>().mockResolvedValue(
          problem(409, { status: 409, title: "Conflict", ...body }),
        ),
      });
      const call =
        operationName === "transitionOrder"
          ? api.transitionOrder(operation)
          : operationName === "createProofUploadSession"
            ? api.createProofUploadSession(operation, {
                contentType: "image/png",
                sizeBytes: 4,
                sha256: hash,
              })
            : api.finalizeProof(
                operation,
                "44444444-4444-4444-8444-444444444444",
                hash,
              );
      await expect(call).rejects.toMatchObject({
        category: "conflict",
        publicCode: expected,
      });
    },
  );
});

function problem(status: number, body: Record<string, unknown>): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/problem+json" },
  });
}

function sliceBetween(source: string, start: string, end: string): string {
  const startIndex = source.indexOf(start);
  const endIndex = source.indexOf(end, startIndex + start.length);
  expect(startIndex).toBeGreaterThanOrEqual(0);
  expect(endIndex).toBeGreaterThan(startIndex);
  return source.slice(startIndex, endIndex);
}
