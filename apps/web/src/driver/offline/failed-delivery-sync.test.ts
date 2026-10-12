import { describe, expect, it, vi } from "vitest";
import { DriverSyncApiError } from "./driver-sync-api";
import { selectRunnableOperation } from "./driver-sync-scheduler";
import {
  FailedDeliveryServer,
  MemoryQueue,
  amountCents,
  collectionOperation,
  failedDeliveryOperation,
  orderId,
  photoRecord,
  reportId,
  reportedAt,
  uuid,
  type ReportStep,
} from "./failed-delivery.fixtures";
import {
  driverDispatchReportedFailedAttemptMessage,
  driverFailedReportAttentionMessage,
  driverIncidentMaybeOpenReportMessage,
  driverIncidentOpenReportMessage,
  driverStaleReportMessage,
} from "./failed-delivery-policy";
import type { DriverOfflineOperation } from "./operation-contract";
import type { ProjectedDriverStop } from "./operation-projection";
import { driverRejectionMessage } from "./rejection-codes";

/** The stop as the attention notice reads it. */
function attentionStop(
  patch: Partial<Pick<ProjectedDriverStop, "cod" | "confirmedStatus" | "confirmedVersion">> = {},
) {
  return {
    order_id: orderId,
    cod: null,
    confirmedStatus: "DELIVERING" as const,
    confirmedVersion: 8,
    ...patch,
  };
}

/**
 * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: the queue sends "No se pudo
 * entregar" as the AI-05 sequence - DELIVERY_PHOTO session, PUT, proof,
 * incident, FAILED_ATTEMPT - each step with its own Idempotency-Key, resumes
 * at the right step after a lost answer or a closed app, and never sends a
 * report while the order's cash is saved on the phone or recorded.
 */
const steps: readonly ReportStep[] = ["session", "upload", "finalize", "incident", "transition"];

function queuedReport(patch: Partial<DriverOfflineOperation> = {}): MemoryQueue {
  const queue = new MemoryQueue([failedDeliveryOperation(patch)]);
  queue.proofs.set(reportId, photoRecord());
  return queue;
}

