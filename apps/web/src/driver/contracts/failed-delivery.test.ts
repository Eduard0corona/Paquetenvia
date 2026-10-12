import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import {
  DriverFailedDeliveryIncidentType,
  driverFailedDeliveryOtherReasons,
  driverFailedDeliveryPrimaryReasons,
  driverFailedDeliveryReasonAllowed,
  driverFailedDeliveryReasonLabel,
  driverFailedDeliveryReasons,
  driverFailedDeliveryReasonsFor,
  isDriverFailedDeliveryReason,
} from "./failed-delivery";

/**
 * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: the driver's closed list is the
 * AI-05 reason enum, with the labels of Incidents.Domain.IncidentDriverReportPolicy.
 */
const repository = (path: string) => readFileSync(resolve(process.cwd(), "../..", path), "utf8");
const openApi = repository("docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml");
const serverPolicy = repository("src/Modules/Incidents/Incidents.Domain/IncidentPolicy.cs");

function slice(source: string, start: string, end: string): string {
  const from = source.indexOf(start);
  const to = source.indexOf(end, from + start.length);
  expect(from).toBeGreaterThanOrEqual(0);
  expect(to).toBeGreaterThan(from);
  return source.slice(from, to);
}

describe("closed list of \"No se pudo entregar\"", () => {
  it("orders the four reasons of ¿Qué pasó? and the three of Otro… with the server's labels", () => {
    expect(driverFailedDeliveryPrimaryReasons).toEqual([
      "RECIPIENT_ABSENT",
      "ADDRESS_NOT_FOUND",
      "RECIPIENT_REFUSED",
      "PAYMENT_UNAVAILABLE",
    ]);
    expect(driverFailedDeliveryOtherReasons).toEqual(["ACCESS_RESTRICTED", "PACKAGE_DAMAGED", "SECURITY_RISK"]);
    const labels = Object.fromEntries(
      driverFailedDeliveryReasons.map((reason) => [reason, driverFailedDeliveryReasonLabel(reason)]),
    );
    expect(labels).toEqual({
      RECIPIENT_ABSENT: "No había nadie",
      ADDRESS_NOT_FOUND: "Dirección incorrecta",
      RECIPIENT_REFUSED: "Rechazó el paquete",
      PAYMENT_UNAVAILABLE: "No pagó el cobro",
      ACCESS_RESTRICTED: "Acceso restringido",
      PACKAGE_DAMAGED: "Paquete dañado",
      SECURITY_RISK: "Riesgo de seguridad",
    });
    const reasonLabel = slice(serverPolicy, "public static string ReasonLabel(", "public static string Description(");
    for (const [reason, label] of Object.entries(labels)) {
      const member = reason
        .toLowerCase()
        .split("_")
        .map((part) => part[0].toUpperCase() + part.slice(1))
        .join("");
      expect(reasonLabel).toContain(`IncidentReasonCode.${member} => "${label}"`);
    }
  });

  it("is exactly the AI-05 reason enum and the driver's report shape", () => {
    const request = slice(openApi, "    OpenIncidentRequest:", "    ResolveIncidentRequest:");
    const reasons = slice(request, "        reason_code:", "        next_action:");
    const enumerated = [...reasons.matchAll(/^\s+- ([A-Z_]+)$/gm)].map((match) => match[1]);
    expect([...enumerated].sort()).toEqual([...driverFailedDeliveryReasons].sort());
    const shape = slice(request, "      x-reporter-shape:", "      properties:");
    expect(shape).toContain("driver_omits:\n        - severity\n        - description\n        - next_action");
    expect(request).toContain("pattern: ^[A-Z_]+$");
    expect(DriverFailedDeliveryIncidentType).toMatch(/^[A-Z_]{1,64}$/);
    const incident = slice(openApi, "    Incident:", "    IncidentPage:");
    for (const member of [
      "id",
      "order_id",
      "status",
      "severity",
      "reason_code",
      "next_action",
      "custody_acquired",
      "occurred_at",
      "sla_due_at",
      "evidence_proof_ids",
    ]) {
      expect(incident).toContain(`      - ${member}\n`);
    }
    const operation = slice(openApi, "operationId: openIncident", "  /incidents:");
    expect(operation).toContain("$ref: '#/components/parameters/IdempotencyKey'");
    expect(operation).toContain("'201':");
  });

  it("offers \"No pagó el cobro\" only on a stop with cash on delivery", () => {
    expect(driverFailedDeliveryReasonsFor(true).primary).toContain("PAYMENT_UNAVAILABLE");
    expect(driverFailedDeliveryReasonsFor(false).primary).not.toContain("PAYMENT_UNAVAILABLE");
    expect(driverFailedDeliveryReasonsFor(false).primary).toHaveLength(3);
    expect(driverFailedDeliveryReasonsFor(false).other).toEqual(driverFailedDeliveryOtherReasons);
    expect(driverFailedDeliveryReasonAllowed("PAYMENT_UNAVAILABLE", false)).toBe(false);
    expect(driverFailedDeliveryReasonAllowed("PAYMENT_UNAVAILABLE", true)).toBe(true);
    expect(driverFailedDeliveryReasonAllowed("SECURITY_RISK", false)).toBe(true);
    expect(isDriverFailedDeliveryReason("RECIPIENT_ABSENT")).toBe(true);
    for (const value of ["OTHER", "recipient_absent", "", null, 3]) {
      expect(isDriverFailedDeliveryReason(value)).toBe(false);
    }
  });

  it("names the transition fields the report sends (metadata.incident_id on FAILED_ATTEMPT)", () => {
    const transition = slice(openApi, "    TransitionRequest:", "    CreateAssignmentRequest:");
    for (const field of ["target_status:", "reason:", "expected_version:", "metadata:", "client_occurred_at:"]) {
      expect(transition).toContain(field);
    }
    expect(transition).toContain("maxLength: 500");
  });
});
