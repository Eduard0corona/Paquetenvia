import { describe, expect, it } from "vitest";
import { codStillToCollect } from "../components/driver-stop-details";
import {
  DriverFailedAttemptConfirmedLabel,
  driverConfirmedStatusLabel,
  driverOperationLabel,
  driverStopStatusLabel,
} from "../contracts/labels";
import {
  driverCashGoneReportMessage,
  driverDispatchReportedFailedAttemptMessage,
  driverDispatchRoleReportMessage,
  driverFailedDeliveryAvailability,
  driverFailedReportAttentionMessage,
  driverFailedReportCashHold,
  driverFailedReportHoldState,
  driverFailedReportIncident,
  driverIncidentMaybeOpenReportMessage,
  driverIncidentOpenReportMessage,
  driverRoleLoadingReportMessage,
  driverRoleUnknownReportMessage,
  driverStaleReportMessage,
} from "./failed-delivery-policy";
import {
  cashStop,
  collectionOperation,
  deliveryStop,
  failedDeliveryOperation,
  orderId,
  partition,
} from "./failed-delivery.fixtures";
import {
  createDriverOfflineOperation,
  type DriverOfflineOperation,
} from "./operation-contract";
import {
  compareDriverOperationDependency,
  nextDriverOperationKind,
  orderDriverOperations,
  projectDriverOperations,
} from "./operation-projection";
import {
  driverCashRecordedReportMessage,
  driverCashSavedReportMessage,
  driverRejectionMessage,
  incidentRejectionCodeToKeep,
} from "./rejection-codes";
import type { DriverStop } from "../contracts/driver-stop";

/**
 * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: when the stop offers "No se
 * pudo entregar", how a queued report projects, and the cash rule (owner
 * answers "Solo si hay cobro" and "No, llama a despacho").
 */
function availability(stop: DriverStop, operations: readonly DriverOfflineOperation[] = []) {
  const projection = projectDriverOperations([stop], operations);
  return driverFailedDeliveryAvailability(projection.stops[0], projection.operations, "driver");
}

