"use client";

import { useEffect, useState, useSyncExternalStore } from "react";
import { createDriversApi } from "../api/drivers-api";
import { createLocationsApi } from "../api/locations-api";
import { readOperationsSession, subscribeToOperationsSession } from "../session/operations-session";
import { apiBaseUrl } from "./active-role";
import { CreateRouteFormController, type CreateRouteFormState } from "./create-route-form-controller";

/**
 * UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11: the selectors of "Crear ruta" in Rutas, fed by the same drivers client
 * as the Repartidores screen (listOrganizationDrivers) and by listCities and listServiceAreas. A session or
 * organization change starts from empty lists; nothing is kept outside this tab's memory.
 */
export function useCreateRouteForm(): { state: CreateRouteFormState; controller: CreateRouteFormController } {
  const [controller] = useState(
    () =>
      new CreateRouteFormController({
        readSession: readOperationsSession,
        createApi: (session) => {
          const drivers = createDriversApi(apiBaseUrl(), session);
          const locations = createLocationsApi(apiBaseUrl(), session);
          return {
            listDrivers: (cursor, signal) => drivers.list(cursor, signal),
            listCities: (signal) => locations.listCities(signal),
            listServiceAreas: (cityId, signal) => locations.listServiceAreas(cityId, signal),
          };
        },
      }),
  );
  const state = useSyncExternalStore(controller.subscribe, controller.getSnapshot, controller.getSnapshot);

  useEffect(() => {
    void controller.start();
    const unsubscribe = subscribeToOperationsSession(() => void controller.start());
    return () => {
      unsubscribe();
      controller.stop();
    };
  }, [controller]);

  return { state, controller };
}
