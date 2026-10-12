import type { DriverStop } from "../contracts/driver-stop";
import type { DriverOfflineOperation } from "./operation-contract";
import type { ProjectedDriverStop } from "./operation-projection";
import {
  driverCashRecordedReportMessage,
  driverCashSavedReportMessage,
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
 * - `hidden`: anything else (not a delivery, not at the door, another action
 *   of the stop needs attention first, or a delivery is already saved).
 */
export type DriverFailedDeliveryAvailability =
  | { readonly kind: "hidden" }
  | { readonly kind: "offered"; readonly paymentReason: boolean }
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
 * the projection shows it, with derived statuses).
 */
export function driverFailedDeliveryAvailability(
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

/** The Spanish message of a failed-delivery report held for the cash. */
export function driverFailedReportHoldMessage(
  stop: Pick<ProjectedDriverStop, "order_id" | "cod">,
  operations: readonly DriverOfflineOperation[],
): string {
  switch (driverFailedReportHoldState(stop, operations)) {
    case "recorded":
      return driverCashRecordedReportMessage;
    case "saved":
      return driverCashSavedReportMessage;
    case "gone":
      return driverCashGoneReportMessage;
  }
}