describe("projection of a failed-delivery report", () => {
  it("shows a queued report as pending: confirmed DELIVERING, projected FAILED_ATTEMPT", () => {
    const projection = projectDriverOperations([deliveryStop()], [failedDeliveryOperation()]);
    expect(projection.stops[0]).toMatchObject({
      confirmedStatus: "DELIVERING",
      confirmedVersion: 8,
      projectedStatus: "FAILED_ATTEMPT",
      projectedVersion: 9,
      pendingCount: 1,
      attentionCount: 0,
      failedReport: "queued",
    });
    expect(nextDriverOperationKind("FAILED_ATTEMPT")).toBeNull();
    expect(driverStopStatusLabel("FAILED_ATTEMPT")).toBe("Intento fallido");
  });

  it("reads a confirmed failed attempt as dispatch deciding", () => {
    const projection = projectDriverOperations(
      [deliveryStop({ status: "FAILED_ATTEMPT", aggregate_version: 9 })],
      [],
    );
    expect(projection.stops[0]).toMatchObject({
      confirmedStatus: "FAILED_ATTEMPT",
      projectedStatus: "FAILED_ATTEMPT",
      failedReport: "none",
    });
    expect(driverConfirmedStatusLabel("FAILED_ATTEMPT")).toBe("Intento fallido · El despacho decide");
    expect(DriverFailedAttemptConfirmedLabel).toBe("Intento fallido · El despacho decide");
    expect(driverConfirmedStatusLabel("DELIVERING")).toBe("En reparto");
    expect(driverOperationLabel("FAILED_DELIVERY")).toBe("No se pudo entregar");
  });

  it("holds a report built on another version for attention and never applies it", () => {
    const projection = projectDriverOperations(
      [deliveryStop({ aggregate_version: 9 })],
      [failedDeliveryOperation()],
    );
    expect(projection.stops[0]).toMatchObject({
      projectedStatus: "DELIVERING",
      attentionCount: 1,
      failedReport: "attention",
    });
  });

  it("puts the report first at the door so nothing of the order runs after it", () => {
    const report = failedDeliveryOperation();
    const collection = collectionOperation({
      createdAt: new Date(Date.parse(report.createdAt) + 1_000).toISOString(),
    });
    expect(orderDriverOperations([collection, report]).map((value) => value.kind)).toEqual([
      "FAILED_DELIVERY",
      "COD_COLLECTION",
    ]);
    expect(compareDriverOperationDependency(report, collection)).toBeLessThan(0);
    const projection = projectDriverOperations([cashStop(false)], [collection, report]);
    expect(projection.operations.map((value) => [value.kind, value.status])).toEqual([
      ["FAILED_DELIVERY", "PENDING"],
      ["COD_COLLECTION", "NEEDS_ATTENTION"],
    ]);
  });

  it("treats an accepted report as settled once REST moved past it, whatever dispatch decided (MAJOR 3)", () => {
    const accepted = failedDeliveryOperation({
      proofId: "00000000-0000-4000-8000-000000000600",
      status: "AWAITING_REST_CONFIRMATION",
      failedDelivery: {
        reasonCode: "RECIPIENT_ABSENT",
        incidentIdempotencyKey: `drv2-${failedDeliveryOperation().id}-incident`,
        incidentRequested: true,
        incidentId: "00000000-0000-4000-8000-000000000700",
      },
    });
    // "Reintentar hoy": DELIVERING again at version 10.
    const projection = projectDriverOperations([deliveryStop({ aggregate_version: 10 })], [accepted]);
    expect(projection.stops[0]).toMatchObject({
      projectedStatus: "DELIVERING",
      projectedVersion: 10,
      pendingCount: 0,
      attentionCount: 0,
      failedReport: "none",
    });
    expect(projection.operations).toEqual([]);
    expect(nextDriverOperationKind(projection.stops[0].projectedStatus)).toBe("DELIVERY_PROOF");
    // Before REST moved past it, it is still shown as the status to sync.
    const waiting = projectDriverOperations([deliveryStop()], [accepted]);
    expect(waiting.stops[0]).toMatchObject({ projectedStatus: "FAILED_ATTEMPT", failedReport: "queued" });
  });

  it("shows a pending report as held, not confirmed, when dispatch registered the failed attempt itself (BLOCKER 1)", () => {
    const report = failedDeliveryOperation();
    const stop = deliveryStop({ status: "FAILED_ATTEMPT", aggregate_version: 9 });
    const projection = projectDriverOperations([stop], [report]);
    expect(projection.operations).toEqual([
      expect.objectContaining({ id: report.id, status: "NEEDS_ATTENTION", safeError: "VERSION_CONFLICT" }),
    ]);
    expect(
      driverFailedReportAttentionMessage(projection.operations[0], projection.stops[0], projection.operations),
    ).toBe(driverDispatchReportedFailedAttemptMessage);
  });

  it("surfaces a blocked action with nothing held ahead of it, so it can always be discarded (MAJOR 5)", () => {
    const delivery = {
      ...failedDeliveryOperation(),
      kind: "DELIVERY_PROOF" as const,
      targetStatus: "DELIVERED" as const,
      reason: "DRIVER_DELIVERY_CONFIRMED",
      failedDelivery: null,
      status: "BLOCKED" as const,
    };
    const projection = projectDriverOperations(
      [deliveryStop({ status: "FAILED_ATTEMPT", aggregate_version: 9 })],
      [delivery],
    );
    expect(projection.operations).toEqual([
      expect.objectContaining({ id: delivery.id, status: "NEEDS_ATTENTION", safeError: "VERSION_CONFLICT" }),
    ]);
    expect(projection.stops[0].attentionCount).toBe(1);
  });

  it("no longer counts the cash of a stop whose delivery failed as pending", () => {
    const pending = projectDriverOperations([cashStop(false)], []).stops[0];
    expect(codStillToCollect(pending)).toBe(125_000);
    const reported = projectDriverOperations([cashStop(false)], [failedDeliveryOperation()]).stops[0];
    expect(codStillToCollect(reported)).toBeNull();
    const confirmed = projectDriverOperations(
      [cashStop(false, { status: "FAILED_ATTEMPT", aggregate_version: 9 })],
      [],
    ).stops[0];
    expect(codStillToCollect(confirmed)).toBeNull();
  });
});

