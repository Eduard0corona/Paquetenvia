"use client";

import { clientApiBaseUrl } from "../../lib/api-base-url";
import { useCallback, useEffect, useRef, useState } from "react";
import type { ManagedRealtimeConnection } from "@/realtime/base-connection";
import type {
  OperationsDashboardFilters,
  OperationsDashboardOrder,
  OperationsDashboardResponse,
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
import type { OperationsQueueCounts } from "../contracts/queue-counts";
import {
  emptyQueueCountsView,
  QueueCountsLoader,
  type QueueCountsView,
} from "./queue-counts-loader";
import {
  AuthoritativeRefreshCoordinator,
  RefreshScopeChangedError,
  type RefreshRequirement,
} from "./authoritative-refresh-coordinator";

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
  /** UI-PHASE2-QUEUE-COUNTS-2026-10-05: real server counts, never the loaded page. */
  readonly queueCounts: OperationsQueueCounts | null;
  readonly queueCountsUnavailable: boolean;
  setFilters(filters: OperationsDashboardFilters): void;
  refresh(): void;
  loadMore(): void;
  publishExternalOffer(
    orderId: string,
    commissionCents: number,
    expiresAt: string,
    vehicleType: "MOTORCYCLE" | "CAR" | "VAN" | "BICYCLE" | "WALKER",
    idempotencyKey: string,
  ): Promise<void>;
}

