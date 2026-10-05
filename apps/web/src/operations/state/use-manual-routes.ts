"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { realtimeCredentials } from "@/auth/request-credentials";
import type { ManagedRealtimeConnection } from "@/realtime/base-connection";
import { createOperationsConnection } from "@/realtime/operations-connection";
import type { Uuid } from "@/realtime/envelope";
import { createRoutesApi, RoutesApiError, type CreateRouteInput, type RoutesApi } from "../api/routes-api";
import type { ManualRoute, ManualRouteDetail } from "../contracts/manual-route";
import { readOperationsSession, subscribeToOperationsSession, type OperationsSession } from "../session/operations-session";

export interface ManualRoutesState {
  readonly routes: readonly ManualRoute[];
  readonly selected: ManualRouteDetail | null;
  readonly loading: boolean;
  readonly mutating: boolean;
  readonly message: string | null;
  readonly connected: boolean;
  select(routeId: string): Promise<void>;
  refresh(): Promise<void>;
  create(input: CreateRouteInput): Promise<void>;
  addStop(orderId: string): Promise<void>;
  removeStop(stopId: string): Promise<void>;
  reorder(stopIds: readonly string[]): Promise<void>;
}

export function useManualRoutes(): ManualRoutesState {
  const [routes, setRoutes] = useState<readonly ManualRoute[]>([]);
  const [selected, setSelected] = useState<ManualRouteDetail | null>(null);
  const [loading, setLoading] = useState(false);
  const [mutating, setMutating] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [connected, setConnected] = useState(false);
  const apiRef = useRef<RoutesApi | null>(null);
  const sessionRef = useRef<OperationsSession | null>(null);
  const connectionRef = useRef<ManagedRealtimeConnection | null>(null);
  const selectedRef = useRef<ManualRouteDetail | null>(null);

  const loadDetail = useCallback(async (routeId: string): Promise<ManualRouteDetail> => {
    const api = apiRef.current;
    if (api === null) throw new RoutesApiError("unauthorized");
    const detail = await api.get(routeId);
    selectedRef.current = detail;
    setSelected(detail);
    return detail;
  }, []);

  const loadAll = useCallback(async (): Promise<readonly ManualRoute[]> => {
    const api = apiRef.current;
    if (api === null) return [];
    const page = await api.list();
    setRoutes(page.items);
    return page.items;
  }, []);

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      await Promise.all([loadAll(), selectedRef.current ? loadDetail(selectedRef.current.id) : Promise.resolve(null)]);
      setMessage(null);
    } catch (error) { setMessage(labelError(error)); }
    finally { setLoading(false); }
  }, [loadAll, loadDetail]);

  const mutate = useCallback(async (action: (api: RoutesApi) => Promise<ManualRouteDetail | ManualRoute>) => {
    const api = apiRef.current;
    if (api === null) { setMessage("Inicia una sesión de Operaciones."); return; }
    setMutating(true);
    setMessage(null);
    try {
      const result = await action(api);
      await loadAll();
      if ("stops" in result) {
        selectedRef.current = result;
        setSelected(result);
      } else {
        await loadDetail(result.id);
      }
      setMessage("Cambio guardado.");
    } catch (error) {
      if (error instanceof RoutesApiError && error.category === "conflict" && selectedRef.current)
        await loadDetail(selectedRef.current.id).catch(() => undefined);
      setMessage(labelError(error));
    } finally { setMutating(false); }
  }, [loadAll, loadDetail]);

  useEffect(() => {
    let cancelled = false;
    const start = async () => {
      const previous = connectionRef.current;
      connectionRef.current = null;
      if (previous) await previous.stop().catch(() => undefined);
      const session = readOperationsSession();
      sessionRef.current = session;
      selectedRef.current = null;
      setSelected(null);
      setRoutes([]);
      setConnected(false);
      if (session === null || cancelled) { apiRef.current = null; return; }
      const baseUrl = process.env.NEXT_PUBLIC_API_BASE_URL ?? window.location.origin;
      const api = createRoutesApi(baseUrl, session);
      apiRef.current = api;
      try {
        const page = await api.list();
        if (cancelled || sessionRef.current !== session) return;
        setRoutes(page.items);
        const search = new URL(window.location.href).searchParams;
        const queryRoute = search.get("routeId");
        if (queryRoute && page.items.some((route) => route.id === queryRoute)) {
          await loadDetail(queryRoute);
        }
        const queryOrder = search.get("orderId");
        if (queryOrder) setMessage("Selecciona una ruta para agregar la orden indicada.");
        const connection = createOperationsConnection({
          baseUrl,
          organizationId: session.organizationId as Uuid,
          ...realtimeCredentials(session),
          suppressLogging: true,
          onReconnecting: () => setConnected(false),
          onResynchronized: () => setConnected(true),
          onResynchronizationError: () => setMessage("No fue posible actualizar las rutas. Usa Actualizar para reintentar."),
          resynchronizeFromRest: async () => {
            const current = await api.list();
            setRoutes(current.items);
            if (selectedRef.current) await loadDetail(selectedRef.current.id);
            return { aggregate_versions: Object.fromEntries(current.items.map((route) => [route.id, route.version])) };
          },
        }, {
          RouteChanged: (event) => {
            void loadAll();
            if (selectedRef.current?.id === event.payload.route_id) void loadDetail(event.payload.route_id);
          },
        });
        connectionRef.current = connection;
        await connection.start();
        if (!cancelled) setConnected(true);
      } catch (error) { if (!cancelled) setMessage(labelError(error)); }
    };
    void start();
    const unsubscribe = subscribeToOperationsSession(() => void start());
    return () => {
      cancelled = true;
      unsubscribe();
      const connection = connectionRef.current;
      connectionRef.current = null;
      if (connection) void connection.stop().catch(() => undefined);
    };
  }, [loadAll, loadDetail]);

  return {
    routes, selected, loading, mutating, message, connected,
    select: async (routeId) => { setLoading(true); try { await loadDetail(routeId); setMessage(null); } catch (error) { setMessage(labelError(error)); } finally { setLoading(false); } },
    refresh,
    create: async (input) => mutate((api) => api.create(input, crypto.randomUUID())),
    addStop: async (orderId) => { const route = selectedRef.current; if (route) await mutate((api) => api.addStop(route.id, orderId, route.version, crypto.randomUUID())); },
    removeStop: async (stopId) => { const route = selectedRef.current; if (route) await mutate((api) => api.removeStop(route.id, stopId, route.version, crypto.randomUUID())); },
    reorder: async (stopIds) => { const route = selectedRef.current; if (route) await mutate((api) => api.reorder(route.id, stopIds, route.version, crypto.randomUUID())); },
  };
}

function labelError(error: unknown): string {
  if (error instanceof RoutesApiError && error.category === "conflict")
    return "La ruta cambió mientras la editabas. Se cargó la versión más reciente; revisa y vuelve a intentar.";
  if (error instanceof RoutesApiError && ["unauthorized", "forbidden"].includes(error.category))
    return "No tienes acceso a la planeación de rutas.";
  return "No fue posible completar la operación de rutas.";
}
