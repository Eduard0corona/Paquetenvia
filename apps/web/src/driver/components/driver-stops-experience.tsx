"use client";

/* eslint-disable @next/next/no-html-link-for-pages -- document navigation lets the Service Worker resolve offline shells */

import { useEffect, useMemo, useRef, useState } from "react";
import { resolveDriverApiBaseUrl } from "../api/api-base-url";
import { createDriverStopsApi } from "../api/driver-stops-api";
import { IndexedDbDriverStopsCache } from "../cache/driver-stops-cache";
import {
  driverStopStatusLabel,
  driverStopTypeLabel,
} from "../contracts/labels";
import { defaultDriverStopsRealtimeFactory } from "../realtime/driver-stops-realtime";
import {
  findDriverStopForRoute,
  parseDriverStopsPathname,
  type DriverStopsRoute,
} from "../routing/driver-stops-route";
import {
  driverSessionChangedEvent,
  readBrowserDriverSession,
  sessionIdentity,
  type DriverSession,
} from "../session/driver-session";
import {
  DriverStopsController,
  type DriverStopsViewState,
} from "../state/driver-stops-controller";
import { disabledDriverStopsTelemetry } from "../telemetry/driver-stops-telemetry";
import styles from "./driver-stops.module.css";

const unavailableState: DriverStopsViewState = Object.freeze({
  phase: "session-unavailable",
  stops: [],
  synchronizedAt: null,
  realtime: "offline",
  refreshing: false,
});

export function DriverStopsExperience() {
  const [session, setSession] = useState<DriverSession | null>(null);
  const [state, setState] = useState<DriverStopsViewState>(unavailableState);
  const [route, setRoute] = useState<DriverStopsRoute | null>(null);
  const controllerRef = useRef<DriverStopsController | null>(null);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const identity = sessionIdentity(session);

  useEffect(() => {
    const refreshSession = () => setSession(readBrowserDriverSession());
    refreshSession();
    window.addEventListener(driverSessionChangedEvent, refreshSession);
    return () =>
      window.removeEventListener(driverSessionChangedEvent, refreshSession);
  }, []);

  useEffect(() => {
    const refreshRoute = () =>
      setRoute(parseDriverStopsPathname(window.location.pathname));
    refreshRoute();
    window.addEventListener("popstate", refreshRoute);
    return () => window.removeEventListener("popstate", refreshRoute);
  }, []);

  useEffect(() => {
    let active = true;
    if (!session) {
      return;
    }

    const baseUrl = resolveDriverApiBaseUrl(
      window.location.origin,
      process.env.NEXT_PUBLIC_API_BASE_URL,
      process.env.NODE_ENV,
    );
    const controller = new DriverStopsController({
      baseUrl,
      session,
      api: createDriverStopsApi({ baseUrl, session }),
      cache: new IndexedDbDriverStopsCache(),
      realtimeFactory: defaultDriverStopsRealtimeFactory,
      telemetry: disabledDriverStopsTelemetry,
    });
    controllerRef.current = controller;
    const unsubscribe = controller.subscribe((next) => {
      if (active) setState(next);
    });
    void controller.start();

    return () => {
      active = false;
      unsubscribe();
      controllerRef.current = null;
      void controller.dispose();
    };
  }, [identity, session]);

  useEffect(() => {
    if (route && route.kind !== "list" && state.phase !== "loading") {
      headingRef.current?.focus({ preventScroll: false });
    }
  }, [route, state.phase]);

  const stop = useMemo(
    () => findDriverStopForRoute(route, state.stops),
    [route, state.stops],
  );

  const retry = () => void controllerRef.current?.retry();

  if (!route || !session || state.phase === "session-unavailable") {
    return (
      <DriverShell>
        <StatusPanel title="Sesión no disponible">
          Inicia una sesión válida y selecciona una organización para consultar tus
          paradas.
        </StatusPanel>
      </DriverShell>
    );
  }

  if (state.phase === "loading") {
    return (
      <DriverShell busy>
        <section className={styles.loading} aria-label="Cargando paradas">
          <span className={styles.skeleton} />
          <span className={styles.skeleton} />
          <span className={styles.skeleton} />
        </section>
      </DriverShell>
    );
  }

  if (state.phase === "unauthorized") {
    return (
      <DriverShell>
        <StatusPanel title="Sesión no válida">
          Tu sesión ya no permite consultar paradas. Vuelve a autenticarte.
        </StatusPanel>
      </DriverShell>
    );
  }

  if (state.phase === "forbidden") {
    return (
      <DriverShell>
        <StatusPanel title="Acceso no disponible">
          No tienes acceso a las paradas de repartidor en esta organización.
        </StatusPanel>
      </DriverShell>
    );
  }

  if (state.phase === "invalid-contract") {
    return (
      <DriverShell>
        <StatusPanel title="Información no disponible">
          La respuesta no pudo validarse de forma segura.
          <RetryButton onClick={retry} />
        </StatusPanel>
      </DriverShell>
    );
  }

  if (state.phase === "recoverable-error") {
    return (
      <DriverShell>
        <StatusPanel title="No pudimos actualizar">
          Intenta nuevamente en unos momentos.
          <RetryButton onClick={retry} />
        </StatusPanel>
      </DriverShell>
    );
  }

  const dataIsOffline =
    state.phase === "offline" || state.phase === "offline-empty";

  return (
    <DriverShell busy={state.refreshing}>
      <ConnectionSummary
        state={state}
        offline={dataIsOffline || state.realtime === "offline"}
      />
      {route.kind === "detail" ? (
        <StopDetail
          orderId={route.orderId}
          stop={stop}
          headingRef={headingRef}
          synchronizedAt={state.synchronizedAt}
          offline={dataIsOffline}
        />
      ) : route.kind === "list" ? (
        <StopList state={state} retry={retry} />
      ) : (
        <StopDetail
          orderId={null}
          stop={undefined}
          headingRef={headingRef}
          synchronizedAt={state.synchronizedAt}
          offline={dataIsOffline}
        />
      )}
    </DriverShell>
  );
}

