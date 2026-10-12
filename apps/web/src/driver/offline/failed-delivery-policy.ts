import type { DriverStop } from "../contracts/driver-stop";
import type { DriverReportRole } from "../contracts/organization-roles";
import type { DriverOfflineOperation } from "./operation-contract";
import type { ProjectedDriverStop } from "./operation-projection";
import {
  driverCashRecordedReportMessage,
  driverCashSavedReportMessage,
  driverIncidentRejectionCodes,
  driverRejectionMessage,
} from "./rejection-codes";

/**
 * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: what the stop shows for
 * "No se pudo entregar".
 *
 * - `offered`: a DELIVERY stop whose confirmed or projected state is DELIVERING,
 *   with nothing at the door saved or recorded; "No pagó el cobro" only when
 *   the stop collects cash (owner answer "Solo si hay cobro (Recommended)").
 * - `cod-saved`: a collection is saved on the phone (owner answer "No, llama a
 *   despacho (Recommended)"): the report is not offered; the driver discards
 *   the collection first if it was a mistake, with the existing discard path.
 * - `cod-recorded`: the server holds the collection (DriverStop.cod.recorded,
 *   or the collection was accepted and only waits for REST): the driver calls
 *   dispatch, who decides and coordinates returning the cash.
 * - `queued` / `attention`: a report is saved on the phone, waiting to sync or
 *   held for the driver's attention.
 * - `dispatch-decides`: REST confirms FAILED_ATTEMPT ("Intento fallido · El
 *   despacho decide").
 * - `dispatch-role`: the report would be offered, but the person also holds
 *   DISPATCHER or PLATFORM_ADMIN in this organization (getMyOrganizationRoles,
 *   ORG-MY-ROLES-2026-10-12), so the server treats a report from the phone as
 *   dispatch's and refuses the short one (owner answer "Registrar desde
 *   Despacho (Recommended)", 2026-10-12): the stop says to register the
 *   attempt from the order in Despacho.
 * - `role-unknown`: the report would be offered, but no answer of
 *   getMyOrganizationRoles is known for the session and none is on its way (the
 *   request failed with nothing saved, or an older API without it): it is not
 *   offered (fails closed) and the stop says why.
 * - `hidden`: anything else (not a delivery, not at the door, another action
 *   of the stop needs attention first, or a delivery is already saved), and
 *   also while the person's roles are still loading with no answer known: the
 *   report is not offered yet and nothing is said about the role.
 */
export type DriverFailedDeliveryAvailability =
  | { readonly kind: "hidden" }
  | { readonly kind: "offered"; readonly paymentReason: boolean }
  | { readonly kind: "dispatch-role" }
  | { readonly kind: "role-unknown" }
  | {
      readonly kind: "cod-saved";
      /** The saved collection, when the stop itself may discard it (not held in the attention notice). */
      readonly discardable: DriverOfflineOperation | null;
    }
  | { readonly kind: "cod-recorded" }
  | { readonly kind: "queued" }
  | {
      readonly kind: "attention";
      /** The saved collection that holds the report, when the driver may discard it here. */
      readonly discardable: DriverOfflineOperation | null;
    }
  | { readonly kind: "dispatch-decides" };

/**
 * `operations` are the projected operations of the stop's order (the queue as
 * the projection shows it, with derived statuses). `reportRole` is
 * driverReportRoleOf what the phone knows of the person's roles: only `driver`
 * is ever offered the report.
 */
export function driverFailedDeliveryAvailability(
  stop: ProjectedDriverStop,
  operations: readonly DriverOfflineOperation[],
  reportRole: DriverReportRole,
): DriverFailedDeliveryAvailability {
  const availability = availabilityForDriver(stop, operations);
  if (availability.kind !== "offered" || reportRole === "driver") {
    return availability;
  }
  switch (reportRole) {
    case "dispatch":
      return dispatchRole;
    case "loading":
      return hidden;
    case "unknown":
      return roleUnknown;
  }
}