describe("when the stop offers \"No se pudo entregar\"", () => {
  it("offers it at the door of a delivery, with \"No pagó el cobro\" only when the stop collects cash", () => {
    expect(availability(deliveryStop())).toEqual({ kind: "offered", paymentReason: false });
    expect(availability(cashStop(false))).toEqual({ kind: "offered", paymentReason: true });
  });

  it("offers it when the arrival is still waiting to sync (projected DELIVERING)", () => {
    const arrival = createDriverOfflineOperation({
      partitionKey: partition.key,
      orderId,
      kind: "START_DELIVERY",
      expectedVersion: 7,
      randomUuid: () => "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
    });
    const stop = deliveryStop({ status: "IN_TRANSIT", aggregate_version: 7 });
    expect(availability(stop)).toEqual({ kind: "hidden" });
    const projection = projectDriverOperations([stop], [arrival]);
    expect(projection.stops[0]).toMatchObject({ projectedStatus: "DELIVERING", projectedVersion: 8 });
    expect(driverFailedDeliveryAvailability(projection.stops[0], projection.operations, "driver")).toEqual({
      kind: "offered",
      paymentReason: false,
    });
  });

  it.each<[string, Partial<DriverStop>]>([
    ["a pickup", { stop_type: "PICKUP", status: "AT_PICKUP" }],
    ["a return", { stop_type: "RETURN", status: "RETURNING" }],
    ["before the door", { status: "IN_TRANSIT" }],
    ["a rescheduled order", { status: "RESCHEDULED" }],
  ])("hides it on %s", (_name, patch) => {
    expect(availability(deliveryStop(patch))).toEqual({ kind: "hidden" });
  });

  it("hides it while a delivery is saved on the phone", () => {
    const projection = projectDriverOperations([deliveryStop()], [
      {
        ...failedDeliveryOperation(),
        kind: "DELIVERY_PROOF",
        targetStatus: "DELIVERED",
        reason: "DRIVER_DELIVERY_CONFIRMED",
        failedDelivery: null,
      },
    ]);
    expect(projection.stops[0].projectedStatus).toBe("DELIVERED");
    expect(
      driverFailedDeliveryAvailability(projection.stops[0], projection.operations, "driver"),
    ).toEqual({ kind: "hidden" });
  });

  it("shows a queued report, a held one, and the dispatcher's decision once confirmed", () => {
    expect(availability(deliveryStop(), [failedDeliveryOperation()])).toEqual({ kind: "queued" });
    expect(
      availability(deliveryStop(), [
        failedDeliveryOperation({ status: "NEEDS_ATTENTION", safeError: "VERSION_CONFLICT" }),
      ]),
    ).toEqual({ kind: "attention", discardable: null });
    expect(
      availability(deliveryStop({ status: "FAILED_ATTEMPT", aggregate_version: 9 })),
    ).toEqual({ kind: "dispatch-decides" });
    // REST already confirmed while the queue still waits to delete the report.
    expect(
      availability(deliveryStop({ status: "FAILED_ATTEMPT", aggregate_version: 9 }), [
        failedDeliveryOperation({ status: "AWAITING_REST_CONFIRMATION" }),
      ]),
    ).toEqual({ kind: "dispatch-decides" });
  });

  it("with a collection saved on the phone, asks to discard it first instead of offering the report", () => {
    const saved = collectionOperation();
    expect(availability(cashStop(false), [saved])).toEqual({ kind: "cod-saved", discardable: saved });
    const retrying = collectionOperation({ status: "RETRY_WAIT" });
    expect(availability(cashStop(false), [retrying])).toEqual({
      kind: "cod-saved",
      discardable: retrying,
    });
    // A held collection is discarded from the stop's attention notice.
    expect(
      availability(cashStop(false), [collectionOperation({ status: "NEEDS_ATTENTION" })]),
    ).toEqual({ kind: "cod-saved", discardable: null });
  });

  it("with the cash recorded on the server, asks to call dispatch", () => {
    expect(availability(cashStop(true))).toEqual({ kind: "cod-recorded" });
    expect(
      availability(cashStop(false), [collectionOperation({ status: "AWAITING_REST_CONFIRMATION" })]),
    ).toEqual({ kind: "cod-recorded" });
  });

  it("lets the driver discard the collection that holds a report", () => {
    const collection = collectionOperation({ status: "BLOCKED" });
    const held = failedDeliveryOperation({ status: "NEEDS_ATTENTION", safeError: "COD_COLLECTED" });
    const result = availability(cashStop(false), [held, collection]);
    expect(result).toMatchObject({ kind: "attention", discardable: { id: collection.id } });
  });

  it("hides it while another action of the stop needs attention", () => {
    const projection = projectDriverOperations([deliveryStop()], []);
    const stop = { ...projection.stops[0], attentionCount: 1 };
    expect(driverFailedDeliveryAvailability(stop, [], "driver")).toEqual({ kind: "hidden" });
  });

  it.each([
    ["dispatch", "dispatch-role"],
    ["unknown", "role-unknown"],
  ] as const)(
    "never offers the report to a %s report role: it says why instead",
    (reportRole, kind) => {
      const at = (stop: DriverStop, operations: readonly DriverOfflineOperation[] = []) => {
        const projection = projectDriverOperations([stop], operations);
        return driverFailedDeliveryAvailability(projection.stops[0], projection.operations, reportRole);
      };
      expect(at(deliveryStop())).toEqual({ kind });
      expect(at(cashStop(false))).toEqual({ kind });
      // Everything that is not the offer itself reads the same as for a driver.
      expect(at(cashStop(true))).toEqual({ kind: "cod-recorded" });
      expect(at(deliveryStop(), [failedDeliveryOperation()])).toEqual({ kind: "queued" });
      expect(at(deliveryStop({ stop_type: "PICKUP", status: "AT_PICKUP" }))).toEqual({ kind: "hidden" });
    },
  );

  it("offers nothing and says nothing about the role while the roles are loading", () => {
    const at = (stop: DriverStop, operations: readonly DriverOfflineOperation[] = []) => {
      const projection = projectDriverOperations([stop], operations);
      return driverFailedDeliveryAvailability(projection.stops[0], projection.operations, "loading");
    };
    expect(at(deliveryStop())).toEqual({ kind: "hidden" });
    expect(at(cashStop(false))).toEqual({ kind: "hidden" });
    // Everything that is not the offer itself reads the same as for a driver.
    expect(at(cashStop(true))).toEqual({ kind: "cod-recorded" });
    expect(at(deliveryStop(), [failedDeliveryOperation()])).toEqual({ kind: "queued" });
  });

  it("says why: register the attempt from Despacho (owner answer \"Registrar desde Despacho\"), or open the app again with a connection", () => {
    expect(driverDispatchRoleReportMessage).toBe(
      "También eres despacho en esta organización, así que el intento fallido no se reporta desde el teléfono: " +
        "regístralo desde la orden en Despacho, donde eliges el motivo y el siguiente paso.",
    );
    expect(driverRoleUnknownReportMessage).toBe(
      "No pudimos confirmar tu rol. Vuelve a abrir la app con conexión para reportar.",
    );
    // Signing out would wipe what the phone keeps offline, so no role notice ever asks for it.
    for (const message of [driverRoleUnknownReportMessage, driverRoleLoadingReportMessage]) {
      expect(message).not.toMatch(/sesi[oó]n/i);
    }
    expect(driverRoleLoadingReportMessage).toBe("Estamos confirmando tu rol. Intenta de nuevo en un momento.");
  });
});

