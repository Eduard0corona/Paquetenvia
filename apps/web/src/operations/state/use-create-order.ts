"use client";

import { useEffect, useState, useSyncExternalStore } from "react";
import { createOrderActionsApi } from "../api/order-actions-api";
import { createOrdersApi } from "../api/orders-api";
import {
  readOperationsSession,
  subscribeToOperationsSession,
} from "../session/operations-session";
import type { AcceptanceVersions } from "../contracts/acceptance-versions";
import { apiBaseUrl, loadActiveRole } from "./active-role";
import { CreateOrderController, type CreateOrderState } from "./create-order-controller";

export function useCreateOrder(
  acceptanceVersions: AcceptanceVersions | null,
): { state: CreateOrderState; controller: CreateOrderController } {
  const [controller] = useState(
    () =>
      new CreateOrderController({
        readSession: readOperationsSession,
        createApi: (session) => createOrdersApi(apiBaseUrl(), session),
        // UI-PHASE3-ORDER-WIZARD-2026-10-10: the order is confirmed with the same
        // transitionOrder client as the order detail's "Siguiente paso".
        createActionsApi: (session) => createOrderActionsApi(apiBaseUrl(), session),
        loadRole: loadActiveRole,
        acceptanceVersions,
      }),
  );
  const state = useSyncExternalStore(
    controller.subscribe,
    controller.getSnapshot,
    controller.getSnapshot,
  );
  useEffect(() => {
    void controller.start();
    // A tenant switch or sign-out restarts from an empty state.
    const unsubscribe = subscribeToOperationsSession(() => void controller.start());
    return () => {
      unsubscribe();
      controller.stop();
    };
  }, [controller]);
  return { state, controller };
}
