"use client";

import { useEffect, useState, useSyncExternalStore } from "react";
import { createDriversApi } from "../api/drivers-api";
import {
  readOperationsSession,
  subscribeToOperationsSession,
} from "../session/operations-session";
import { apiBaseUrl, loadActiveRole } from "./active-role";
import { DriversController, type DriversState } from "./drivers-controller";

/**
 * UI-PHASE3-DRIVERS-2026-10-11: the "Repartidores" list. Like the other operations lists, the page becoming visible
 * or focused again reads the list from REST (never while hidden, never twice within a few seconds); a session or
 * organization change starts from an empty state. Nothing is kept outside this tab's memory.
 */
export function useDrivers(): { state: DriversState; controller: DriversController } {
  const [controller] = useState(
    () =>
      new DriversController({
        readSession: readOperationsSession,
        createApi: (session) => createDriversApi(apiBaseUrl(), session),
        loadRole: loadActiveRole,
        isHidden: () => document.hidden,
      }),
  );
  const state = useSyncExternalStore(controller.subscribe, controller.getSnapshot, controller.getSnapshot);

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