function availabilityForDriver(
  stop: ProjectedDriverStop,
  operations: readonly DriverOfflineOperation[],
): DriverFailedDeliveryAvailability {
  if (stop.stop_type !== "DELIVERY") return hidden;
  if (stop.confirmedStatus === "FAILED_ATTEMPT") {
    return Object.freeze({ kind: "dispatch-decides" });
  }
  const own = operations.filter(
    (operation) => operation.orderId === stop.order_id,
  );
  const report = own.find((operation) => operation.kind === "FAILED_DELIVERY");
  const collection =
    own.find((operation) => operation.kind === "COD_COLLECTION") ?? null;
  if (report) {
    if (report.status === "NEEDS_ATTENTION" || report.status === "BLOCKED") {
      return Object.freeze({
        kind: "attention",
        discardable:
          report.safeError === "COD_COLLECTED" &&
          collection !== null &&
          collection.status !== "AWAITING_REST_CONFIRMATION"
            ? collection
            : null,
      });
    }
    return Object.freeze({ kind: "queued" });
  }
  if (stop.projectedStatus !== "DELIVERING") return hidden;
  if (
    stop.cod !== null &&
    (stop.cod.recorded ||
      collection?.status === "AWAITING_REST_CONFIRMATION")
  ) {
    return Object.freeze({ kind: "cod-recorded" });
  }
  if (collection !== null) {
    return Object.freeze({
      kind: "cod-saved",
      // A held collection is discarded from the stop's attention notice.
      discardable:
        collection.status === "NEEDS_ATTENTION" ||
        collection.status === "BLOCKED"
          ? null
          : collection,
    });
  }
  if (stop.attentionCount > 0 || stop.deliveryAwaitingCash) return hidden;
  return Object.freeze({ kind: "offered", paymentReason: stop.cod !== null });
}

const hidden: DriverFailedDeliveryAvailability = Object.freeze({
  kind: "hidden",
});

const dispatchRole: DriverFailedDeliveryAvailability = Object.freeze({
  kind: "dispatch-role",
});

const roleUnknown: DriverFailedDeliveryAvailability = Object.freeze({
  kind: "role-unknown",
});

/**
 * Owner answer "Registrar desde Despacho (Recommended)" (2026-10-12): shown in
 * place of "No se pudo entregar" to a person who is also dispatch in the
 * organization, known from getMyOrganizationRoles (ORG-MY-ROLES-2026-10-12).
 * The server keeps treating that person's report as dispatch's, so the phone
 * never offers it and uploads no photo.
 */
export const driverDispatchRoleReportMessage =
  "También eres despacho en esta organización, así que el intento fallido no se reporta desde el teléfono: " +
  "regístralo desde la orden en Despacho, donde eliges el motivo y el siguiente paso.";

/**
 * ORG-MY-ROLES-2026-10-12: shown in place of "No se pudo entregar" when no
 * answer of getMyOrganizationRoles is known for the session and none is on its
 * way (the report fails closed). The phone asks again when the connection
 * returns or the app is opened again; it never asks the driver to sign out,
 * which would lose the data saved on the phone.
 */
export const driverRoleUnknownReportMessage =
  "No pudimos confirmar tu rol. Vuelve a abrir la app con conexión para reportar.";

/**
 * ORG-MY-ROLES-2026-10-12: "Enviar reporte" while the roles are still loading
 * with no answer known (the form is not offered then, so this only covers a
 * race). Neutral: nothing is wrong yet.
 */
export const driverRoleLoadingReportMessage =
  "Estamos confirmando tu rol. Intenta de nuevo en un momento.";

export type DriverFailedReportCashHold = "recorded" | "saved" | null;

/**
 * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12 (owner answer "No, llama a
 * despacho (Recommended)"): why a queued failed-delivery report must not be
 * sent. "recorded" when REST reports the cash recorded or the queue holds a
 * collection the server already accepted; "saved" when a collection of the
 * same order is saved on the phone (pending, retrying or held); null when the
 * report may go. The server still accepts both; the PWA never sends both.
 */
export function driverFailedReportCashHold(
  report: DriverOfflineOperation,
  stop: Pick<DriverStop, "cod"> | null,
  operations: readonly DriverOfflineOperation[],
): DriverFailedReportCashHold {
  if (report.kind !== "FAILED_DELIVERY") return null;
  if (stop?.cod?.recorded === true) return "recorded";
  const collection = operations.find(
    (candidate) =>
      candidate.orderId === report.orderId &&
      candidate.kind === "COD_COLLECTION",
  );
  if (!collection) return null;
  return collection.status === "AWAITING_REST_CONFIRMATION"
    ? "recorded"
    : "saved";
}

/** Shown when a report held for the cash no longer has a collection saved or recorded. */
export const driverCashGoneReportMessage =
  "Ya no hay un cobro guardado para esta entrega. Reintenta la misma acción para enviar el reporte.";

/**
 * Where the cash that holds a failed-delivery report (safeError COD_COLLECTED)
 * stands now: recorded on the server (the report can never be sent from this
 * phone), still saved on the phone, or gone (the report may be retried).
 */
export function driverFailedReportHoldState(
  stop: Pick<ProjectedDriverStop, "order_id" | "cod">,
  operations: readonly DriverOfflineOperation[],
): "recorded" | "saved" | "gone" {
  const collection = operations.find(
    (operation) =>
      operation.orderId === stop.order_id &&
      operation.kind === "COD_COLLECTION",
  );
  if (
    stop.cod?.recorded === true ||
    collection?.status === "AWAITING_REST_CONFIRMATION"
  ) {
    return "recorded";
  }
  return collection ? "saved" : "gone";
}