describe("failed-delivery sync", () => {
  it("runs the five steps once, in order, with the report's keys, and leaves when REST confirms", async () => {
    const queue = queuedReport();
    const server = new FailedDeliveryServer();
    const api = server.api();
    const scheduler = server.scheduler(queue, { api });

    await scheduler.requestSync(true);

    expect(server.calls).toEqual(steps);
    expect(server.keys.session).toEqual([`drv2-${reportId}-session-1`]);
    expect(server.keys.finalize).toEqual([`drv2-${reportId}-finalize-1`]);
    expect(server.keys.incident).toEqual([`drv2-${reportId}-incident`]);
    expect(server.keys.transition).toEqual([`drv2-${reportId}-transition`]);
    const reported = vi.mocked(api.openFailedDeliveryIncident).mock.calls[0][0];
    expect(reported.proofId).toBe(server.proofs.get(`drv2-${reportId}-finalize-1`));
    const transitioned = vi.mocked(api.transitionOrder).mock.calls[0][0];
    expect(transitioned.failedDelivery?.incidentId).toBe(
      server.incidents.get(`drv2-${reportId}-incident`),
    );
    expect(transitioned.expectedVersion).toBe(8);
    expect(server.status).toBe("FAILED_ATTEMPT");
    expect(queue.operations).toEqual([]);
    expect(queue.proofs.size).toBe(0);
    await scheduler.dispose();
  });

  it.each(steps)(
    "resumes after a lost answer at %s with the same keys and one of everything",
    async (step) => {
      const queue = queuedReport();
      const server = new FailedDeliveryServer();
      server.failures = [{ step, when: "after", error: new DriverSyncApiError("recoverable") }];
      const scheduler = server.scheduler(queue);

      await scheduler.requestSync(true);
      expect(queue.only()).toMatchObject({ status: "RETRY_WAIT", safeError: "NETWORK" });
      await scheduler.requestSync(true);

      expect(queue.operations).toEqual([]);
      expect(server.sessions.size).toBe(1);
      expect(server.proofs.size).toBe(1);
      expect(server.incidents.size).toBe(1);
      expect(server.transitionsApplied).toBe(1);
      for (const name of steps) {
        expect(new Set(server.keys[name]).size).toBeLessThanOrEqual(1);
      }
      // A transition the server applied is confirmed by REST, never sent twice.
      expect(server.keys.transition).toHaveLength(1);
      await scheduler.dispose();
    },
  );

  it.each(steps)("waits and retries a network failure before %s", async (step) => {
    const queue = queuedReport();
    const server = new FailedDeliveryServer();
    server.failures = [{ step, when: "before", error: new DriverSyncApiError("recoverable") }];
    const scheduler = server.scheduler(queue);

    await scheduler.requestSync(true);
    expect(queue.only()).toMatchObject({ status: "RETRY_WAIT", attemptCount: 1 });
    await scheduler.requestSync(true);

    expect(queue.operations).toEqual([]);
    expect(server.incidents.size).toBe(1);
    expect(server.transitionsApplied).toBe(1);
    await scheduler.dispose();
  });

  it.each<[string, Partial<DriverOfflineOperation>, (server: FailedDeliveryServer) => void, readonly ReportStep[]]>([
    ["nothing was sent", {}, () => undefined, steps],
    [
      "the upload session was opened",
      { uploadSessionId: uuid(0x500), uploadSessionExpiresAt: "2026-10-13T00:00:00.000Z" },
      (server) => server.sessions.set(`drv2-${reportId}-session-1`, uuid(0x500)),
      ["session", "upload", "finalize", "incident", "transition"],
    ],
    [
      "the photo was uploaded",
      { uploadSessionId: uuid(0x500), uploadSessionExpiresAt: "2026-10-13T00:00:00.000Z", uploadAccepted: true },
      (server) => server.sessions.set(`drv2-${reportId}-session-1`, uuid(0x500)),
      ["finalize", "incident", "transition"],
    ],
    [
      "the proof was finalized",
      { uploadSessionId: uuid(0x500), uploadAccepted: true, proofId: uuid(0x600) },
      () => undefined,
      ["incident", "transition"],
    ],
    [
      "the incident was requested but its answer never came back",
      {
        uploadSessionId: uuid(0x500),
        uploadAccepted: true,
        proofId: uuid(0x600),
        failedDelivery: {
          reasonCode: "RECIPIENT_ABSENT",
          incidentIdempotencyKey: `drv2-${reportId}-incident`,
          incidentRequested: true,
          incidentId: null,
        },
      },
      (server) => server.incidents.set(`drv2-${reportId}-incident`, uuid(0x700)),
      ["incident", "transition"],
    ],
    [
      "the incident was opened",
      {
        uploadSessionId: uuid(0x500),
        uploadAccepted: true,
        proofId: uuid(0x600),
        failedDelivery: {
          reasonCode: "RECIPIENT_ABSENT",
          incidentIdempotencyKey: `drv2-${reportId}-incident`,
          incidentRequested: true,
          incidentId: uuid(0x700),
        },
      },
      (server) => server.incidents.set(`drv2-${reportId}-incident`, uuid(0x700)),
      ["transition"],
    ],
  ])("after the app closed once %s, a new tab resumes at the next step", async (_name, progress, seed, expected) => {
    const queue = new MemoryQueue([failedDeliveryOperation({ ...progress, status: "PROCESSING" })]);
    if (progress.proofId === undefined) queue.proofs.set(reportId, photoRecord());
    const server = new FailedDeliveryServer();
    seed(server);
    const scheduler = server.scheduler(queue);

    await scheduler.requestSync(true);

    expect(server.calls).toEqual(expected);
    expect(server.incidents.size).toBe(1);
    expect(server.transitionsApplied).toBe(1);
    expect(queue.operations).toEqual([]);
    await scheduler.dispose();
  });

  it("holds the report with the server's rule when the incident is refused (409 INVALID_REQUEST)", async () => {
    const queue = queuedReport();
    const server = new FailedDeliveryServer();
    server.failures = [
      { step: "incident", when: "before", error: new DriverSyncApiError("conflict", "INVALID_REQUEST") },
    ];
    const scheduler = server.scheduler(queue);

    await scheduler.requestSync(true);

    const held = queue.only();
    expect(held).toMatchObject({
      status: "NEEDS_ATTENTION",
      safeError: "VERSION_CONFLICT",
      rejectionCode: "INVALID_REQUEST",
      proofId: server.proofs.get(`drv2-${reportId}-finalize-1`),
      failedDelivery: { incidentRequested: true, incidentId: null },
    });
    expect(server.calls).not.toContain("transition");
    // What the server answers to a person it treats as dispatch: never the
    // phone's clock (owner answer "Registrar desde Despacho").
    const message = driverRejectionMessage(held.kind, held.rejectionCode);
    expect(message).toContain("registra el intento desde la orden en Despacho");
    expect(message).not.toMatch(/fecha|hora|reloj/);
    expect(driverFailedReportAttentionMessage(held, attentionStop(), [held])).toBe(message);
    await scheduler.dispose();
  });

  it("keeps EVIDENCE_NOT_AVAILABLE as a rejected photo", async () => {
    const queue = queuedReport();
    const server = new FailedDeliveryServer();
    server.failures = [
      { step: "incident", when: "before", error: new DriverSyncApiError("conflict", "EVIDENCE_NOT_AVAILABLE") },
    ];
    const scheduler = server.scheduler(queue);

    await scheduler.requestSync(true);

    expect(queue.only()).toMatchObject({
      status: "NEEDS_ATTENTION",
      safeError: "EVIDENCE_REJECTED",
      rejectionCode: "EVIDENCE_NOT_AVAILABLE",
    });
    await scheduler.dispose();
  });

  it("shows the dispatcher rule when the transition answers DISPATCH_DECISION_REQUIRED", async () => {
    const queue = queuedReport();
    const server = new FailedDeliveryServer();
    server.failures = [
      {
        step: "transition",
        when: "before",
        error: new DriverSyncApiError("conflict", "DISPATCH_DECISION_REQUIRED"),
      },
    ];
    const scheduler = server.scheduler(queue);

    await scheduler.requestSync(true);

    const held = queue.only();
    expect(held).toMatchObject({
      status: "NEEDS_ATTENTION",
      rejectionCode: "DISPATCH_DECISION_REQUIRED",
      failedDelivery: { incidentId: server.incidents.get(`drv2-${reportId}-incident`) },
    });
    expect(driverRejectionMessage(held.kind, held.rejectionCode)).toBe(
      "El siguiente paso de este intento fallido lo decide el despacho.",
    );
    await scheduler.dispose();
  });

  it("holds the report on a version conflict after its incident opened, and says the incident is open", async () => {
    const queue = queuedReport();
    const server = new FailedDeliveryServer();
    server.failures = [
      { step: "transition", when: "before", error: new DriverSyncApiError("conflict", "VERSION_CONFLICT") },
    ];
    const scheduler = server.scheduler(queue);

    await scheduler.requestSync(true);

    const held = queue.only();
    expect(held).toMatchObject({
      status: "NEEDS_ATTENTION",
      safeError: "VERSION_CONFLICT",
      rejectionCode: "VERSION_CONFLICT",
      failedDelivery: { incidentId: uuid(0x700) },
    });
    expect(server.incidents.size).toBe(1);
    // MINOR 8: never "not sent" and never an invitation to report it again.
    expect(driverFailedReportAttentionMessage(held, attentionStop(), [held])).toBe(
      driverIncidentOpenReportMessage,
    );
    await scheduler.dispose();
  });

  it("asks for a new upload session when the grant expired, keeping the photo and the report keys", async () => {
    const queue = queuedReport();
    const server = new FailedDeliveryServer();
    server.grantExpiresAt = "2026-10-12T18:00:00.000Z";
    const scheduler = server.scheduler(queue);

    await scheduler.requestSync(true);

    expect(queue.only()).toMatchObject({
      status: "NEEDS_ATTENTION",
      safeError: "SESSION_EXPIRED",
      failedDelivery: { incidentIdempotencyKey: `drv2-${reportId}-incident`, incidentId: null },
    });
    expect(queue.proofs.has(reportId)).toBe(true);
    expect(server.calls).toEqual(["session"]);
    await scheduler.dispose();
  });

  it.each<[ReportStep, "unauthorized" | "forbidden"]>([
    ["session", "unauthorized"],
    ["incident", "unauthorized"],
    ["incident", "forbidden"],
    ["transition", "forbidden"],
  ])("clears the partition when %s answers %s", async (step, category) => {
    const queue = queuedReport();
    const server = new FailedDeliveryServer();
    server.failures = [{ step, when: "before", error: new DriverSyncApiError(category) }];
    const onAccessRevoked = vi.fn();
    const scheduler = server.scheduler(queue, { onAccessRevoked });

    await scheduler.requestSync(true);

    expect(queue.clearCount).toBe(1);
    expect(queue.operations).toEqual([]);
    expect(queue.proofs.size).toBe(0);
    expect(onAccessRevoked).toHaveBeenCalledWith(category);
    await scheduler.dispose();
  });

  it("drops an expired report once and tells the driver", async () => {
    const queue = queuedReport();
    const server = new FailedDeliveryServer();
    server.failures = [
      { step: "incident", when: "before", error: new DriverSyncApiError("conflict", "OFFLINE_OPERATION_EXPIRED") },
    ];
    const onOperationExpired = vi.fn();
    const scheduler = server.scheduler(queue, { onOperationExpired });

    await scheduler.requestSync(true);

    expect(queue.operations).toEqual([]);
    expect(onOperationExpired).toHaveBeenCalledOnce();
    expect(onOperationExpired.mock.calls[0][0]).toMatchObject({ kind: "FAILED_DELIVERY" });
    expect(server.calls).not.toContain("transition");
    await scheduler.dispose();
  });

  it("refuses an incident answer that is not this report", async () => {
    const queue = queuedReport();
    const server = new FailedDeliveryServer();
    const api = server.api();
    vi.mocked(api.openFailedDeliveryIncident).mockResolvedValue({
      id: uuid(0x701),
      orderId: queue.only().orderId,
      reasonCode: "RECIPIENT_ABSENT",
      nextAction: "RESCHEDULED",
      occurredAt: reportedAt.toISOString(),
      evidenceProofIds: [uuid(0x600)],
    });
    const scheduler = server.scheduler(queue, { api });

    await scheduler.requestSync(true);

    expect(queue.only()).toMatchObject({ status: "NEEDS_ATTENTION", failedDelivery: { incidentId: null } });
    expect(api.transitionOrder).not.toHaveBeenCalled();
    await scheduler.dispose();
  });
});

