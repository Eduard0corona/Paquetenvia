"use client";

import { useEffect, useState, useSyncExternalStore } from "react";
import { realtimeCredentials } from "../../auth/request-credentials";
import { clientApiBaseUrl } from "../../lib/api-base-url";
import type { ManagedRealtimeConnection } from "../../realtime/base-connection";
import type { Uuid } from "../../realtime/envelope";
import { createOperationsConnection } from "../../realtime/operations-connection";
import { createRoutesApi } from "../api/routes-api";
import { subscribeToOperationsSession, readOperationsSession } from "../session/operations-session";
import { ManualRoutesController, type ManualRoutesState } from "./manual-routes-controller";

/**
 * The route planner (`/ops/routes`): REST is the source of truth (ManualRoutesController, UI-PHASE3-ROUTES-NO-IDS-
 * 2026-10-12) and the operations hub only asks for it - RouteChanged reads the list (and the route shown) again, at
 * most once every 3 seconds (owner answer Q9 of SEC-004-RELOADS-AND-BULK-2026-10-13), and a reconnection
 * resynchronizes from that read. A session or organization change stops the hub and starts from empty.
 * Nothing is kept outside this tab's memory.
 */
export function useManualRoutes(): { state: ManualRoutesState; controller: ManualRoutesController } {
  const [controller] = useState(
    () =>
      new ManualRoutesController({
        readSession: readOperationsSession,
        createApi: (session) => createRoutesApi(clientApiBaseUrl(), session),
        readQuery: () => new URL(window.location.href).searchParams,
      }),
  );
  const state = useSyncExternalStore(controller.subscribe, controller.getSnapshot, controller.getSnapshot);

  useEffect(() => {
    let cancelled = false;
    let run = 0;
    let connection: ManagedRealtimeConnection | null = null;
    const stopConnection = async () => {
      const previous = connection;
      connection = null;
      if (previous) await previous.stop().catch(() => undefined);
    };
    const start = async () => {
      const current = ++run;
      await stopConnection();
      const session = await controller.start();
      if (cancelled || current !== run || session === null) return;
      const next = createOperationsConnection(
        {
          baseUrl: clientApiBaseUrl(),
          organizationId: session.organizationId as Uuid,
          ...realtimeCredentials(session),
          suppressLogging: true,
          onReconnecting: () => controller.setConnected(false),
          onResynchronized: () => controller.setConnected(true),
          onResynchronizationError: () => controller.realtimeFailed(),
          resynchronizeFromRest: () => controller.resynchronize(),
        },
        {
          // Owner answer Q9: gathered into at most one read every 3 seconds.
          RouteChanged: (event) => controller.notice(event.payload.route_id),
        },
      );
      connection = next;
      try {
        await next.start();
        if (!cancelled && current === run) controller.setConnected(true);
      } catch {
        // Without the hub the screen keeps working by REST ("Actualización manual").
        if (!cancelled && current === run) controller.setConnected(false);
      }
    };
    void start();
    const unsubscribe = subscribeToOperationsSession(() => void start());
    return () => {
      cancelled = true;
      unsubscribe();
      void stopConnection();
      controller.stop();
    };
  }, [controller]);

  return { state, controller };
}
