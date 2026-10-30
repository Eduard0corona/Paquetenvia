import { realtimeEventTypes } from "../../realtime/event-types";

/**
 * The reloads the order detail gathers (SEC-004 Q9): "realtime" reads the order and so the panel "Avisos al
 * destinatario" too; "notices" reads only that panel (NTF-RECIPIENT-NOTICE-PANEL-2026-10-30 T6 choice 9).
 */
export type OrderDetailReload = "realtime" | "notices";

/** Of two pending reloads, reading the order also reads the panel. */
export function strongerOrderDetailReload(left: OrderDetailReload, right: OrderDetailReload): OrderDetailReload {
  return left === "realtime" || right === "realtime" ? "realtime" : "notices";
}

/**
 * T6 choice 9: a NotificationStatusChanged v2 notice of this order (one of its recipient notices was created or changed
 * status) reloads the panel; a v1 notice, an IN_APP one or another order's is ignored.
 */
export function isRecipientNoticeOfOrder(
  event: { readonly event_type: string; readonly payload: { readonly order_id?: string } },
  orderId: string,
): boolean {
  return event.event_type === realtimeEventTypes.notificationStatusChangedV2 && event.payload.order_id === orderId;
}