function DriverShell({
  children,
  busy = false,
}: Readonly<{ children: React.ReactNode; busy?: boolean }>) {
  return (
    <main className={styles.shell} aria-busy={busy}>
      <header className={styles.header}>
        <p className={styles.eyebrow}>Paquetenvia Repartidor</p>
        <h1>Mis paradas</h1>
        <p className={styles.intro}>
          Consulta las recolecciones y entregas que tienes asignadas.
        </p>
      </header>
      {children}
    </main>
  );
}

function ConnectionSummary({
  state,
  offline,
}: Readonly<{ state: DriverStopsViewState; offline: boolean }>) {
  const label = offline
    ? "Sin conexión"
    : state.realtime === "reconnecting"
      ? "Reconectando"
      : "Actualizado";
  return (
    <div
      className={`${styles.connection} ${offline ? styles.offline : ""}`}
      role={offline ? "status" : undefined}
      aria-live="polite"
    >
      <span aria-hidden="true" className={styles.connectionMark} />
      <span>{label}</span>
      {state.refreshing ? <span>Actualizando…</span> : null}
    </div>
  );
}

function StopList({
  state,
  retry,
}: Readonly<{ state: DriverStopsViewState; retry: () => void }>) {
  if (state.phase === "offline-empty") {
    return (
      <StatusPanel title="Sin información guardada">
        No hay información disponible sin conexión. Conéctate para actualizar tus
        paradas.
        <RetryButton onClick={retry} />
      </StatusPanel>
    );
  }
  if (state.phase === "empty") {
    return (
      <StatusPanel title="Sin paradas activas">
        No tienes paradas activas.
      </StatusPanel>
    );
  }

  return (
    <>
      {state.phase === "offline" ? (
        <OfflineBanner synchronizedAt={state.synchronizedAt} />
      ) : null}
      <ul className={styles.stopList} aria-label="Paradas asignadas">
        {state.stops.map((stop) => (
          <li className={styles.stopCard} key={stop.order_id}>
            <div className={styles.cardHeading}>
              <div>
                <p className={styles.cardLabel}>Orden</p>
                <h2>{stop.order_public_id}</h2>
              </div>
              <span className={styles.badge}>
                {driverStopStatusLabel(stop.status)}
              </span>
            </div>
            <dl className={styles.stopFacts}>
              <div>
                <dt>Tipo de parada</dt>
                <dd>{driverStopTypeLabel(stop.stop_type)}</dd>
              </div>
              <div>
                <dt>Dirección</dt>
                <dd>{stop.address_summary}</dd>
              </div>
              <div>
                <dt>Sincronización</dt>
                <dd>{state.phase === "offline" ? "Información guardada" : "Actualizada"}</dd>
              </div>
            </dl>
            <a
              className={styles.primaryLink}
              href={`/driver/stops/${stop.order_id}`}
            >
              Ver detalle
            </a>
          </li>
        ))}
      </ul>
    </>
  );
}

