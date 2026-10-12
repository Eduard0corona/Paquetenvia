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
  it("uses listMyStops with the DriverStopList answer and the details switch", () => {
    const operation = sliceBetween(openApi, "  /driver/me/stops:", "  /orders/");
    expect(operation).toContain("operationId: listMyStops");
    expect(operation).toContain("$ref: '#/components/schemas/DriverStopList'");
    expect(operation).toContain("- name: details");
    expect(operation).toContain("type: boolean");
    expect(operation).toContain("Cache-Control");
    const list = sliceBetween(openApi, "    DriverStopList:", "    DriverStop:");
    expect(list).toContain("additionalProperties: false");
    for (const property of ["stops", "completed_today"]) {
      expect(list).toContain(`- ${property}`);
      expect(list).toContain(`${property}:`);
    }
    expect(list).toContain("type: array");
    expect(list).toContain("maxItems: 500");
    expect(list).toContain("$ref: '#/components/schemas/DriverStop'");
    expect(list).toContain("America/Mazatlan");
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
      // UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11
      "details_available",
      "contact_short_name",
      "contact_full_name",
      "zone_name",
      "service_window",
      "cod",
      "navigation",
    ];
    for (const property of required) {
      expect(schema).toContain(`- ${property}`);
      expect(schema).toContain(`${property}:`);
    }
    for (const property of ["latitude", "longitude", "address_text", "references", "expected", "recorded"]) {
      expect(schema).toContain(`- ${property}`);
    }
    expect(schema).toContain("maxLength: 64");
    expect(schema).toContain("maxLength: 200");
    expect(schema).toContain("maxLength: 1000");
    expect(schema).toContain("maxLength: 500");
    expect(schema).toContain("$ref: '#/components/schemas/Money'");
    expect(schema).toContain("contact_token:");
    expect(schema).not.toContain("telephone:");
    expect(schema).not.toContain("phone:");
    expect(schema).not.toContain("cost_cents:");
    expect(schema).not.toContain("contact_name:");
  });

  it("documents the driver's cash collection on recordCodCollection", () => {
    const operation = sliceBetween(openApi, "  /orders/{orderId}/cod-records:", "  /cod-records/{codId}/reconcile:");
    expect(operation).toContain("operationId: recordCodCollection");
    expect(operation).toContain("UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11");
    expect(operation).toContain("$ref: '#/components/schemas/RecordCodRequest'");
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
