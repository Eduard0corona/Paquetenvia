import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import {
  driverStopStatuses,
  driverStopTypes,
} from "./contracts/driver-stop";

const openApi = readFileSync(
  resolve(process.cwd(), "../../docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml"),
  "utf8",
);

describe("DRV-001 OpenAPI alignment", () => {
  it("uses the existing listMyStops array operation", () => {
    const operation = sliceBetween(openApi, "  /driver/me/stops:", "  /orders/");
    expect(operation).toContain("operationId: listMyStops");
    expect(operation).toContain("type: array");
    expect(operation).toContain("$ref: '#/components/schemas/DriverStop'");
  });

  it("models exactly the required frontend properties", () => {
    const schema = sliceBetween(openApi, "    DriverStop:", "    Proof:");
    const required = [
      "order_id",
      "aggregate_version",
      "order_public_id",
      "stop_type",
      "status",
      "address_summary",
    ];
    for (const property of required) {
      expect(schema).toContain(`- ${property}`);
      expect(schema).toContain(`${property}:`);
    }
    expect(schema).toContain("contact_token:");
    expect(schema).not.toContain("telephone:");
    expect(schema).not.toContain("cost_cents:");
  });

  it("keeps the exact stop-type vocabulary and the supported status subset", () => {
    const schema = sliceBetween(openApi, "    DriverStop:", "    Proof:");
    for (const type of driverStopTypes) expect(schema).toContain(`- ${type}`);
    expect(driverStopStatuses).toHaveLength(8);
  });
});

describe("DRV-002 existing OpenAPI alignment", () => {
  it.each([
    ["/driver/me/stops:", "listMyStops"],
    ["/orders/{orderId}/transitions:", "transitionOrder"],
    ["/orders/{orderId}/proof-upload-sessions:", "createProofUploadSession"],
    ["/orders/{orderId}/proofs:", "finalizeProof"],
  ])("uses the existing %s operation", (path, operationId) => {
    const start = openApi.indexOf(`  ${path}`);
    expect(start).toBeGreaterThanOrEqual(0);
    expect(openApi.slice(start, start + 1_500)).toContain(
      `operationId: ${operationId}`,
    );
  });

  it("keeps transition and proof request field names exact", () => {
    const transition = sliceBetween(
      openApi,
      "    TransitionRequest:",
      "    CreateAssignmentRequest:",
    );
    for (const field of [
      "target_status",
      "reason",
      "expected_version",
      "metadata",
    ]) {
      expect(transition).toContain(`${field}:`);
    }
    const finalization = sliceBetween(
      openApi,
      "    FinalizeProofRequest:",
      "    DriverLocationPoint:",
    );
    for (const field of [
      "upload_session_id",
      "proof_type",
      "captured_at",
      "sha256",
    ]) {
      expect(finalization).toContain(`${field}:`);
    }
  });
});

function sliceBetween(source: string, start: string, end: string): string {
  const startIndex = source.indexOf(start);
  const endIndex = source.indexOf(end, startIndex + start.length);
  expect(startIndex).toBeGreaterThanOrEqual(0);
  expect(endIndex).toBeGreaterThan(startIndex);
  return source.slice(startIndex, endIndex);
}
