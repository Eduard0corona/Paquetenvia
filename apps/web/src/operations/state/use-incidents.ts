"use client";

import { useEffect, useState, useSyncExternalStore } from "react";
import { createIncidentsApi } from "../api/incidents-api";
import {
  readOperationsSession,
  subscribeToOperationsSession,
} from "../session/operations-session";
import { apiBaseUrl, loadActiveRole } from "./active-role";
import { IncidentsController, type IncidentsState } from "./incidents-controller";

export function useIncidents(): { state: IncidentsState; controller: IncidentsController } {
  const [controller] = useState(
    () =>
      new IncidentsController({
        readSession: readOperationsSession,
        createApi: (session) => createIncidentsApi(apiBaseUrl(), session),
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
