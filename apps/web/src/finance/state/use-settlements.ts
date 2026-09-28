"use client";

import { useEffect, useState, useSyncExternalStore } from "react";
import {
  readOperationsSession,
  subscribeToOperationsSession,
} from "../../operations/session/operations-session";
import { apiBaseUrl, loadActiveRole } from "../../operations/state/active-role";
import { createSettlementsApi, type SettlementCsv } from "../api/settlements-api";
import { SettlementsController, type SettlementsState } from "./settlements-controller";

/** Hands the in-memory CSV to the browser and drops the object URL right away. */
function saveCsv(csv: SettlementCsv): void {
  const url = URL.createObjectURL(csv.content);
  try {
    const link = document.createElement("a");
    link.href = url;
    link.download = csv.filename;
    link.rel = "noopener";
    link.click();
  } finally {
    setTimeout(() => URL.revokeObjectURL(url), 0);
  }
}

export function useSettlements(): { state: SettlementsState; controller: SettlementsController } {
  const [controller] = useState(
    () =>
      new SettlementsController({
        readSession: readOperationsSession,
        createApi: (session) => createSettlementsApi(apiBaseUrl(), session),
        loadRole: loadActiveRole,
        download: saveCsv,
        initialSelection: () => new URL(window.location.href).searchParams.get("settlement"),
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
