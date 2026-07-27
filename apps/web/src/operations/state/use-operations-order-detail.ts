"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import type { ManagedRealtimeConnection } from "@/realtime/base-connection";
import {
  createOperationsApi,
  OperationsApiError,
  type OperationsDashboardApi,
} from "../api/operations-api";
import type {
  OperationsDashboardOrder,
  OperationsOrderDetail,
} from "../contracts/operations-dashboard";
import { createOperationsDashboardRealtime } from "../realtime/operations-dashboard-realtime";
import {
  readOperationsSession,
  subscribeToOperationsSession,
  type OperationsSession,
} from "../session/operations-session";

export interface OperationsOrderDetailState {
  readonly order: OperationsOrderDetail | null;
  readonly projection: OperationsDashboardOrder | null;
  readonly loading: boolean;
  readonly notFound: boolean;
  readonly accessUnavailable: boolean;
  readonly error: string | null;
  readonly connection: string;
  readonly lastUpdated: Date | null;
  refresh(): void;
}

export function useOperationsOrderDetail(
  orderId: string,
): OperationsOrderDetailState {
  const apiBaseUrl =
    process.env.NEXT_PUBLIC_API_BASE_URL ??
    (typeof window === "undefined" ? "http://127.0.0.1" : window.location.origin);
  const [order, setOrder] = useState<OperationsOrderDetail | null>(null);
  const [projection, setProjection] =
    useState<OperationsDashboardOrder | null>(null);
  const [loading, setLoading] = useState(true);
  const [notFound, setNotFound] = useState(false);
  const [accessUnavailable, setAccessUnavailable] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [connectionState, setConnectionState] = useState("Sin sesión");
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);
  const apiRef = useRef<OperationsDashboardApi | null>(null);
  const sessionRef = useRef<OperationsSession | null>(null);
  const connectionRef = useRef<ManagedRealtimeConnection | null>(null);
  const abortRef = useRef<AbortController | null>(null);
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const inFlightRef = useRef(false);
  const pendingRef = useRef(false);
  const projectionRef = useRef(projection);
  const blockedRef = useRef(false);
  const load = useCallback(
    async (api: OperationsDashboardApi, session: OperationsSession) => {
      if (inFlightRef.current) {
        pendingRef.current = true;
        return;
      }
      inFlightRef.current = true;
      const controller = new AbortController();
      abortRef.current?.abort();
      abortRef.current = controller;
      setLoading(true);
      try {
        const [detail, page] = await Promise.all([
          api.getOrder(orderId, controller.signal),
          api.list({ orderId }, controller.signal),
        ]);
        if (sessionRef.current !== session) return;
        if (page.items.length !== 1) throw new OperationsApiError("not_found");
        setOrder(detail);
        setProjection(page.items[0]);
        projectionRef.current = page.items[0];
        setNotFound(false);
        setAccessUnavailable(false);
        setError(null);
        setLastUpdated(new Date(page.generated_at));
      } catch (caught: unknown) {
        if (controller.signal.aborted) return;
        const category =
          caught instanceof OperationsApiError ? caught.category : "unavailable";
        if (category === "not_found") {
          setNotFound(true);
          setOrder(null);
          setProjection(null);
        } else if (category === "unauthorized" || category === "forbidden") {
          blockedRef.current = true;
          setAccessUnavailable(true);
          setOrder(null);
          setProjection(null);
          await connectionRef.current?.stop().catch(() => undefined);
          connectionRef.current = null;
        } else {
          setError("No fue posible actualizar la orden.");
        }
      } finally {
        if (abortRef.current === controller) abortRef.current = null;
        inFlightRef.current = false;
        setLoading(false);
        if (pendingRef.current) {
          pendingRef.current = false;
          window.dispatchEvent(
            new Event("paquetenvia:operations-detail-coalesced"),
          );
        }
      }
    },
    [orderId],
  );

  const schedule = useCallback(
    (delay: number) => {
      if (timerRef.current !== null) clearTimeout(timerRef.current);
      timerRef.current = setTimeout(() => {
        timerRef.current = null;
        const api = apiRef.current;
        const session = sessionRef.current;
        if (api !== null && session !== null) void load(api, session);
      }, delay);
    },
    [load],
  );

  const start = useCallback(async () => {
    abortRef.current?.abort();
    if (timerRef.current !== null) clearTimeout(timerRef.current);
    await connectionRef.current?.stop().catch(() => undefined);
    connectionRef.current = null;
    setOrder(null);
    setProjection(null);
    setLastUpdated(null);
    setNotFound(false);
    setAccessUnavailable(false);
    blockedRef.current = false;
    const session = readOperationsSession();
    sessionRef.current = session;
    if (session === null) {
      setConnectionState("Sin sesión");
      setLoading(false);
      return;
    }
    const api = createOperationsApi(apiBaseUrl, session);
    apiRef.current = api;
    setConnectionState("Conectando");
    await load(api, session);
    if (sessionRef.current !== session || blockedRef.current) return;
    const realtime = createOperationsDashboardRealtime(apiBaseUrl, session, {
      refreshOperations: () => schedule(250),
      refreshLocation: () => schedule(500),
      resynchronize: async () => {
        if (readOperationsSession() !== session) throw new Error("Session changed.");
        await load(api, session);
        const item = projectionRef.current;
        return {
          aggregate_versions:
            item === null ? {} : { [item.order_id]: item.aggregate_version },
        };
      },
      reconnecting: () => setConnectionState("Reconectando"),
      connected: () => setConnectionState("Conectada"),
      unavailable: () => setConnectionState("Sin conexión"),
    });
    connectionRef.current = realtime;
    try {
      await realtime.start();
      if (connectionRef.current === realtime) setConnectionState("Conectada");
    } catch {
      setConnectionState("Sin conexión");
    }
  }, [apiBaseUrl, load, schedule]);

  useEffect(() => {
    const initial = window.setTimeout(() => void start(), 0);
    const unsubscribe = subscribeToOperationsSession(() => void start());
    return () => {
      clearTimeout(initial);
      unsubscribe();
    };
  }, [start]);

  useEffect(() => {
    const coalesced = () => {
      const api = apiRef.current;
      const session = sessionRef.current;
      if (api !== null && session !== null) void load(api, session);
    };
    window.addEventListener(
      "paquetenvia:operations-detail-coalesced",
      coalesced,
    );
    return () =>
      window.removeEventListener(
        "paquetenvia:operations-detail-coalesced",
        coalesced,
      );
  }, [load]);

  useEffect(() => {
    const onVisible = () => {
      if (document.hidden) return;
      const api = apiRef.current;
      const session = sessionRef.current;
      if (api !== null && session !== null) void load(api, session);
    };
    document.addEventListener("visibilitychange", onVisible);
    const interval = window.setInterval(() => {
      if (!document.hidden && !accessUnavailable) onVisible();
    }, 30_000);
    return () => {
      document.removeEventListener("visibilitychange", onVisible);
      clearInterval(interval);
    };
  }, [accessUnavailable, load]);

  useEffect(
    () => () => {
      abortRef.current?.abort();
      if (timerRef.current !== null) clearTimeout(timerRef.current);
      void connectionRef.current?.stop();
    },
    [],
  );

  return {
    order,
    projection,
    loading,
    notFound,
    accessUnavailable,
    error,
    connection: connectionState,
    lastUpdated,
    refresh: () => {
      const api = apiRef.current;
      const session = sessionRef.current;
      if (api !== null && session !== null) void load(api, session);
    },
  };
}
