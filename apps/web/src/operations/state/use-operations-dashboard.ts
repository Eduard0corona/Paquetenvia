"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import type { ManagedRealtimeConnection } from "@/realtime/base-connection";
import type {
  OperationsDashboardFilters,
  OperationsDashboardOrder,
  OperationsOrganizationContext,
} from "../contracts/operations-dashboard";
import {
  createOperationsApi,
  OperationsApiError,
  type OperationsDashboardApi,
} from "../api/operations-api";
import {
  readOperationsSession,
  subscribeToOperationsSession,
  type OperationsSession,
} from "../session/operations-session";
import { createOperationsDashboardRealtime } from "../realtime/operations-dashboard-realtime";
import {
  noOpOperationsTelemetry,
  type OperationsDashboardTelemetry,
} from "../telemetry/operations-telemetry";

type ConnectionState =
  | "Sin sesión"
  | "Conectando"
  | "Conectada"
  | "Reconectando"
  | "Sin conexión";

export interface OperationsDashboardState {
  readonly items: readonly OperationsDashboardOrder[];
  readonly contexts: readonly OperationsOrganizationContext[];
  readonly activeOrganizationName: string;
  readonly nextCursor: string | null;
  readonly loading: boolean;
  readonly loadingMore: boolean;
  readonly accessUnavailable: boolean;
  readonly error: string | null;
  readonly connection: ConnectionState;
  readonly lastUpdated: Date | null;
  readonly filters: OperationsDashboardFilters;
  setFilters(filters: OperationsDashboardFilters): void;
  refresh(): void;
  loadMore(): void;
  requestOrganizationChange(organizationId: string): Promise<void>;
  readonly canChangeOrganization: boolean;
}

