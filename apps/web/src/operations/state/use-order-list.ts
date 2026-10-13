"use client";

import { useEffect, useState, useSyncExternalStore } from "react";
import { createOrderListApi } from "../api/order-list-api";
import { orderListViewKey, parseOrderListView, type OrderListView } from "../contracts/order-list";
import {
  readOperationsSession,
  subscribeToOperationsSession,
} from "../session/operations-session";
import { apiBaseUrl, loadActiveRole } from "./active-role";
import { OrderListController, type OrderListState } from "./order-list-controller";

/**
 * UI-PHASE3-ORDERS-LIST-2026-10-12: the "Órdenes" list for the filters in the URL. Like the other operations lists,
 * the page becoming visible or focused again reads the list from REST (never while hidden, never twice within a few
 * seconds, and not once "Cargar más" loaded further pages); a session or organization change starts from an empty
 * list. Nothing is kept outside this tab's memory.
 */
export function useOrderList(view: OrderListView): { state: OrderListState; controller: OrderListController } {
  const [controller] = useState(
    () =>
      new OrderListController(
        {
          readSession: readOperationsSession,
          createApi: (session) => createOrderListApi(apiBaseUrl(), session),
          loadRole: loadActiveRole,
          isHidden: () => document.hidden,
        },
        view,
      ),
  );
  const state = useSyncExternalStore(controller.subscribe, controller.getSnapshot, controller.getSnapshot);
  const viewKey = orderListViewKey(view);

  useEffect(() => {
    void controller.setView(parseOrderListView(new URLSearchParams(viewKey)));
  }, [controller, viewKey]);

  useEffect(() => {
    void controller.start();
    const unsubscribe = subscribeToOperationsSession(() => void controller.start());
    const onVisibility = () => {
      if (!document.hidden) void controller.refresh("visibility");
    };
    const onFocus = () => void controller.refresh("focus");
    document.addEventListener("visibilitychange", onVisibility);
    window.addEventListener("focus", onFocus);
    return () => {
      unsubscribe();
      document.removeEventListener("visibilitychange", onVisibility);
      window.removeEventListener("focus", onFocus);
      controller.stop();
    };
  }, [controller]);

  return { state, controller };
}