function StopDetail({
  orderId,
  stop,
  headingRef,
  synchronizedAt,
  offline,
}: Readonly<{
  orderId: string | null;
  stop: DriverStopsViewState["stops"][number] | undefined;
  headingRef: React.RefObject<HTMLHeadingElement | null>;
  synchronizedAt: string | null;
  offline: boolean;
}>) {
  if (!stop || stop.order_id !== orderId) {
    return (
      <section className={styles.detail}>
        <h2 ref={headingRef} tabIndex={-1}>
          Parada no disponible
        </h2>
        <p>
          La parada no existe o ya no está visible en tu organización actual.
        </p>
        <a className={styles.secondaryLink} href="/driver/stops">
          Volver a mis paradas
        </a>
      </section>
    );
  }

  return (
    <>
      {offline ? <OfflineBanner synchronizedAt={synchronizedAt} /> : null}
      <article className={styles.detail}>
        <p className={styles.cardLabel}>Detalle de parada</p>
        <h2 ref={headingRef} tabIndex={-1}>
          {stop.order_public_id}
        </h2>
        <dl className={styles.detailFacts}>
          <div>
            <dt>Tipo de parada</dt>
            <dd>{driverStopTypeLabel(stop.stop_type)}</dd>
          </div>
          <div>
            <dt>Estado</dt>
            <dd>
              <span className={styles.badge}>
                {driverStopStatusLabel(stop.status)}
              </span>
            </dd>
          </div>
          <div>
            <dt>Dirección</dt>
            <dd>{stop.address_summary}</dd>
          </div>
          <div>
            <dt>Última sincronización</dt>
            <dd>{formatTimestamp(synchronizedAt)}</dd>
          </div>
          <div>
            <dt>Conectividad</dt>
            <dd>{offline ? "Sin conexión" : "En línea"}</dd>
          </div>
        </dl>
        <a className={styles.secondaryLink} href="/driver/stops">
          Volver a mis paradas
        </a>
      </article>
    </>
  );
}

function OfflineBanner({
  synchronizedAt,
}: Readonly<{ synchronizedAt: string | null }>) {
  return (
    <aside className={styles.offlineBanner} role="status" aria-live="polite">
      <strong>Sin conexión</strong>
      <span>Mostrando última información disponible.</span>
      <span>Última sincronización: {formatTimestamp(synchronizedAt)}</span>
    </aside>
  );
}

function StatusPanel({
  title,
  children,
}: Readonly<{ title: string; children: React.ReactNode }>) {
  return (
    <section className={styles.statusPanel} role="status">
      <h2>{title}</h2>
      <div>{children}</div>
    </section>
  );
}

function RetryButton({ onClick }: Readonly<{ onClick: () => void }>) {
  return (
    <button className={styles.retryButton} type="button" onClick={onClick}>
      Reintentar
    </button>
  );
}

function formatTimestamp(value: string | null): string {
  if (!value) return "No disponible";
  return new Intl.DateTimeFormat("es-MX", {
    dateStyle: "medium",
    timeStyle: "short",
    timeZone: "America/Mazatlan",
  }).format(new Date(value));
}