describe("the cash that holds a report", () => {
  it("is recorded on the server, saved on the phone, or absent", () => {
    const report = failedDeliveryOperation();
    expect(driverFailedReportCashHold(report, cashStop(true), [report])).toBe("recorded");
    expect(driverFailedReportCashHold(report, cashStop(false), [report, collectionOperation()])).toBe(
      "saved",
    );
    expect(
      driverFailedReportCashHold(report, cashStop(false), [
        report,
        collectionOperation({ status: "AWAITING_REST_CONFIRMATION" }),
      ]),
    ).toBe("recorded");
    expect(driverFailedReportCashHold(report, cashStop(false), [report])).toBeNull();
    expect(driverFailedReportCashHold(report, deliveryStop(), [report])).toBeNull();
    expect(driverFailedReportCashHold(report, null, [report])).toBeNull();
    // Another order's collection never holds this report.
    expect(
      driverFailedReportCashHold(report, cashStop(false), [
        report,
        { ...collectionOperation(), orderId: "33333333-3333-4333-8333-333333333333" },
      ]),
    ).toBeNull();
    expect(driverFailedReportCashHold(collectionOperation(), cashStop(true), [])).toBeNull();
  });

  it("explains each case in Spanish without a phone number", () => {
    const held = failedDeliveryOperation({ status: "NEEDS_ATTENTION", safeError: "COD_COLLECTED" });
    const stop = projectDriverOperations([cashStop(true)], []).stops[0];
    expect(driverFailedReportHoldState(stop, [])).toBe("recorded");
    expect(driverFailedReportAttentionMessage(held, stop, [held])).toBe(driverCashRecordedReportMessage);
    expect(driverCashRecordedReportMessage).toBe(
      "El cobro de esta entrega ya está registrado, así que el reporte no se envió. " +
        "Llama a despacho: ellos deciden y coordinan la devolución del cobro.",
    );
    const saved = projectDriverOperations([cashStop(false)], []).stops[0];
    expect(driverFailedReportAttentionMessage(held, saved, [held, collectionOperation()])).toBe(
      driverCashSavedReportMessage,
    );
    expect(driverFailedReportAttentionMessage(held, saved, [held])).toBe(driverCashGoneReportMessage);
    for (const message of [
      driverCashRecordedReportMessage,
      driverCashSavedReportMessage,
      driverCashGoneReportMessage,
    ]) {
      expect(message).not.toMatch(/\d{3}/);
      expect(message).not.toContain("tel:");
    }
    expect(orderId).toBeTruthy();
  });
});

