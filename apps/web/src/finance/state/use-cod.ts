"use client";

import { useEffect, useState, useSyncExternalStore } from "react";
import {
  readOperationsSession,
  subscribeToOperationsSession,
} from "../../operations/session/operations-session";
import { apiBaseUrl, loadActiveRole } from "../../operations/state/active-role";
import { createCodApi } from "../api/cod-api";
import { CodController, type CodState } from "./cod-controller";

export function useCod(): { state: CodState; controller: CodController } {
  const [controller] = useState(
    () =>
      new CodController({
        readSession: readOperationsSession,
        createApi: (session) => createCodApi(apiBaseUrl(), session),
        loadRole: loadActiveRole,
        initialOrder: () => new URL(window.location.href).searchParams.get("order"),
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
