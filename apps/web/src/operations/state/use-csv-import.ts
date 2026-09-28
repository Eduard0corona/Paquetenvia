"use client";

import { useEffect, useState, useSyncExternalStore } from "react";
import { createCsvImportApi } from "../api/csv-import-api";
import {
  readOperationsSession,
  subscribeToOperationsSession,
} from "../session/operations-session";
import { apiBaseUrl, loadActiveRole } from "./active-role";
import { CsvImportController, type CsvImportState } from "./csv-import-controller";

export function useCsvImport(): { state: CsvImportState; controller: CsvImportController } {
  const [controller] = useState(
    () =>
      new CsvImportController({
        readSession: readOperationsSession,
        createApi: (session) => createCsvImportApi(apiBaseUrl(), session),
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