describe("a held report whose incident is or may be open (MINOR 8)", () => {
  const requested = (incidentId: string | null, patch: Partial<DriverOfflineOperation> = {}) =>
    failedDeliveryOperation({
      proofId: "00000000-0000-4000-8000-000000000600",
      status: "NEEDS_ATTENTION",
      failedDelivery: {
        reasonCode: "RECIPIENT_ABSENT",
        incidentIdempotencyKey: `drv2-${failedDeliveryOperation().id}-incident`,
        incidentRequested: true,
        incidentId,
      },
      ...patch,
    });
  const open = requested("00000000-0000-4000-8000-000000000700");
  const maybe = requested(null);

  it("tells whether the incident exists", () => {
    expect(driverFailedReportIncident(failedDeliveryOperation())).toBe("none");
    expect(driverFailedReportIncident(open)).toBe("open");
    expect(driverFailedReportIncident(maybe)).toBe("maybe");
    // A rule code of openIncident means the server refused it: nothing was opened.
    expect(driverFailedReportIncident({ ...maybe, rejectionCode: "INVALID_REQUEST" })).toBe("none");
    expect(driverFailedReportIncident(collectionOperation())).toBe("none");
  });

  it("never says \"not sent\" nor invites reporting it again", () => {
    const stop = projectDriverOperations([deliveryStop({ aggregate_version: 10 })], []).stops[0];
    const cases = [
      driverFailedReportAttentionMessage(
        { ...open, safeError: "VERSION_CONFLICT", rejectionCode: "DISPATCH_DECISION_REQUIRED" },
        stop,
        [],
      ),
      driverFailedReportAttentionMessage({ ...open, safeError: "VERSION_CONFLICT" }, stop, []),
      driverFailedReportAttentionMessage({ ...maybe, safeError: "VERSION_CONFLICT" }, stop, []),
      driverFailedReportAttentionMessage(
        { ...open, safeError: "COD_COLLECTED" },
        projectDriverOperations([cashStop(true)], []).stops[0],
        [],
      ),
      driverFailedReportAttentionMessage(
        { ...maybe, safeError: "COD_COLLECTED" },
        projectDriverOperations([cashStop(false)], []).stops[0],
        [collectionOperation()],
      ),
    ];
    expect(cases[0]).toBe(driverIncidentOpenReportMessage);
    expect(cases[1]).toBe(driverIncidentOpenReportMessage);
    expect(cases[2]).toBe(driverIncidentMaybeOpenReportMessage);
    expect(cases[3]).toContain("Llama a despacho");
    expect(cases[4]).toContain("descártalo y el reporte terminará de enviarse");
    for (const message of cases) {
      expect(message).toMatch(/abierto como incidencia/);
      expect(message).not.toContain("el reporte no se envió");
      expect(message).not.toMatch(/vuelve a reportar|vuelve a registrar/i);
    }
  });

  it("says a report built on an attempt that moved on was not sent, and asks for a new photo", () => {
    const stale = failedDeliveryOperation({ status: "NEEDS_ATTENTION", safeError: "VERSION_CONFLICT" });
    const moved = projectDriverOperations([deliveryStop({ aggregate_version: 10 })], []).stops[0];
    expect(driverFailedReportAttentionMessage(stale, moved, [])).toBe(driverStaleReportMessage);
    expect(driverStaleReportMessage).toContain("foto nueva");
  });
});

