"use client";

import { clientApiBaseUrl } from "../../lib/api-base-url";
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
import {
  AuthoritativeRefreshCoordinator,
  RefreshScopeChangedError,
  type RefreshRequirement,
} from "./authoritative-refresh-coordinator";
import { ScreenReloads } from "./screen-reloads";
import { isRateLimited } from "./tenant-error-messages";
import {
  rateLimitWaits,
  sleepUntilWaitEnds,
  type RateLimitedOperation,
} from "../../lib/rate-limit-wait";

/** SEC-004: the order detail reads its dashboard row (Reporting.OperationsDashboard); getOrder is not limited. */
export const orderDetailRateLimit: readonly RateLimitedOperation[] = ["dashboard"];

interface OrderDetailRefreshResult {
  readonly detail: OperationsOrderDetail;
  readonly projection: OperationsDashboardOrder;
  readonly generatedAt: string;
}

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
  const apiBaseUrl = clientApiBaseUrl();
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
  // Owner answers Q9 (at most one realtime reload every 3 seconds), Q11 and Q14 (nothing is read during the
  // server's wait; the order is read once when it ends). Its reload is set from an effect below.
  const [reloads] = useState(() => new ScreenReloads<"realtime">({ operations: orderDetailRateLimit }));
  // A reconnect waiting out the server's wait stops waiting on a session change or unmount.
  const waitAbortRef = useRef(new AbortController());
  const refreshCoordinatorRef = useRef(
    new AuthoritativeRefreshCoordinator<OrderDetailRefreshResult>(),
  );
  const refreshScopeRef = useRef(0);
  const blockedRef = useRef(false);
  const load = useCallback(
    async (
      api: OperationsDashboardApi,
      session: OperationsSession,
      requirement: RefreshRequirement = "normal",
    ): Promise<OrderDetailRefreshResult | null> => {
      const scope = refreshScopeRef.current;
      // SEC-004: during the server's wait nothing is sent, whatever asked (polling, realtime, visibility,
      // "Actualizar", a change from the panels below); the order is read once, by itself, when it ends.
      if (requirement === "normal" && !reloads.admit("realtime")) return null;
      try {
        return await refreshCoordinatorRef.current.request({
          requirement,
          isCurrent: () =>
            refreshScopeRef.current === scope &&
            sessionRef.current === session &&
            apiRef.current === api,
          execute: async (signal) => {
            reloads.reloaded();
            setLoading(true);
            try {
              const [detail, page] = await Promise.all([
                api.getOrder(orderId, signal),
                api.list({ orderId }, signal),
              ]);
              if (page.items.length === 0)
                throw new OperationsApiError("not_found");
              if (page.items.length !== 1)
                throw new OperationsApiError("invalid");
              return {
                detail,
                projection: page.items[0],
                generatedAt: page.generated_at,
              };
            } finally {
              setLoading(false);
            }
          },
          apply: (result) => {
            setOrder(result.detail);
            setProjection(result.projection);
            setNotFound(false);
            setAccessUnavailable(false);
            setError(null);
            setLastUpdated(new Date(result.generatedAt));
          },
        });
      } catch (caught: unknown) {
        if (caught instanceof RefreshScopeChangedError) {
          if (requirement === "mandatory-reconnect") throw caught;
          return null;
        }
        const category =
          caught instanceof OperationsApiError ? caught.category : "unavailable";
        if (category === "not_found") {
          setNotFound(true);
          setOrder(null);
          setProjection(null);
          setLastUpdated(null);
        } else if (category === "unauthorized" || category === "forbidden") {
          blockedRef.current = true;
          setAccessUnavailable(true);
          setOrder(null);
          setProjection(null);
          setLastUpdated(null);
          if (requirement === "normal") {
            await connectionRef.current?.stop().catch(() => undefined);
            connectionRef.current = null;
          }
        } else if (isRateLimited(caught)) {
          // SEC-004: the common message counts down, the order stays, and it is read once when the wait ends.
          setError(null);
          reloads.rateLimited("realtime");
        } else {
          setError("No fue posible actualizar la orden.");
        }
        if (requirement === "mandatory-reconnect") throw caught;
        return null;
      }
    },
    [orderId, reloads],
  );

  // The gathered realtime reload, and the one reload when the server's wait ends.
  useEffect(() => {
    reloads.setReload(() => {
      const api = apiRef.current;
      const session = sessionRef.current;
      if (api !== null && session !== null) void load(api, session);
    });
  }, [load, reloads]);

  const start = useCallback(async () => {
    refreshScopeRef.current += 1;
    refreshCoordinatorRef.current.cancel();
    reloads.cancel();
    waitAbortRef.current.abort();
    waitAbortRef.current = new AbortController();
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
    // Owner answer Q9: the notices are gathered into at most one reload every 3 seconds, after the existing
    // debounces (250 ms for an order, 500 ms for a driver position; both read the order and its dashboard row).
    const realtime = createOperationsDashboardRealtime(apiBaseUrl, session, {
      refreshOperations: () => reloads.notice("realtime", 250),
      refreshLocation: () => reloads.notice("realtime", 500),
      resynchronize: async () => {
        if (readOperationsSession() !== session) throw new Error("Session changed.");
        // SEC-004: the mandatory read waits out the server's wait instead of meeting the same 429.
        await sleepUntilWaitEnds(rateLimitWaits, orderDetailRateLimit, waitAbortRef.current.signal);
        if (sessionRef.current !== session) throw new Error("Session changed.");
        const result = await load(api, session, "mandatory-reconnect");
        if (result === null) throw new Error("Session changed.");
        return {
          aggregate_versions: {
            [result.projection.order_id]: result.projection.aggregate_version,
          },
        };
      },
      reconnecting: () => {
        if (sessionRef.current === session)
          setConnectionState("Reconectando");
      },
      connected: () => {
        if (sessionRef.current === session) setConnectionState("Conectada");
      },
      unavailable: () => {
        if (sessionRef.current === session)
          setConnectionState("Sin conexión");
      },
    });
    connectionRef.current = realtime;
    try {
      await realtime.start();
      if (connectionRef.current === realtime) setConnectionState("Conectada");
    } catch {
      setConnectionState("Sin conexión");
    }
  }, [apiBaseUrl, load, reloads]);

  useEffect(() => {
    const initial = window.setTimeout(() => void start(), 0);
    const unsubscribe = subscribeToOperationsSession(() => void start());
    return () => {
      clearTimeout(initial);
      unsubscribe();
    };
  }, [start]);

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
      refreshCoordinatorRef.current.cancel();
      reloads.cancel();
      waitAbortRef.current.abort();
      void connectionRef.current?.stop();
    },
    [reloads],
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