describe("a report and the attempt it describes", () => {
  const withIncident = (patch: Partial<DriverOfflineOperation> = {}) =>
    failedDeliveryOperation({
      uploadSessionId: uuid(0x500),
      uploadAccepted: true,
      proofId: uuid(0x600),
      failedDelivery: {
        reasonCode: "RECIPIENT_ABSENT",
        incidentIdempotencyKey: `drv2-${reportId}-incident`,
        incidentRequested: true,
        incidentId: uuid(0x700),
      },
      ...patch,
    });

  it("never takes dispatch's own failed attempt as this report: it is held, unsent, with its photo (BLOCKER 1)", async () => {
    const queue = queuedReport();
    const server = new FailedDeliveryServer();
    server.dispatchMoves("FAILED_ATTEMPT");
    const scheduler = server.scheduler(queue);

    await scheduler.requestSync(true);

    expect(server.calls).toEqual([]);
    const held = queue.only();
    expect(held).toMatchObject({
      status: "NEEDS_ATTENTION",
      safeError: "VERSION_CONFLICT",
      rejectionCode: null,
      failedDelivery: { incidentRequested: false, incidentId: null },
    });
    expect(queue.proofs.has(reportId)).toBe(true);
    expect(
      driverFailedReportAttentionMessage(
        held,
        attentionStop({ confirmedStatus: "FAILED_ATTEMPT", confirmedVersion: 9 }),
        [held],
      ),
    ).toBe(driverDispatchReportedFailedAttemptMessage);
    await scheduler.dispose();
  });

  it("is confirmed by a failed attempt once its own incident exists", async () => {
    const queue = new MemoryQueue([withIncident({ status: "RETRY_WAIT" })]);
    const server = new FailedDeliveryServer();
    server.dispatchMoves("FAILED_ATTEMPT");
    const scheduler = server.scheduler(queue);

    await scheduler.requestSync(true);

    expect(server.calls).toEqual([]);
    expect(queue.operations).toEqual([]);
    await scheduler.dispose();
  });

  it("sends nothing for an attempt dispatch already moved on: no photo, no incident (MAJOR 4)", async () => {
    const queue = queuedReport();
    const server = new FailedDeliveryServer();
    server.dispatchMoves("FAILED_ATTEMPT");
    server.dispatchMoves("DELIVERING"); // "Reintentar hoy": a new attempt at version 10.
    const scheduler = server.scheduler(queue);

    await scheduler.requestSync(true);

    expect(server.calls).toEqual([]);
    const held = queue.only();
    expect(held).toMatchObject({ status: "NEEDS_ATTENTION", safeError: "VERSION_CONFLICT" });
    expect(
      driverFailedReportAttentionMessage(held, attentionStop({ confirmedVersion: 10 }), [held]),
    ).toBe(driverStaleReportMessage);
    await scheduler.dispose();
  });

  it("reads the stop again after the photo and opens no incident once the attempt moved on", async () => {
    const queue = queuedReport();
    const server = new FailedDeliveryServer();
    server.onStopsRead = () => {
      if (server.stopsReads === 2) {
        server.dispatchMoves("FAILED_ATTEMPT");
        server.dispatchMoves("DELIVERING");
      }
    };
    const scheduler = server.scheduler(queue);

    await scheduler.requestSync(true);

    expect(server.calls).toEqual(["session", "upload", "finalize"]);
    expect(queue.only()).toMatchObject({
      status: "NEEDS_ATTENTION",
      safeError: "VERSION_CONFLICT",
      failedDelivery: { incidentRequested: false, incidentId: null },
    });
    await scheduler.dispose();
  });

  it("confirms an applied report whose answer was lost, even after dispatch moved the order on (MAJOR 3)", async () => {
    const queue = queuedReport();
    const server = new FailedDeliveryServer();
    server.failures = [
      { step: "transition", when: "after", error: new DriverSyncApiError("recoverable") },
    ];
    const scheduler = server.scheduler(queue);

    await scheduler.requestSync(true);
    expect(queue.only()).toMatchObject({ status: "RETRY_WAIT" });
    server.dispatchMoves("DELIVERING"); // "Reintentar hoy" before the phone read REST.
    await scheduler.requestSync(true);

    // The replayed key returns the stored answer; any later version confirms it.
    const key = `drv2-${reportId}-transition`;
    expect(server.keys.transition).toEqual([key, key]);
    expect(server.transitionsApplied).toBe(1);
    expect(server.incidents.size).toBe(1);
    expect(queue.operations).toEqual([]);
    await scheduler.dispose();
  });

  it.each(["DELIVERING", "RETURNING", "RESCHEDULED"] as const)(
    "confirms an accepted report by any later version (dispatch then chose %s)",
    async (decision) => {
      const queue = new MemoryQueue([withIncident({ status: "AWAITING_REST_CONFIRMATION" })]);
      const server = new FailedDeliveryServer();
      server.dispatchMoves("FAILED_ATTEMPT");
      server.dispatchMoves(decision);
      const scheduler = server.scheduler(queue);

      await scheduler.requestSync(true);

      expect(server.calls).toEqual([]);
      expect(queue.operations).toEqual([]);
      await scheduler.dispose();
    },
  );

  it("marks the incident requested before sending it, and a report held after a lost answer says it may be open", async () => {
    const queue = queuedReport();
    const server = new FailedDeliveryServer();
    server.failures = [
      { step: "incident", when: "after", error: new DriverSyncApiError("recoverable") },
    ];
    const api = server.api();
    const open = vi.mocked(api.openFailedDeliveryIncident).getMockImplementation()!;
    const requestedWhenSent: boolean[] = [];
    vi.mocked(api.openFailedDeliveryIncident).mockImplementation(async (operation, signal) => {
      requestedWhenSent.push(queue.only().failedDelivery?.incidentRequested === true);
      return open(operation, signal);
    });
    const scheduler = server.scheduler(queue, { api });

    await scheduler.requestSync(true);
    expect(requestedWhenSent).toEqual([true]);
    expect(queue.only()).toMatchObject({
      status: "RETRY_WAIT",
      failedDelivery: { incidentRequested: true, incidentId: null },
    });
    const cod = { expected: { currency: "MXN" as const, amount_cents: amountCents }, recorded: true };
    server.cod = cod;
    await scheduler.requestSync(true);

    const held = queue.only();
    expect(held).toMatchObject({ status: "NEEDS_ATTENTION", safeError: "COD_COLLECTED" });
    expect(server.keys.incident).toHaveLength(1);
    const message = driverFailedReportAttentionMessage(held, attentionStop({ cod }), [held]);
    expect(message).toContain("Puede que tu reporte ya haya quedado abierto como incidencia");
    expect(message).toContain("Llama a despacho");
    expect(message).not.toContain("el reporte no se envió");
    expect(
      driverFailedReportAttentionMessage(
        { ...held, safeError: "VERSION_CONFLICT" },
        attentionStop({ confirmedVersion: 10 }),
        [held],
      ),
    ).toBe(driverIncidentMaybeOpenReportMessage);
    await scheduler.dispose();
  });
});