describe("refused reports in Spanish", () => {
  it.each([
    ["INVALID_REQUEST", "registra el intento desde la orden en Despacho"],
    ["IDEMPOTENCY_CONFLICT", "ya se había enviado con otros datos"],
    ["ORDER_STATE_NOT_ALLOWED", "La orden ya no está en entrega"],
    ["EVIDENCE_NOT_AVAILABLE", "La foto del reporte no quedó disponible"],
    ["INCIDENT_STATE_CONFLICT", "otra incidencia de esta orden"],
    ["CONFLICT", "El reporte chocó con otra operación"],
  ] as const)("names %s for a report", (code, text) => {
    expect(incidentRejectionCodeToKeep(code)).toBe(code);
    expect(driverRejectionMessage("FAILED_DELIVERY", code)).toContain(text);
  });

  it("keeps the transition rules for a report and never keeps unknown or expiry codes", () => {
    expect(driverRejectionMessage("FAILED_DELIVERY", "DISPATCH_DECISION_REQUIRED")).toBe(
      "El siguiente paso de este intento fallido lo decide el despacho.",
    );
    expect(driverRejectionMessage("FAILED_DELIVERY", "VERSION_CONFLICT")).toContain("La orden cambió");
    expect(incidentRejectionCodeToKeep("OFFLINE_OPERATION_EXPIRED")).toBeNull();
    expect(incidentRejectionCodeToKeep("SOMETHING")).toBeNull();
    expect(incidentRejectionCodeToKeep(null)).toBeNull();
    // A cash collection keeps its own messages for the codes both share.
    expect(driverRejectionMessage("COD_COLLECTION", "INVALID_REQUEST")).toContain("cobro");
  });
});