export function useOperationsDashboard(
  telemetry: OperationsDashboardTelemetry = noOpOperationsTelemetry,
): OperationsDashboardState {
  const apiBaseUrl = process.env.NEXT_PUBLIC_API_BASE_URL ?? windowOrigin();
  const [items, setItems] = useState<readonly OperationsDashboardOrder[]>([]);
  const [contexts, setContexts] = useState<
    readonly OperationsOrganizationContext[]
  >([]);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [filters, setFiltersState] = useState<OperationsDashboardFilters>({});
  const [loading, setLoading] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [accessUnavailable, setAccessUnavailable] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [connection, setConnection] =
    useState<ConnectionState>("Sin sesión");
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);
  const [activeOrganizationId, setActiveOrganizationId] = useState("");
  const [canChangeOrganization, setCanChangeOrganization] = useState(false);
  const sessionRef = useRef<OperationsSession | null>(null);
  const apiRef = useRef<OperationsDashboardApi | null>(null);
  const connectionRef = useRef<ManagedRealtimeConnection | null>(null);
  const abortRef = useRef<AbortController | null>(null);
  const filtersRef = useRef(filters);
  const itemsRef = useRef(items);
  const inFlightRef = useRef(false);
  const pendingRefreshRef = useRef(false);
  const operationsTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const locationTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const seenCursorsRef = useRef(new Set<string>());
  const nextCursorRef = useRef<string | null>(null);
  const clearTimers = useCallback(() => {
    if (operationsTimerRef.current !== null)
      clearTimeout(operationsTimerRef.current);
    if (locationTimerRef.current !== null)
      clearTimeout(locationTimerRef.current);
    operationsTimerRef.current = null;
    locationTimerRef.current = null;
  }, []);

  const clearForSessionChange = useCallback(async () => {
    abortRef.current?.abort();
    abortRef.current = null;
    clearTimers();
    const current = connectionRef.current;
    connectionRef.current = null;
    if (current !== null) await current.stop().catch(() => undefined);
    setItems([]);
    itemsRef.current = [];
    setContexts([]);
    setNextCursor(null);
    nextCursorRef.current = null;
    seenCursorsRef.current.clear();
    setLastUpdated(null);
    setError(null);
    setAccessUnavailable(false);
  }, [clearTimers]);

  const performLoad = useCallback(
    async (mode: "replace" | "append", trigger: string) => {
      const api = apiRef.current;
      const session = sessionRef.current;
      if (api === null || session === null || document.hidden) return;
      if (inFlightRef.current) {
        pendingRefreshRef.current = true;
        return;
      }
      inFlightRef.current = true;
      telemetry.refreshTriggered(trigger);
      const controller = new AbortController();
      abortRef.current?.abort();
      abortRef.current = controller;
      if (mode === "replace") setLoading(true);
      else setLoadingMore(true);
      try {
        const requestFilters =
          mode === "append" && nextCursorRef.current !== null
            ? { ...filtersRef.current, cursor: nextCursorRef.current }
            : { ...filtersRef.current, cursor: undefined };
        const response = await api.list(requestFilters, controller.signal);
        if (sessionRef.current !== session) return;
        if (
          response.next_cursor !== null &&
          seenCursorsRef.current.has(response.next_cursor)
        ) {
          throw new Error("Cursor cycle.");
        }
        if (response.next_cursor !== null)
          seenCursorsRef.current.add(response.next_cursor);
        const merged =
          mode === "append"
            ? mergeOperationsOrders(itemsRef.current, response.items)
            : [...response.items];
        setItems(merged);
        itemsRef.current = merged;
        setNextCursor(response.next_cursor);
        nextCursorRef.current = response.next_cursor;
        setLastUpdated(new Date(response.generated_at));
        setError(null);
        setAccessUnavailable(false);
        telemetry.lookupCompleted("rest");
      } catch (caught: unknown) {
        if (controller.signal.aborted) return;
        const category =
          caught instanceof OperationsApiError ? caught.category : "contract";
        telemetry.lookupFailed(category);
        if (category === "unauthorized" || category === "forbidden") {
          setItems([]);
          itemsRef.current = [];
          setAccessUnavailable(true);
          setConnection("Sin conexión");
          await connectionRef.current?.stop().catch(() => undefined);
          connectionRef.current = null;
        } else {
          setError("No fue posible actualizar las operaciones.");
        }
      } finally {
        if (abortRef.current === controller) abortRef.current = null;
        inFlightRef.current = false;
        setLoading(false);
        setLoadingMore(false);
        if (pendingRefreshRef.current) {
          pendingRefreshRef.current = false;
          window.dispatchEvent(new Event("paquetenvia:operations-coalesced"));
        }
      }
    },
    [telemetry],
  );

  const scheduleRefresh = useCallback(
    (delay: number, trigger: string) => {
      const ref = delay === 500 ? locationTimerRef : operationsTimerRef;
      if (ref.current !== null) clearTimeout(ref.current);
      ref.current = setTimeout(() => {
        ref.current = null;
        seenCursorsRef.current.clear();
        void performLoad("replace", trigger);
      }, delay);
    },
    [performLoad],
  );

  const startSession = useCallback(async () => {
    await clearForSessionChange();
    const session = readOperationsSession();
    sessionRef.current = session;
    if (session === null) {
      apiRef.current = null;
      setConnection("Sin sesión");
      return;
    }
    setActiveOrganizationId(session.organizationId);
    setCanChangeOrganization(
      session.requestOrganizationChange !== undefined,
    );
    const api = createOperationsApi(apiBaseUrl, session);
    apiRef.current = api;
    setConnection("Conectando");
    const controller = new AbortController();
    abortRef.current = controller;
    try {
      const [dashboard, availableContexts] = await Promise.all([
        api.list({}, controller.signal),
        api.organizationContexts(controller.signal),
      ]);
      if (sessionRef.current !== session) return;
      setItems(dashboard.items);
      itemsRef.current = dashboard.items;
      setNextCursor(dashboard.next_cursor);
      nextCursorRef.current = dashboard.next_cursor;
      setContexts(availableContexts);
      setLastUpdated(new Date(dashboard.generated_at));
      telemetry.lookupCompleted("rest");
      const realtime = createOperationsDashboardRealtime(apiBaseUrl, session, {
        refreshOperations: () => scheduleRefresh(250, "realtime"),
        refreshLocation: () => scheduleRefresh(500, "location"),
        resynchronize: async () => {
          if (readOperationsSession() !== session)
            throw new Error("Session changed.");
          await performLoad("replace", "reconnect");
          return {
            aggregate_versions: Object.fromEntries(
              itemsRef.current.map((item) => [
                item.order_id,
                item.aggregate_version,
              ]),
            ),
          };
        },
        reconnecting: () => {
          setConnection("Reconectando");
          telemetry.realtimeStateChanged("reconnecting");
        },
        connected: () => {
          setConnection("Conectada");
          telemetry.realtimeStateChanged("connected");
        },
        unavailable: () => {
          setConnection("Sin conexión");
          telemetry.realtimeStateChanged("unavailable");
        },
      });
      connectionRef.current = realtime;
      await realtime.start();
      if (connectionRef.current === realtime) {
        setConnection("Conectada");
        telemetry.realtimeStateChanged("connected");
      }
    } catch (caught: unknown) {
      if (controller.signal.aborted) return;
      const category =
        caught instanceof OperationsApiError ? caught.category : "network";
      if (category === "unauthorized" || category === "forbidden") {
        setAccessUnavailable(true);
        setItems([]);
        itemsRef.current = [];
      } else {
        setError("Operaciones no está disponible.");
      }
      setConnection("Sin conexión");
    } finally {
      if (abortRef.current === controller) abortRef.current = null;
    }
  }, [
    apiBaseUrl,
    clearForSessionChange,
    performLoad,
    scheduleRefresh,
    telemetry,
  ]);

  useEffect(() => {
    const initial = window.setTimeout(() => void startSession(), 0);
    const unsubscribe = subscribeToOperationsSession(() => void startSession());
    return () => {
      clearTimeout(initial);
      unsubscribe();
    };
  }, [startSession]);

  useEffect(() => {
    const coalesced = () => void performLoad("replace", "coalesced");
    window.addEventListener("paquetenvia:operations-coalesced", coalesced);
    return () =>
      window.removeEventListener("paquetenvia:operations-coalesced", coalesced);
  }, [performLoad]);

  useEffect(() => {
    const onVisibility = () => {
      if (!document.hidden) void performLoad("replace", "visibility");
    };
    document.addEventListener("visibilitychange", onVisibility);
    const timer = window.setInterval(() => {
      if (!document.hidden && !accessUnavailable)
        void performLoad("replace", "polling");
    }, 30_000);
    return () => {
      document.removeEventListener("visibilitychange", onVisibility);
      clearInterval(timer);
    };
  }, [accessUnavailable, performLoad]);

  useEffect(
    () => () => {
      abortRef.current?.abort();
      clearTimers();
      void connectionRef.current?.stop();
    },
    [clearTimers],
  );

  const setFilters = useCallback(
    (next: OperationsDashboardFilters) => {
      telemetry.filterChanged(filterCategory(filtersRef.current, next));
      filtersRef.current = next;
      setFiltersState(next);
      seenCursorsRef.current.clear();
      setNextCursor(null);
      nextCursorRef.current = null;
      abortRef.current?.abort();
      void performLoad("replace", "filter");
    },
    [performLoad, telemetry],
  );

  const requestOrganizationChange = useCallback(async (organizationId: string) => {
    const session = sessionRef.current;
    if (session?.requestOrganizationChange === undefined) return;
    await session.requestOrganizationChange(organizationId);
  }, []);

  const activeOrganizationName =
    contexts.find(
      (context) =>
        context.organization_id === activeOrganizationId,
    )?.display_name ?? "Organización activa";

  return {
    items,
    contexts,
    activeOrganizationName,
    nextCursor,
    loading,
    loadingMore,
    accessUnavailable,
    error,
    connection,
    lastUpdated,
    filters,
    setFilters,
    refresh: () => {
      seenCursorsRef.current.clear();
      void performLoad("replace", "manual");
    },
    loadMore: () => void performLoad("append", "pagination"),
    requestOrganizationChange,
    canChangeOrganization,
  };
}

export function mergeOperationsOrders(
  current: readonly OperationsDashboardOrder[],
  next: readonly OperationsDashboardOrder[],
): readonly OperationsDashboardOrder[] {
  const map = new Map(current.map((item) => [item.order_id, item]));
  for (const item of next) {
    const prior = map.get(item.order_id);
    if (prior === undefined || item.aggregate_version >= prior.aggregate_version)
      map.set(item.order_id, item);
  }
  return [...map.values()].sort(
    (left, right) =>
      Date.parse(right.updated_at) - Date.parse(left.updated_at) ||
      right.order_id.localeCompare(left.order_id),
  );
}

function filterCategory(
  previous: OperationsDashboardFilters,
  next: OperationsDashboardFilters,
): string {
  const keys = Object.keys(next) as (keyof OperationsDashboardFilters)[];
  return keys.find((key) => previous[key] !== next[key]) ?? "clear";
}

function windowOrigin(): string {
  return typeof window === "undefined" ? "http://127.0.0.1" : window.location.origin;
}