describe("a stop REST leaves out (BLOCKER 2)", () => {
  it("never sends a report while REST cannot show its stop, and applies the cash rule once it can", async () => {
    const queue = queuedReport();
    const server = new FailedDeliveryServer();
    server.dropped = true;
    server.cod = { expected: { currency: "MXN", amount_cents: amountCents }, recorded: true };
    const scheduler = server.scheduler(queue);

    await scheduler.requestSync(true);

    expect(server.calls).toEqual([]);
    expect(queue.only()).toMatchObject({
      status: "RETRY_WAIT",
      safeError: "RESOURCE_UNAVAILABLE",
      attemptCount: 1,
    });
    server.dropped = false;
    await scheduler.requestSync(true);
    expect(server.calls).toEqual([]);
    expect(queue.only()).toMatchObject({ status: "NEEDS_ATTENTION", safeError: "COD_COLLECTED" });
    await scheduler.dispose();
  });

  it("never records a collection while REST cannot show its stop, and holds it once REST shows it off the door", async () => {
    const queue = new MemoryQueue([collectionOperation()]);
    const server = new FailedDeliveryServer();
    server.dropped = true;
    server.dispatchMoves("FAILED_ATTEMPT");
    server.cod = { expected: { currency: "MXN", amount_cents: amountCents }, recorded: false };
    const scheduler = server.scheduler(queue);

    await scheduler.requestSync(true);

    expect(server.calls).toEqual([]);
    expect(queue.only()).toMatchObject({ status: "RETRY_WAIT", safeError: "RESOURCE_UNAVAILABLE" });
    server.dropped = false;
    await scheduler.requestSync(true);
    expect(server.calls).toEqual([]);
    expect(queue.only()).toMatchObject({ status: "NEEDS_ATTENTION", safeError: "VERSION_CONFLICT" });
    await scheduler.dispose();
  });

  it("opens no incident when the stop drops out of the answer after the photo", async () => {
    const queue = queuedReport();
    const server = new FailedDeliveryServer();
    server.onStopsRead = () => {
      if (server.stopsReads === 2) server.dropped = true;
    };
    const scheduler = server.scheduler(queue);

    await scheduler.requestSync(true);

    expect(server.calls).toEqual(["session", "upload", "finalize"]);
    expect(queue.only()).toMatchObject({
      status: "RETRY_WAIT",
      safeError: "RESOURCE_UNAVAILABLE",
      failedDelivery: { incidentRequested: false },
    });
    await scheduler.dispose();
  });

  it("does not wait forever: an unsent report whose stop never comes back expires after 72 hours", async () => {
    const queue = queuedReport();
    const server = new FailedDeliveryServer();
    server.dropped = true;
    const onOperationExpired = vi.fn();
    let now = new Date(reportedAt.getTime() + 60_000);
    const scheduler = server.scheduler(queue, { now: () => now, onOperationExpired });

    await scheduler.requestSync(true);
    expect(queue.only().status).toBe("RETRY_WAIT");
    now = new Date(reportedAt.getTime() + 73 * 3_600_000);
    await scheduler.requestSync(true);

    expect(queue.operations).toEqual([]);
    expect(onOperationExpired).toHaveBeenCalledOnce();
    expect(server.calls).toEqual([]);
    await scheduler.dispose();
  });

  it("does not poll forever: an accepted action REST never shows leaves after 72 hours, with no expiry notice", async () => {
    const queue = new MemoryQueue([
      failedDeliveryOperation({
        uploadSessionId: uuid(0x500),
        uploadAccepted: true,
        proofId: uuid(0x600),
        status: "AWAITING_REST_CONFIRMATION",
        failedDelivery: {
          reasonCode: "RECIPIENT_ABSENT",
          incidentIdempotencyKey: `drv2-${reportId}-incident`,
          incidentRequested: true,
          incidentId: uuid(0x700),
        },
      }),
    ]);
    const server = new FailedDeliveryServer();
    server.dropped = true;
    const onOperationExpired = vi.fn();
    let now = new Date(reportedAt.getTime() + 60_000);
    const scheduler = server.scheduler(queue, { now: () => now, onOperationExpired });

    await scheduler.requestSync(true);
    expect(queue.only().status).toBe("AWAITING_REST_CONFIRMATION");
    now = new Date(reportedAt.getTime() + 73 * 3_600_000);
    await scheduler.requestSync(true);

    expect(queue.operations).toEqual([]);
    expect(onOperationExpired).not.toHaveBeenCalled();
    expect(server.calls).toEqual([]);
    await scheduler.dispose();
  });
});

