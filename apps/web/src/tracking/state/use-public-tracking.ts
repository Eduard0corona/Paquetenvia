"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import type { ManagedRealtimeConnection } from "../../realtime/base-connection";
import {
  createPublicTrackingApi,
  PublicTrackingApiError,
} from "../api/public-tracking-api";
import type { PublicTrackingProjection } from "../contracts/public-tracking";
import { createPublicTrackingRealtime } from "../realtime/public-tracking-realtime";
import {
  noOpPublicTrackingTelemetry,
  type PublicTrackingTelemetry,
} from "../telemetry/public-tracking-telemetry";

export type PublicTrackingViewState =
  | "loading"
  | "tracking"
  | "not-found"
  | "unavailable"
  | "rate-limited";
export type PublicTrackingConnectionState =
  | "connecting"
  | "connected"
  | "reconnecting"
  | "offline";

export interface PublicTrackingState {
  readonly view: PublicTrackingViewState;
  readonly connection: PublicTrackingConnectionState;
  readonly projection: PublicTrackingProjection | null;
  readonly lastUpdated: Date | null;
  readonly refresh: () => void;
}

export function usePublicTracking(
  token: string | null,
  apiBaseUrl: string,
  telemetry: PublicTrackingTelemetry = noOpPublicTrackingTelemetry,
): PublicTrackingState {
  const [view, setView] = useState<PublicTrackingViewState>(
    token === null ? "not-found" : "loading",
  );
  const [connection, setConnection] =
    useState<PublicTrackingConnectionState>("connecting");
  const [projection, setProjection] =
    useState<PublicTrackingProjection | null>(null);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);
  const projectionRef = useRef<PublicTrackingProjection | null>(null);
  const activeRequest = useRef<Promise<PublicTrackingProjection | null> | null>(
    null,
  );
  const requestAbort = useRef<AbortController | null>(null);
  const realtime = useRef<ManagedRealtimeConnection | null>(null);
  const debounce = useRef<ReturnType<typeof setTimeout> | null>(null);
  const stopped = useRef(false);

  const lookup = useCallback(
    async (trigger: string): Promise<PublicTrackingProjection | null> => {
      if (token === null || stopped.current) return null;
      if (activeRequest.current !== null) return activeRequest.current;
      telemetry.refreshTriggered(trigger);
      const controller = new AbortController();
      requestAbort.current = controller;
      const api = createPublicTrackingApi({ baseUrl: apiBaseUrl });
      const request = api
        .getProjection(token, controller.signal)
        .then((next) => {
          if (stopped.current) return null;
          projectionRef.current = next;
          setProjection(next);
          setLastUpdated(new Date());
          setView("tracking");
          telemetry.lookupCompleted("rest");
          return next;
        })
        .catch(async (error: unknown) => {
          if (stopped.current) return null;
          const category =
            error instanceof PublicTrackingApiError
              ? error.category
              : "invalid-response";
          telemetry.lookupFailed(category);
          if (category === "not-found") {
            projectionRef.current = null;
            setProjection(null);
            setView("not-found");
            await realtime.current?.stop();
            realtime.current = null;
          } else if (category === "rate-limited") {
            setView("rate-limited");
          } else {
            setView("unavailable");
          }
          return null;
        })
        .finally(() => {
          activeRequest.current = null;
          requestAbort.current = null;
        });
      activeRequest.current = request;
      return request;
    },
    [apiBaseUrl, telemetry, token],
  );

  useEffect(() => {
    stopped.current = false;
    if (token === null) {
      return;
    }

    let polling: ReturnType<typeof setInterval> | null = null;
    const setRealtimeState = (state: PublicTrackingConnectionState) => {
      setConnection(state);
      telemetry.realtimeStateChanged(state);
    };
    const scheduleSignalRefresh = () => {
      if (debounce.current !== null) clearTimeout(debounce.current);
      debounce.current = setTimeout(() => void lookup("realtime"), 250);
    };
    const visible = () => {
      if (!document.hidden) void lookup("visible");
    };
    const online = () => {
      setRealtimeState("reconnecting");
      void lookup("online");
    };
    const offline = () => setRealtimeState("offline");

    void lookup("initial").then(async (initial) => {
      if (initial === null || stopped.current) return;
      const managed = createPublicTrackingRealtime({
        baseUrl: apiBaseUrl,
        token,
        projection: initial,
        refresh: () => lookup("reconnect"),
        onSignal: scheduleSignalRefresh,
        onReconnecting: () => setRealtimeState("reconnecting"),
        onReconnected: () => setRealtimeState("connected"),
        onDisconnected: () => setRealtimeState("offline"),
      });
      realtime.current = managed;
      try {
        await managed.start();
        if (!stopped.current) setRealtimeState("connected");
      } catch {
        if (!stopped.current) setRealtimeState("offline");
      }
    });

    polling = setInterval(() => {
      if (!document.hidden) void lookup("poll");
    }, 30_000);
    document.addEventListener("visibilitychange", visible);
    window.addEventListener("online", online);
    window.addEventListener("offline", offline);

    return () => {
      stopped.current = true;
      requestAbort.current?.abort();
      if (polling !== null) clearInterval(polling);
      if (debounce.current !== null) clearTimeout(debounce.current);
      document.removeEventListener("visibilitychange", visible);
      window.removeEventListener("online", online);
      window.removeEventListener("offline", offline);
      void realtime.current?.stop();
      realtime.current = null;
    };
  }, [apiBaseUrl, lookup, telemetry, token]);

  return {
    view,
    connection,
    projection,
    lastUpdated,
    refresh: () => void lookup("manual"),
  };
}
