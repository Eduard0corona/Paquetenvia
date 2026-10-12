"use client";

import { useEffect, useState, useSyncExternalStore } from "react";
import type { ManagedRealtimeConnection } from "@/realtime/base-connection";
import { clientApiBaseUrl } from "../../lib/api-base-url";
import { createOperationsApi } from "../api/operations-api";
import { inboxViewKey, parseInboxView, type InboxView } from "../contracts/inbox";
import { createOperationsDashboardRealtime } from "../realtime/operations-dashboard-realtime";
import {
  readOperationsSession,
  subscribeToOperationsSession,
} from "../session/operations-session";
import { InboxController, type InboxState, type InboxTrigger } from "./inbox-controller";

export type InboxConnectionState =
  | "Sin sesión"
  | "Conectando"
  | "Conectada"
  | "Reconectando"
  | "Sin conexión";

export interface OperationsInboxState extends InboxState {
  readonly connection: InboxConnectionState;
  refresh(trigger?: Extract<InboxTrigger, "manual" | "assignment">): void;
  loadMore(): void;
}

/**
 * UI-PHASE3-INBOX-2026-10-10: the work inbox for the view in the URL. Realtime events
 * (OperationsHub, 250 ms debounce), a reconnect, polling every 30 s and the page becoming
 * visible only trigger a REST read of the same view, exactly as the operations dashboard
 * does (ADR-OBS-001); nothing is kept outside this tab's memory.
 */
export function useOperationsInbox(view: InboxView): OperationsInboxState {
  const [controller] = useState(
    () =>
      new InboxController(
        {
          readSession: readOperationsSession,
          createApi: (session) => createOperationsApi(clientApiBaseUrl(), session),
          isHidden: () => document.hidden,
        },
        view,
      ),
  );
  const state = useSyncExternalStore(controller.subscribe, controller.getSnapshot, controller.getSnapshot);
  const [connection, setConnection] = useState<InboxConnectionState>("Sin sesión");
  const viewKey = inboxViewKey(view);

  useEffect(() => {
    controller.setView(parseInboxView(new URLSearchParams(viewKey)));
  }, [controller, viewKey]);

  useEffect(() => {
    const apiBaseUrl = clientApiBaseUrl();
    let realtime: ManagedRealtimeConnection | null = null;
    let realtimeTimer: ReturnType<typeof setTimeout> | null = null;
    let generation = 0;

    const stopRealtime = async () => {
      if (realtimeTimer !== null) clearTimeout(realtimeTimer);
      realtimeTimer = null;
      const current = realtime;
      realtime = null;
      if (current !== null) await current.stop().catch(() => undefined);
    };

    const startSession = async () => {
      const mine = ++generation;
      await stopRealtime();
      if (mine !== generation) return;
      setConnection("Conectando");
      const { session, phase } = await controller.start();
      if (mine !== generation) return;
      if (session === null) {
        setConnection("Sin sesión");
        return;
      }
      // A view change may have superseded the first read (the phase can still be "loading");
      // only a denied access keeps the hub off. Events only ask for another REST read.
      if (phase === "access_unavailable" || controller.getSnapshot().phase === "access_unavailable") {
        setConnection("Sin conexión");
        return;
      }
      const connection = createOperationsDashboardRealtime(apiBaseUrl, session, {
        refreshOperations: () => {
          if (realtimeTimer !== null) clearTimeout(realtimeTimer);
          realtimeTimer = setTimeout(() => {
            realtimeTimer = null;
            void controller.refresh("realtime");
          }, 250);
        },
        // Positions are not part of the inbox; a location update changes no queue.
        refreshLocation: () => undefined,
        resynchronize: async () => {
          if (readOperationsSession() !== session) throw new Error("Session changed.");
          return controller.resynchronize();
        },
        reconnecting: () => {
          if (mine === generation) setConnection("Reconectando");
        },
        connected: () => {
          if (mine === generation) setConnection("Conectada");
        },
        unavailable: () => {
          if (mine === generation) setConnection("Sin conexión");
        },
      });
      realtime = connection;
      try {
        await connection.start();
        if (realtime === connection && mine === generation) setConnection("Conectada");
      } catch {
        if (mine === generation) setConnection("Sin conexión");
      }
    };

    // Lost access (401/403) stops the hub, as on the dashboard.
    const unsubscribeState = controller.subscribe(() => {
      if (controller.getSnapshot().phase !== "access_unavailable" || realtime === null) return;
      void stopRealtime();
      setConnection("Sin conexión");
    });
    const initial = window.setTimeout(() => void startSession(), 0);
    const unsubscribeSession = subscribeToOperationsSession(() => void startSession());
    const onVisibility = () => {
      if (!document.hidden) void controller.refresh("visibility");
    };
    document.addEventListener("visibilitychange", onVisibility);
    const polling = window.setInterval(() => {
      if (!document.hidden) void controller.refresh("polling");
    }, 30_000);

    return () => {
      generation += 1;
      clearTimeout(initial);
      clearInterval(polling);
      unsubscribeSession();
      unsubscribeState();
      document.removeEventListener("visibilitychange", onVisibility);
      controller.stop();
      void stopRealtime();
    };
  }, [controller]);

  return {
    ...state,
    connection,
    refresh: (trigger = "manual") => void controller.refresh(trigger),
    loadMore: () => void controller.loadMore(),
  };
}