/**
 * Whether the report's incident exists on the server: "open" when the phone
 * holds its id; "maybe" when openIncident was sent but its answer never came
 * back (and the server did not refuse it with a rule code); "none" otherwise.
 */
export function driverFailedReportIncident(
  report: DriverOfflineOperation,
): "open" | "maybe" | "none" {
  const failedDelivery = report.failedDelivery;
  if (failedDelivery === null) return "none";
  if (failedDelivery.incidentId !== null) return "open";
  return failedDelivery.incidentRequested &&
    !(driverIncidentRejectionCodes as readonly (string | null)[]).includes(
      report.rejectionCode,
    )
    ? "maybe"
    : "none";
}

/** Before any request: dispatch already registered the failed attempt itself. */
export const driverDispatchReportedFailedAttemptMessage =
  "Despacho ya registró el intento fallido de esta entrega, así que tu reporte no se envió. Descártalo.";

/** Before any request: the stop changed since the report was saved. */
export const driverStaleReportMessage =
  "La entrega cambió desde que guardaste el reporte (por ejemplo, despacho pidió reintentarla), así que el " +
  "reporte no se envió. Descártalo y, si de nuevo no puedes entregar, vuelve a reportar con una foto nueva.";

/** MINOR 8: once the incident exists it is never presented as "not sent". */
export const driverIncidentOpenReportMessage =
  "Tu reporte ya quedó abierto como incidencia y despacho decide qué sigue. No lo vuelvas a reportar: descarta esta acción.";

export const driverIncidentMaybeOpenReportMessage =
  "Puede que tu reporte ya haya quedado abierto como incidencia, pero el teléfono no lo pudo confirmar. " +
  "Despacho decide qué sigue: avísales, no lo vuelvas a reportar y descarta esta acción.";

const incidentLead: Readonly<Record<"open" | "maybe", string>> = {
  open: "Tu reporte ya quedó abierto como incidencia",
  maybe: "Puede que tu reporte ya haya quedado abierto como incidencia",
};

/**
 * The Spanish message of a "No se pudo entregar" report held for the driver's
 * attention, so the stop never says "not sent" for a report whose incident is
 * (or may be) open and never invites reporting it again:
 *
 * - incident open or maybe open: that first, then the cash that holds the
 *   transition, if any (recorded: call dispatch; saved: discard it if it was a
 *   mistake; gone: retry the same action to finish);
 * - incident refused by the server (a rule code of openIncident): its message;
 * - held for the cash before any incident: the cash messages;
 * - held before any request because the attempt moved on: dispatch already
 *   registered the failed attempt, or the stop changed since the report;
 * - anything else: the rule message of its code, or the generic one.
 */
export function driverFailedReportAttentionMessage(
  report: DriverOfflineOperation,
  stop: Pick<
    ProjectedDriverStop,
    "order_id" | "cod" | "confirmedStatus" | "confirmedVersion"
  >,
  operations: readonly DriverOfflineOperation[],
): string {
  const incident = driverFailedReportIncident(report);
  const cash =
    report.safeError === "COD_COLLECTED"
      ? driverFailedReportHoldState(stop, operations)
      : null;
  if (incident !== "none") {
    const lead = incidentLead[incident];
    switch (cash) {
      case "recorded":
        return (
          `${lead}, pero el cobro de esta entrega también quedó registrado, así que el intento fallido ` +
          "no se envió. Llama a despacho: ellos deciden y coordinan la devolución del cobro. No lo " +
          "vuelvas a reportar: descarta esta acción."
        );
      case "saved":
        return (
          `${lead}, pero hay un cobro guardado en el teléfono, así que el intento fallido no se envió. ` +
          "Si el cobro fue un error, descártalo y el reporte terminará de enviarse; si no, llama a despacho."
        );
      case "gone":
        return `${lead}. Ya no hay un cobro guardado: reintenta la misma acción para terminar de enviarlo.`;
      case null:
        return incident === "open"
          ? driverIncidentOpenReportMessage
          : driverIncidentMaybeOpenReportMessage;
    }
  }
  if (report.rejectionCode !== null) {
    return driverRejectionMessage(report.kind, report.rejectionCode);
  }
  switch (cash) {
    case "recorded":
      return driverCashRecordedReportMessage;
    case "saved":
      return driverCashSavedReportMessage;
    case "gone":
      return driverCashGoneReportMessage;
    case null:
      break;
  }
  if (report.safeError === "VERSION_CONFLICT") {
    if (stop.confirmedStatus === "FAILED_ATTEMPT") {
      return driverDispatchReportedFailedAttemptMessage;
    }
    if (
      stop.confirmedStatus !== "DELIVERING" ||
      stop.confirmedVersion !== report.expectedVersion
    ) {
      return driverStaleReportMessage;
    }
  }
  return driverRejectionMessage(report.kind, null);
}