describe("failed-delivery report and the cash (owner answer \"No, llama a despacho\")", () => {
  it.each(["PENDING", "RETRY_WAIT", "NEEDS_ATTENTION"] as const)(
    "never sends a report while a collection of the order is saved on the phone (%s), nor the collection",
    async (status) => {
      const queue = new MemoryQueue([
        collectionOperation({ status }),
        failedDeliveryOperation(),
      ]);
      queue.proofs.set(reportId, photoRecord());
      const server = new FailedDeliveryServer();
      server.cod = { expected: { currency: "MXN", amount_cents: amountCents }, recorded: false };
      const scheduler = server.scheduler(queue);

      await scheduler.requestSync(true);

      // The report is the first action at the door: it is held for the cash
      // and the collection waits behind it, so neither reaches the server.
      expect(server.calls).toEqual([]);
      expect(
        queue.operations.find((operation) => operation.kind === "FAILED_DELIVERY"),
      ).toMatchObject({ status: "NEEDS_ATTENTION", safeError: "COD_COLLECTED" });
      expect(
        queue.operations.find((operation) => operation.kind === "COD_COLLECTION")?.status,
      ).toBe("BLOCKED");
      await scheduler.dispose();
    },
  );

  it("holds the report when REST shows the cash recorded, before any request", async () => {
    const queue = queuedReport();
    const server = new FailedDeliveryServer();
    server.cod = { expected: { currency: "MXN", amount_cents: amountCents }, recorded: true };
    const scheduler = server.scheduler(queue);

    await scheduler.requestSync(true);

    expect(server.calls).toEqual([]);
    expect(queue.only()).toMatchObject({ status: "NEEDS_ATTENTION", safeError: "COD_COLLECTED" });
    expect(queue.proofs.has(reportId)).toBe(true);
    await scheduler.dispose();
  });

  it("holds the report when the cash is recorded while the photo uploads, before the incident", async () => {
    const queue = queuedReport();
    const server = new FailedDeliveryServer();
    server.cod = { expected: { currency: "MXN", amount_cents: amountCents }, recorded: false };
    server.onStopsRead = () => {
      // Dispatch records the cash right after the first read of the pass.
      if (server.stopsReads === 2) server.cod = { ...server.cod!, recorded: true };
    };
    const scheduler = server.scheduler(queue);

    await scheduler.requestSync(true);

    expect(server.calls).toEqual(["session", "upload", "finalize"]);
    expect(queue.only()).toMatchObject({
      status: "NEEDS_ATTENTION",
      safeError: "COD_COLLECTED",
      proofId: server.proofs.get(`drv2-${reportId}-finalize-1`),
      failedDelivery: { incidentId: null },
    });
    await scheduler.dispose();
  });

  it("holds the transition when the cash is recorded right after the incident", async () => {
    const queue = queuedReport();
    const server = new FailedDeliveryServer();
    server.cod = { expected: { currency: "MXN", amount_cents: amountCents }, recorded: false };
    const api = server.api();
    const open = vi.mocked(api.openFailedDeliveryIncident).getMockImplementation()!;
    vi.mocked(api.openFailedDeliveryIncident).mockImplementation(async (operation) => {
      const receipt = await open(operation);
      server.cod = { ...server.cod!, recorded: true };
      return receipt;
    });
    const scheduler = server.scheduler(queue, { api });

    await scheduler.requestSync(true);

    expect(server.calls).toEqual(["session", "upload", "finalize", "incident"]);
    expect(server.transitionsApplied).toBe(0);
    const held = queue.only();
    expect(held).toMatchObject({
      status: "NEEDS_ATTENTION",
      safeError: "COD_COLLECTED",
      failedDelivery: { incidentId: uuid(0x700) },
    });
    // MINOR 8: the incident is open, so the stop never says the report was not sent.
    const message = driverFailedReportAttentionMessage(
      held,
      attentionStop({ cod: server.cod }),
      [held],
    );
    expect(message).toContain("Tu reporte ya quedó abierto como incidencia");
    expect(message).toContain("Llama a despacho");
    expect(message).not.toContain("el reporte no se envió");
    await scheduler.dispose();
  });

  it("treats a collection the server already accepted as recorded", async () => {
    const queue = new MemoryQueue([
      collectionOperation({ status: "AWAITING_REST_CONFIRMATION" }),
      failedDeliveryOperation(),
    ]);
    queue.proofs.set(reportId, photoRecord());
    const server = new FailedDeliveryServer();
    server.cod = { expected: { currency: "MXN", amount_cents: amountCents }, recorded: false };
    const scheduler = server.scheduler(queue);

    await scheduler.requestSync(true);

    expect(server.calls).toEqual([]);
    expect(
      queue.operations.find((operation) => operation.kind === "FAILED_DELIVERY"),
    ).toMatchObject({ status: "NEEDS_ATTENTION", safeError: "COD_COLLECTED" });
    // An action the server already accepted is never turned back into one to send.
    expect(
      queue.operations.find((operation) => operation.kind === "COD_COLLECTION")?.status,
    ).toBe("AWAITING_REST_CONFIRMATION");
    await scheduler.dispose();
  });

  it("never records a collection once its stop left the door (for example after a failed attempt)", async () => {
    const queue = new MemoryQueue([collectionOperation()]);
    const server = new FailedDeliveryServer();
    server.status = "FAILED_ATTEMPT";
    server.version = 9;
    server.cod = { expected: { currency: "MXN", amount_cents: amountCents }, recorded: false };
    const scheduler = server.scheduler(queue);

    await scheduler.requestSync(true);

    expect(server.calls).not.toContain("cod");
    expect(queue.only()).toMatchObject({ status: "NEEDS_ATTENTION", safeError: "VERSION_CONFLICT" });
    await scheduler.dispose();
  });

  it("runs nothing else of the order while a report is pending, retrying or held", () => {
    const now = new Date(reportedAt.getTime() + 60_000);
    const later = collectionOperation({
      createdAt: new Date(reportedAt.getTime() + 30_000).toISOString(),
    });
    for (const report of [
      failedDeliveryOperation(),
      failedDeliveryOperation({
        status: "RETRY_WAIT",
        nextAttemptAt: new Date(now.getTime() + 5_000).toISOString(),
      }),
      failedDeliveryOperation({ status: "NEEDS_ATTENTION", safeError: "COD_COLLECTED" }),
    ]) {
      const selected = selectRunnableOperation([later, report], now, false);
      expect(selected === null || selected.id === report.id).toBe(true);
    }
    expect(selectRunnableOperation([later, failedDeliveryOperation()], now, true)?.kind).toBe(
      "FAILED_DELIVERY",
    );
  });
});