export function useOperationsDashboard(
  telemetry: OperationsDashboardTelemetry = noOpOperationsTelemetry,
): OperationsDashboardState {
  const apiBaseUrl = clientApiBaseUrl();
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
  const [queueCountsView, setQueueCountsView] =
    useState<QueueCountsView>(emptyQueueCountsView);
  const [queueCountsLoader] = useState(
    () => new QueueCountsLoader(setQueueCountsView),
  );
  const sessionRef = useRef<OperationsSession | null>(null);
  const apiRef = useRef<OperationsDashboardApi | null>(null);
  const connectionRef = useRef<ManagedRealtimeConnection | null>(null);
  const abortRef = useRef<AbortController | null>(null);
  const filtersRef = useRef(filters);
  const itemsRef = useRef(items);
  const refreshCoordinatorRef = useRef(
    new AuthoritativeRefreshCoordinator<OperationsDashboardResponse>(),
  );
  const refreshScopeRef = useRef(0);
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
    refreshScopeRef.current += 1;
    refreshCoordinatorRef.current.cancel();
    abortRef.current?.abort();
    abortRef.current = null;
    clearTimers();
    const current = connectionRef.current;
    connectionRef.current = null;
    if (current !== null) await current.stop().catch(() => undefined);
    queueCountsLoader.reset();
    setItems([]);
    itemsRef.current = [];
    setContexts([]);
    setNextCursor(null);
    nextCursorRef.current = null;
    seenCursorsRef.current.clear();
    setLastUpdated(null);
    setError(null);
    setAccessUnavailable(false);
  }, [clearTimers, queueCountsLoader]);

  // The counts are fetched again with every replacing list load except a
  // driver-position refresh, which never changes an order status or queue.
  const refreshQueueCounts = useCallback(() => {
    const api = apiRef.current;
    if (api === null) return;
    void queueCountsLoader.load((signal) => api.queueCounts(signal));
  }, [queueCountsLoader]);

  const performLoad = useCallback(
    async (
      mode: "replace" | "append",
      trigger: string,
      requirement: RefreshRequirement = "normal",
    ): Promise<OperationsDashboardResponse | null> => {
      const api = apiRef.current;
      const session = sessionRef.current;
      const scope = refreshScopeRef.current;
      if (
        api === null ||
        session === null ||
        (requirement === "normal" && document.hidden)
      )
        return null;

      try {
        return await refreshCoordinatorRef.current.request({
          requirement,
          isCurrent: () =>
            refreshScopeRef.current === scope &&
            sessionRef.current === session &&
            apiRef.current === api,
          execute: async (signal) => {
            telemetry.refreshTriggered(trigger);
            if (mode === "replace") setLoading(true);
            else setLoadingMore(true);
            try {
              const requestFilters =
                mode === "append" && nextCursorRef.current !== null
                  ? { ...filtersRef.current, cursor: nextCursorRef.current }
                  : { ...filtersRef.current, cursor: undefined };
              return await api.list(requestFilters, signal);
            } finally {
              if (mode === "replace") setLoading(false);
              else setLoadingMore(false);
            }
          },
          apply: (response) => {
            if (
              mode === "append" &&
              response.next_cursor !== null &&
              seenCursorsRef.current.has(response.next_cursor)
            ) {
              throw new Error("Cursor cycle.");
            }
            if (mode === "replace") seenCursorsRef.current.clear();
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
            if (mode === "replace" && trigger !== "location") refreshQueueCounts();
          },
        });
      } catch (caught: unknown) {
        if (caught instanceof RefreshScopeChangedError) {
          if (requirement === "mandatory-reconnect") throw caught;
          return null;
        }
        const category =
          caught instanceof OperationsApiError ? caught.category : "contract";
        telemetry.lookupFailed(category);
        if (category === "unauthorized" || category === "forbidden") {
          queueCountsLoader.reset();
          setItems([]);
          itemsRef.current = [];
          setNextCursor(null);
          nextCursorRef.current = null;
          seenCursorsRef.current.clear();
          setLastUpdated(null);
          setAccessUnavailable(true);
          setConnection("Sin conexión");
          if (requirement === "normal") {
            await connectionRef.current?.stop().catch(() => undefined);
            connectionRef.current = null;
          }
        } else {
          setError("No fue posible actualizar las operaciones.");
        }
        if (requirement === "mandatory-reconnect") throw caught;
        return null;
      }
    },
    [queueCountsLoader, refreshQueueCounts, telemetry],
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
      refreshQueueCounts();
      const realtime = createOperationsDashboardRealtime(apiBaseUrl, session, {
        refreshOperations: () => scheduleRefresh(250, "realtime"),
        refreshLocation: () => scheduleRefresh(500, "location"),
        resynchronize: async () => {
          if (readOperationsSession() !== session)
            throw new Error("Session changed.");
          const response = await performLoad(
            "replace",
            "reconnect",
            "mandatory-reconnect",
          );
          if (response === null) throw new Error("Session changed.");
          return {
            aggregate_versions: Object.fromEntries(
              response.items.map((item) => [
                item.order_id,
                item.aggregate_version,
              ]),
            ),
          };
        },
        reconnecting: () => {
          if (sessionRef.current !== session) return;
          setConnection("Reconectando");
          telemetry.realtimeStateChanged("reconnecting");
        },
        connected: () => {
          if (sessionRef.current !== session) return;
          setConnection("Conectada");
          telemetry.realtimeStateChanged("connected");
        },
        unavailable: () => {
          if (sessionRef.current !== session) return;
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
    refreshQueueCounts,
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
      refreshCoordinatorRef.current.cancel();
      abortRef.current?.abort();
      queueCountsLoader.reset();
      clearTimers();
      void connectionRef.current?.stop();
    },
    [clearTimers, queueCountsLoader],
  );

  const setFilters = useCallback(
    (next: OperationsDashboardFilters) => {
      telemetry.filterChanged(filterCategory(filtersRef.current, next));
      filtersRef.current = next;
      setFiltersState(next);
      seenCursorsRef.current.clear();
      setNextCursor(null);
      nextCursorRef.current = null;
      refreshScopeRef.current += 1;
      refreshCoordinatorRef.current.cancel();
      void performLoad("replace", "filter");
    },
    [performLoad, telemetry],
  );

  const publishExternalOffer = useCallback(
    async (
      orderId: string,
      commissionCents: number,
      expiresAt: string,
      vehicleType: "MOTORCYCLE" | "CAR" | "VAN" | "BICYCLE" | "WALKER",
      idempotencyKey: string,
    ) => {
      const api = apiRef.current;
      if (api === null) throw new OperationsApiError("unauthorized");
      await api.publishExternalOffer(
        { orderId, commissionCents, expiresAt, vehicleType },
        idempotencyKey,
      );
      seenCursorsRef.current.clear();
      await performLoad("replace", "manual");
    },
    [performLoad],
  );

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
    queueCounts: queueCountsView.counts,
    queueCountsUnavailable: queueCountsView.unavailable,
    setFilters,
    refresh: () => {
      seenCursorsRef.current.clear();
      void performLoad("replace", "manual");
    },
    loadMore: () => void performLoad("append", "pagination"),
    publishExternalOffer,
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
