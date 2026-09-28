"use client";

import { useEffect, useState, useSyncExternalStore } from "react";
import { createOrdersApi } from "../api/orders-api";
import {
  readOperationsSession,
  subscribeToOperationsSession,
} from "../session/operations-session";
import { apiBaseUrl, loadActiveRole } from "./active-role";
import { CreateOrderController, type CreateOrderState } from "./create-order-controller";

export function useCreateOrder(): { state: CreateOrderState; controller: CreateOrderController } {
  const [controller] = useState(
    () =>
      new CreateOrderController({
        readSession: readOperationsSession,
        createApi: (session) => createOrdersApi(apiBaseUrl(), session),
        loadRole: loadActiveRole,
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