describe("pausing the sync around a discard", () => {
  it("aborts the running pass and starts none until resumed", async () => {
    const queue = new MemoryQueue([collectionOperation()]);
    const server = new FailedDeliveryServer();
    server.cod = { expected: { currency: "MXN", amount_cents: amountCents }, recorded: false };
    let release: () => void = () => undefined;
    const api = server.api();
    vi.mocked(api.recordCodCollection).mockImplementation(
      (_operation, signal) =>
        new Promise((_resolve, reject) => {
          release = () => reject(new DriverSyncApiError("cancelled"));
          signal?.addEventListener("abort", () => release(), { once: true });
        }),
    );
    const scheduler = server.scheduler(queue, { api });

    const running = scheduler.requestSync(true);
    await vi.waitFor(() => expect(api.recordCodCollection).toHaveBeenCalledOnce());
    await scheduler.pause();
    await running;

    // The collection stays as it was persisted; nothing writes it back later.
    await queue.deleteOperation({ key: "J".repeat(43) }, queue.only().id);
    await scheduler.requestSync(true);
    expect(api.recordCodCollection).toHaveBeenCalledOnce();
    scheduler.resume();
    await scheduler.requestSync(true);
    expect(queue.operations).toEqual([]);
    expect(api.recordCodCollection).toHaveBeenCalledOnce();
    await scheduler.dispose();
  });
});
