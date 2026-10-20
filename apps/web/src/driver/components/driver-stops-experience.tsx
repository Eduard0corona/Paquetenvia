"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { isBffAuthenticationEnabled } from "../../auth/auth-mode";
import { buildSignInPromptHref } from "../../auth/bff-session";
import {
  isSessionBootstrapSettled,
  sessionBootstrapSettledEvent,
} from "../../auth/bootstrap-state";
import { resolveDriverApiBaseUrl } from "../api/api-base-url";
import {
  createDriverStopsApi,
  DriverStopsApiError,
} from "../api/driver-stops-api";
import { createExternalOffersApi } from "../api/external-offers-api";
import { createDriverDispatchContactApi } from "../api/dispatch-contact-api";
import { createDriverOrganizationRolesApi } from "../api/organization-roles-api";
import { createDriverVoiceApi, type DriverVoiceApi } from "../api/voice-api";
import { IndexedDbDriverDispatchContactCache } from "../cache/driver-dispatch-contact-cache";
import { IndexedDbDriverOrganizationRolesCache } from "../cache/driver-roles-cache";
import { IndexedDbDriverStopsCache } from "../cache/driver-stops-cache";
import type { DriverFailedDeliveryReason } from "../contracts/failed-delivery";
import {
  driverReportRoleOf,
  type DriverOrganizationRolesState,
  type DriverReportRole,
} from "../contracts/organization-roles";
import {
  driverConfirmedStatusLabel,
  driverOperationLabel,
  driverStopStatusLabel,
  driverStopTypeLabel,
} from "../contracts/labels";
import { completedTodayAt } from "../contracts/stop-details";
import {
  createDriverSyncApi,
  DriverSyncApiError,
} from "../offline/driver-sync-api";
import {
  driverFailedReportAttention,
  driverFailedReportHoldState,
} from "../offline/failed-delivery-policy";
import {
  driverOperationApplies,
  nextDriverOperationKind,
  projectDriverOperations,
  type ProjectedDriverStop,
} from "../offline/operation-projection";
import {
  driverOperationDefinitions,
  type DriverOfflineOperation,
  type DriverOperationKind,
  type DriverOperationalStatus,
} from "../offline/operation-contract";
import { driverRejectionMessage } from "../offline/rejection-codes";
import { defaultDriverStopsRealtimeFactory } from "../realtime/driver-stops-realtime";
import {
  driverStopsRouteChangedEvent,
  isInAppNavigationClick,
  navigateWithinDriverStops,
  parseDriverStopsPathname,
  type DriverStopsRoute,
} from "../routing/driver-stops-route";
import {
  driverLogoutStartedEvent,
  driverSessionChangedEvent,
  driverSessionCheckRequestedEvent,
  readBrowserDriverSession,
  sessionIdentity,
  type DriverSession,
} from "../session/driver-session";
import {
  forgetDriverSessionMarker,
  readDriverSessionMarker,
} from "../session/driver-session-marker";
import {
  DriverDispatchContactController,
  type DriverDispatchContactState,
} from "../state/driver-dispatch-contact-controller";
import {
  DriverOperationsController,
  driverMessageCallsDispatch,
  type DriverFailedDeliveryEnqueueResult,
  type DriverOperationsState,
} from "../state/driver-operations-controller";
import { DriverRolesController } from "../state/driver-roles-controller";
import {
  DriverStopsController,
  type DriverStopsViewState,
} from "../state/driver-stops-controller";
import {
  ExternalOffersController,
  type ExternalOffersState,
} from "../state/external-offers-controller";
import { disabledDriverStopsTelemetry } from "../telemetry/driver-stops-telemetry";
import { formatMxnCents } from "../../operations/contracts/money";
import { useConfirmDialog } from "../../components/confirm-dialog";
import { DriverAccount } from "./driver-account";
import { DriverCodCollection } from "./driver-cod-collection";
import { DriverDispatchCall } from "./driver-dispatch-call";
import { DriverFailedDelivery } from "./driver-failed-delivery";
import { DriverProofCapture } from "./driver-proof-capture";
import {
  NavigationLink,
  StopDetailFacts,
  StopHighlights,
  StopLead,
  StopsSummary,
  stopAddressText,
  stopRowLabel,
} from "./driver-stop-details";
import { RecipientCall } from "./recipient-call";
import styles from "./driver-stops.module.css";

const unavailableState: DriverStopsViewState = Object.freeze({
  phase: "session-unavailable",
  stops: [],
  synchronizedAt: null,
  realtime: "offline",
  refreshing: false,
  completedToday: null,
});
const unavailableOperationsState: DriverOperationsState = Object.freeze({
  operations: [],
  loading: true,
  mutating: false,
  message: null,
});
const unavailableExternalOffersState: ExternalOffersState = Object.freeze({
  offers: [],
  loading: true,
  pendingOfferId: null,
  message: null,
});
// DRV-SESSION-EXPIRY-OFFLINE-2026-10-19: external offers are online only, so
// a remembered session shows none and asks for none.
const noExternalOffersState: ExternalOffersState = Object.freeze({
  offers: [],
  loading: false,
  pendingOfferId: null,
  message: null,
});
// ORG-MY-ROLES-2026-10-12: until the roles controller answers, the roles are
// loading: the report is not offered and nothing is said about the role.
const loadingRolesState: DriverOrganizationRolesState = Object.freeze({
  roles: null,
  loading: true,
});
// DRV-DISPATCH-CONTACT-2026-10-12: no number is known until the controller
// answers, so no "Llamar a despacho" is shown.
const noDispatchContact: DriverDispatchContactState = Object.freeze({
  phoneE164: null,
});
/** The "Mis paradas" title, focused after an in-app move back to the list. */
const driverStopsTitleId = "driver-stops-title";
const stopUnavailableForReport: DriverFailedDeliveryEnqueueResult =
  Object.freeze({
    stored: false,
    message: "La parada no está disponible. Vuelve a abrirla desde tus paradas.",
  });

export function DriverStopsExperience() {
  const [session, setSession] = useState<DriverSession | null>(null);
  // DRV-SESSION-EXPIRY-OFFLINE-2026-10-19 (E2): the page's session check found
  // no session while the phone still remembers its driver, so that session
  // ended (401) and the same driver is asked to sign in again.
  const [signInAgain, setSignInAgain] = useState(false);
  const [state, setState] = useState<DriverStopsViewState>(unavailableState);
  const [operationsState, setOperationsState] = useState<DriverOperationsState>(
    unavailableOperationsState,
  );
  const [externalOffersState, setExternalOffersState] =
    useState<ExternalOffersState>(unavailableExternalOffersState);
  // ORG-MY-ROLES-2026-10-12: the person's own roles in the session's
  // organization (getMyOrganizationRoles, saved per partition) and whether a
  // request for them is on its way.
  const [rolesState, setRolesState] = useState<DriverOrganizationRolesState>(
    loadingRolesState,
  );
  // DRV-DISPATCH-CONTACT-2026-10-12: the dispatch number of the session's
  // organization (getMyDispatchContact, saved per partition), or null.
  const [dispatchContactState, setDispatchContactState] =
    useState<DriverDispatchContactState>(noDispatchContact);
  const [route, setRoute] = useState<DriverStopsRoute | null>(null);
  const controllerRef = useRef<DriverStopsController | null>(null);
  const operationsControllerRef = useRef<DriverOperationsController | null>(
    null,
  );
  const externalOffersControllerRef = useRef<ExternalOffersController | null>(
    null,
  );
  const rolesControllerRef = useRef<DriverRolesController | null>(null);
  const dispatchContactControllerRef =
    useRef<DriverDispatchContactController | null>(null);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const listFocusPendingRef = useRef(false);
  const identity = sessionIdentity(session);
  // DRV-SESSION-EXPIRY-OFFLINE-2026-10-19 (E3): a session the phone remembered
  // while the server could not be asked; read and queue locally only.
  const remembered = session?.remembered === true;
  // VOICE-001: online-only calls; never part of the offline queue.
  const voiceApi = useMemo<DriverVoiceApi | null>(
    () =>
      session && session.remembered !== true
        ? createDriverVoiceApi(
            resolveDriverApiBaseUrl(
              window.location.origin,
              process.env.NEXT_PUBLIC_API_BASE_URL,
              process.env.NODE_ENV,
            ),
            session,
          )
        : null,
    [session],
  );

  useEffect(() => {
    const refreshSession = () => {
      const current = readBrowserDriverSession();
      setSession(current);
      setSignInAgain(
        current === null &&
          isBffAuthenticationEnabled(process.env.NEXT_PUBLIC_AUTH_MODE) &&
          isSessionBootstrapSettled() &&
          readDriverSessionMarker(window) !== null,
      );
    };
    refreshSession();
    window.addEventListener(driverSessionChangedEvent, refreshSession);
    window.addEventListener(sessionBootstrapSettledEvent, refreshSession);
    return () => {
      window.removeEventListener(driverSessionChangedEvent, refreshSession);
      window.removeEventListener(sessionBootstrapSettledEvent, refreshSession);
    };
  }, []);

  useEffect(() => {
    // UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: "Cerrar sesión" wipes the saved
    // stop details; every controller stops first so no refresh writes them back.
    const stopControllers = () => {
      void controllerRef.current?.dispose();
      void operationsControllerRef.current?.dispose();
      externalOffersControllerRef.current?.dispose();
      rolesControllerRef.current?.dispose();
      dispatchContactControllerRef.current?.dispose();
    };
    window.addEventListener(driverLogoutStartedEvent, stopControllers);
    return () =>
      window.removeEventListener(driverLogoutStartedEvent, stopControllers);
  }, []);

  useEffect(() => {
    const readRoute = () =>
      setRoute(parseDriverStopsPathname(window.location.pathname));
    // DRV-PWA-INAPP-NAVIGATION: an in-app move or the browser's back and
    // forward. As a page load did, the move reads the stops again (offline the
    // read falls back to the saved snapshot and says so) and the list takes
    // the focus once.
    const refreshRoute = () => {
      listFocusPendingRef.current = true;
      readRoute();
      void controllerRef.current?.retry();
    };
    readRoute();
    window.addEventListener("popstate", refreshRoute);
    window.addEventListener(driverStopsRouteChangedEvent, refreshRoute);
    return () => {
      window.removeEventListener("popstate", refreshRoute);
      window.removeEventListener(driverStopsRouteChangedEvent, refreshRoute);
    };
  }, []);

  useEffect(() => {
    let active = true;
    if (!session) return;

    const baseUrl = resolveDriverApiBaseUrl(
      window.location.origin,
      process.env.NEXT_PUBLIC_API_BASE_URL,
      process.env.NODE_ENV,
    );
    const stopsApi = createDriverStopsApi({ baseUrl, session });
    const externalOffersController = new ExternalOffersController(
      createExternalOffersApi(baseUrl, session),
    );
    // ORG-MY-ROLES-2026-10-12: asked once per session start (a new sign-in or
    // organization is a new session); the answer saved for the partition keeps
    // "No se pudo entregar" working offline.
    const rolesController = new DriverRolesController({
      session,
      api: createDriverOrganizationRolesApi({ baseUrl, session }),
      cache: new IndexedDbDriverOrganizationRolesCache(),
    });
    // DRV-DISPATCH-CONTACT-2026-10-12: asked once per session start too; the
    // answer saved for the partition keeps "Llamar a despacho" working offline.
    const dispatchContactController = new DriverDispatchContactController({
      session,
      api: createDriverDispatchContactApi({ baseUrl, session }),
      cache: new IndexedDbDriverDispatchContactCache(),
    });
    const controller = new DriverStopsController({
      baseUrl,
      session,
      api: stopsApi,
      cache: new IndexedDbDriverStopsCache(),
      realtimeFactory: defaultDriverStopsRealtimeFactory,
      telemetry: disabledDriverStopsTelemetry,
      refreshExternalOffersFromSignal: () =>
        externalOffersController.scheduleRefresh(),
      resynchronizeExternalOffersFromRest: () =>
        externalOffersController.refreshForReconnect(),
    });
    const operationsController = new DriverOperationsController({
      session,
      api: createDriverSyncApi({
        baseUrl,
        session,
        production: process.env.NODE_ENV === "production",
      }),
      refreshStops: async (signal) => {
        try {
          return await controller.refreshForSync(signal);
        } catch (error) {
          if (error instanceof DriverStopsApiError) {
            const category =
              error.category === "session-ended" ||
              error.category === "unauthorized" ||
              error.category === "forbidden" ||
              error.category === "cancelled"
                ? error.category
                : error.category === "recoverable"
                  ? "recoverable"
                  : "invalid-contract";
            throw new DriverSyncApiError(category);
          }
          throw error;
        }
      },
      onAccessRevoked: (category) => void controller.revokeAccess(category),
      reportRole: () => driverReportRoleOf(rolesController.current),
      // DRV-SESSION-EXPIRY-OFFLINE-2026-10-19 (E3): nothing is sent until the
      // server confirms this driver and organization; the confirmed session
      // gets its own controller.
      sync: session.remembered !== true,
    });
    controllerRef.current = controller;
    operationsControllerRef.current = operationsController;
    externalOffersControllerRef.current = externalOffersController;
    rolesControllerRef.current = rolesController;
    dispatchContactControllerRef.current = dispatchContactController;
    const unsubscribe = controller.subscribe((next) => {
      if (active) setState(next);
      // DRV-SESSION-EXPIRY-OFFLINE-2026-10-19 (E3): a 403 or a session without
      // a usable credential wiped this session's partition, so the phone stops
      // remembering it; a 401 ("session-ended") keeps it (E2).
      if (next.phase === "forbidden" || next.phase === "unauthorized") {
        forgetDriverSessionMarker(
          window,
          session.cacheNamespace,
          session.organizationId,
        );
      }
    });
    const unsubscribeRoles = rolesController.subscribe((next) => {
      if (active) setRolesState(next);
    });
    const unsubscribeDispatchContact = dispatchContactController.subscribe(
      (next) => {
        if (active) setDispatchContactState(next);
      },
    );
    const unsubscribeOperations = operationsController.subscribe((next) => {
      if (active) setOperationsState(next);
    });
    const unsubscribeExternalOffers = externalOffersController.subscribe(
      (next) => {
        if (active) setExternalOffersState(next);
      },
    );
    void controller.start();
    void operationsController.start();
    if (session.remembered !== true) void externalOffersController.start();
    void rolesController.start();
    void dispatchContactController.start();

    return () => {
      active = false;
      unsubscribe();
      unsubscribeOperations();
      unsubscribeExternalOffers();
      unsubscribeRoles();
      unsubscribeDispatchContact();
      controllerRef.current = null;
      operationsControllerRef.current = null;
      externalOffersControllerRef.current = null;
      rolesControllerRef.current = null;
      dispatchContactControllerRef.current = null;
      externalOffersController.dispose();
      rolesController.dispose();
      dispatchContactController.dispose();
      void operationsController.dispose();
      void controller.dispose();
    };
  }, [identity, session]);

  useEffect(() => {
    if (route && route.kind !== "list" && state.phase !== "loading") {
      headingRef.current?.focus({ preventScroll: false });
    }
  }, [route, state.phase]);

  useEffect(() => {
    if (route?.kind !== "list" || !listFocusPendingRef.current) return;
    listFocusPendingRef.current = false;
    document.getElementById(driverStopsTitleId)?.focus({ preventScroll: true });
  }, [route]);

  const projection = useMemo(
    () => projectDriverOperations(state.stops, operationsState.operations),
    [state.stops, operationsState.operations],
  );
  const stop = useMemo(() => {
    if (!route || route.kind !== "detail") return undefined;
    return projection.stops.find(
      (candidate) => candidate.order_id === route.orderId,
    );
  }, [projection.stops, route]);

  // E3: while the session is only remembered, "Reintentar" and "Sincronizar
  // ahora" also ask the server to confirm it now.
  const requestSessionCheck = () => {
    if (remembered) window.dispatchEvent(new Event(driverSessionCheckRequestedEvent));
  };
  const retry = () => {
    requestSessionCheck();
    void controllerRef.current?.retry();
  };

  if (
    route &&
    ((!session && signInAgain) || (session && state.phase === "session-ended"))
  ) {
    // DRV-SESSION-EXPIRY-OFFLINE-2026-10-19 (owner answer E2 "Conservar y
    // pedir entrar (Recommended)"): nothing was wiped.
    return (
      <DriverShell>
        <StatusPanel title="Tu sesión venció, entra de nuevo">
          Lo que tengas pendiente sigue guardado en este teléfono y se enviará
          cuando vuelvas a entrar con tu cuenta.
          <SignInAction route={route} />
        </StatusPanel>
      </DriverShell>
    );
  }

  if (!route || !session || state.phase === "session-unavailable") {
    return (
      <DriverShell>
        <StatusPanel title="Sesión no disponible">
          Inicia una sesión válida y selecciona una organización para consultar
          tus paradas.
          <SignInAction route={route} />
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

  if (
    state.phase === "unauthorized" ||
    state.phase === "forbidden"
  ) {
    return (
      <DriverShell>
        <StatusPanel
          title={
            state.phase === "unauthorized"
              ? "Sesión no válida"
              : "Acceso no disponible"
          }
        >
          {state.phase === "unauthorized"
            ? "Tu sesión ya no permite consultar paradas. Vuelve a autenticarte."
            : "No tienes acceso a las paradas de repartidor en esta organización."}
          {state.phase === "unauthorized" ? <SignInAction route={route} /> : null}
        </StatusPanel>
      </DriverShell>
    );
  }

  if (
    state.phase === "invalid-contract" ||
    state.phase === "recoverable-error"
  ) {
    return (
      <DriverShell>
        <StatusPanel
          title={
            state.phase === "invalid-contract"
              ? "Información no disponible"
              : "No pudimos actualizar"
          }
        >
          {state.phase === "invalid-contract"
            ? "La respuesta no pudo validarse de forma segura."
            : "Intenta nuevamente en unos momentos."}
          <RetryButton onClick={retry} />
        </StatusPanel>
      </DriverShell>
    );
  }

  const dataIsOffline =
    state.phase === "offline" || state.phase === "offline-empty";

  return (
    <DriverShell busy={state.refreshing || operationsState.mutating}>
      <ConnectionSummary
        state={state}
        offline={dataIsOffline || state.realtime === "offline"}
      />
      {remembered ? (
        <aside className={styles.offlineBanner} role="status">
          <strong>Sesión sin confirmar</strong>
          <span>
            Lo que registres queda guardado en este teléfono y se enviará
            cuando haya conexión y se confirme tu sesión.
          </span>
        </aside>
      ) : null}
      <QueueSummary
        operations={projection.operations}
        mutating={operationsState.mutating}
        message={operationsState.message}
        dispatchPhone={dispatchContactState.phoneE164}
        onSync={() => {
          requestSessionCheck();
          void operationsControllerRef.current?.syncNow();
        }}
      />
      {route.kind === "detail" ? (
        <StopDetail
          orderId={route.orderId}
          stop={stop}
          headingRef={headingRef}
          synchronizedAt={state.synchronizedAt}
          offline={dataIsOffline}
          voiceApi={voiceApi}
          operations={projection.operations.filter(
            (operation) => operation.orderId === route.orderId,
          )}
          reportRole={driverReportRoleOf(rolesState)}
          dispatchPhone={dispatchContactState.phoneE164}
          mutating={operationsState.mutating}
          onEnqueue={(kind, blob) =>
            stop && operationsControllerRef.current
              ? operationsControllerRef.current.enqueue(
                  {
                    orderId: stop.order_id,
                    kind,
                    projectedStatus: stop.projectedStatus,
                    projectedVersion: stop.projectedVersion,
                  },
                  blob,
                )
              : Promise.resolve(false)
          }
          onCollectCod={() =>
            stop && stop.cod && operationsControllerRef.current
              ? operationsControllerRef.current.enqueueCodCollection({
                  orderId: stop.order_id,
                  orderPublicId: stop.order_public_id,
                  amountCents: stop.cod.expected.amount_cents,
                  projectedStatus: stop.projectedStatus,
                  projectedVersion: stop.projectedVersion,
                })
              : Promise.resolve(false)
          }
          onReportFailedDelivery={(reason, photo) =>
            stop && operationsControllerRef.current
              ? operationsControllerRef.current.enqueueFailedDelivery(
                  {
                    orderId: stop.order_id,
                    reasonCode: reason,
                    projectedStatus: stop.projectedStatus,
                    projectedVersion: stop.projectedVersion,
                    confirmedStatus: stop.confirmedStatus,
                    confirmedVersion: stop.confirmedVersion,
                    cod: stop.cod,
                  },
                  photo,
                )
              : Promise.resolve(stopUnavailableForReport)
          }
          onDiscard={(id, status, version) =>
            void operationsControllerRef.current?.discard(id, status, version)
          }
          onNewSession={(id) =>
            void operationsControllerRef.current?.createNewSession(id)
          }
          onRetrySame={(id, status, version) =>
            void operationsControllerRef.current?.retrySame(id, status, version)
          }
          onRebuild={(id, status, version) =>
            void operationsControllerRef.current?.rebuildForCurrentVersion(
              id,
              status,
              version,
            )
          }
        />
      ) : route.kind === "list" ? (
        <>
          <ExternalOffersPanel
            state={remembered ? noExternalOffersState : externalOffersState}
            onAccept={(offerId) =>
              void externalOffersControllerRef.current?.accept(offerId)
            }
            onDismiss={(offerId) =>
              externalOffersControllerRef.current?.dismiss(offerId)
            }
          />
          <StopList
            state={state}
            projectedStops={projection.stops}
            retry={retry}
          />
        </>
      ) : (
        <StopDetail
          orderId={null}
          stop={undefined}
          headingRef={headingRef}
          synchronizedAt={state.synchronizedAt}
          offline={dataIsOffline}
          voiceApi={null}
          operations={[]}
          reportRole="unknown"
          dispatchPhone={null}
          mutating={operationsState.mutating}
          onEnqueue={() => Promise.resolve(false)}
          onCollectCod={() => Promise.resolve(false)}
          onReportFailedDelivery={() => Promise.resolve(stopUnavailableForReport)}
          onDiscard={() => undefined}
          onNewSession={() => undefined}
          onRetrySame={() => undefined}
          onRebuild={() => undefined}
        />
      )}
    </DriverShell>
  );
}

function ExternalOffersPanel({
  state,
  onAccept,
  onDismiss,
}: Readonly<{
  state: ExternalOffersState;
  onAccept: (offerId: string) => void;
  onDismiss: (offerId: string) => void;
}>) {
  if (state.loading && state.offers.length === 0) {
    return <p className={styles.liveMessage}>Consultando ofertas externas...</p>;
  }
  if (state.offers.length === 0 && !state.message) return null;
  return (
    <section
      className={styles.externalOffers}
      aria-labelledby="external-offers-heading"
    >
      <h2 id="external-offers-heading">Ofertas externas disponibles</h2>
      <p className={styles.liveMessage} aria-live="polite">
        {state.message}
      </p>
      <ul className={styles.stopList}>
        {state.offers.map((offer) => {
          const pending = state.pendingOfferId === offer.id;
          return (
            <li className={styles.stopCard} key={offer.id}>
              <div className={styles.cardHeading}>
                <div>
                  <p className={styles.cardLabel}>Oferta para orden</p>
                  <h3>{offer.order_id}</h3>
                </div>
                <span className={styles.badge}>Externa</span>
              </div>
              <dl className={styles.stopFacts}>
                <div>
                  <dt>Comisión</dt>
                  <dd>{formatCommission(offer.commission.amount_cents)}</dd>
                </div>
                <div>
                  <dt>Expira</dt>
                  <dd>{formatTimestamp(offer.expires_at)}</dd>
                </div>
                <div>
                  <dt>Estado</dt>
                  <dd>Disponible</dd>
                </div>
              </dl>
              <div className={styles.offerActions}>
                <button
                  type="button"
                  disabled={pending}
                  onClick={() => onAccept(offer.id)}
                >
                  {pending ? "Aceptando..." : "Aceptar oferta"}
                </button>
                <button
                  type="button"
                  disabled={pending}
                  onClick={() => onDismiss(offer.id)}
                >
                  No me interesa
                </button>
              </div>
            </li>
          );
        })}
      </ul>
    </section>
  );
}

function DriverShell({
  children,
  busy = false,
}: Readonly<{ children: React.ReactNode; busy?: boolean }>) {
  return (
    <main className={styles.shell} aria-busy={busy}>
      <header className={styles.header}>
        <div className={styles.headerTop}>
          <p className={styles.eyebrow}>Paquetenvia Repartidor</p>
          <DriverAccount />
        </div>
        <h1 id={driverStopsTitleId} tabIndex={-1}>
          Mis paradas
        </h1>
        <p className={styles.intro}>
          Consulta y registra las acciones de tus recolecciones y entregas.
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

function QueueSummary({
  operations,
  mutating,
  message,
  dispatchPhone,
  onSync,
}: Readonly<{
  operations: readonly DriverOfflineOperation[];
  mutating: boolean;
  message: string | null;
  /** DRV-DISPATCH-CONTACT-2026-10-12: "Llamar a despacho" next to a message that asks to call dispatch. */
  dispatchPhone: string | null;
  onSync: () => void;
}>) {
  if (operations.length === 0 && !message) return null;
  const attention = operations.filter(
    (operation) => operation.status === "NEEDS_ATTENTION",
  ).length;
  return (
    <section className={styles.queueSummary} aria-labelledby="queue-heading">
      <h2 id="queue-heading">Sincronización pendiente</h2>
      <p>
        {operations.length} {operations.length === 1 ? "acción" : "acciones"}{" "}
        en el dispositivo
        {attention > 0 ? `; ${attention} requiere atención` : ""}.
      </p>
      {operations.length > 0 ? (
        <button type="button" disabled={mutating} onClick={onSync}>
          Sincronizar ahora
        </button>
      ) : null}
      <p className={styles.liveMessage} aria-live="polite">
        {message}
      </p>
      {driverMessageCallsDispatch(message) ? (
        <DriverDispatchCall phoneE164={dispatchPhone} />
      ) : null}
    </section>
  );
}

function StopList({
  state,
  projectedStops,
  retry,
}: Readonly<{
  state: DriverStopsViewState;
  projectedStops: readonly ProjectedDriverStop[];
  retry: () => void;
}>) {
  if (state.phase === "offline-empty") {
    return (
      <StatusPanel title="Sin información guardada">
        No hay información disponible sin conexión. Conéctate para actualizar
        tus paradas.
        <RetryButton onClick={retry} />
      </StatusPanel>
    );
  }
  const completedToday = completedTodayAt(
    state.synchronizedAt,
    state.completedToday,
  );
  if (state.phase === "empty") {
    return (
      <StatusPanel title="Sin paradas activas">
        No tienes paradas activas.
        <StopsSummary stops={[]} completedToday={completedToday} />
      </StatusPanel>
    );
  }

  return (
    <>
      {state.phase === "offline" ? (
        <OfflineBanner synchronizedAt={state.synchronizedAt} />
      ) : null}
      <StopsSummary stops={projectedStops} completedToday={completedToday} />
      <ul className={styles.stopList} aria-label="Paradas asignadas">
        {projectedStops.map((stop, index) => (
          <li className={styles.stopCard} key={stop.order_id}>
            <div className={styles.cardHeading}>
              <div>
                <p className={styles.rowLabel}>{stopRowLabel(stop, index)}</p>
                <h2>{stop.order_public_id}</h2>
              </div>
              <span className={index === 0 ? styles.badge : styles.quietBadge}>
                {index === 0 ? "Siguiente" : "Pendiente"}
              </span>
            </div>
            <StopHighlights stop={stop} />
            <dl className={styles.stopFacts}>
              <div>
                <dt>Estado</dt>
                <dd>{driverConfirmedStatusLabel(stop.confirmedStatus)}</dd>
              </div>
              <div>
                <dt>Dirección</dt>
                <dd className={styles.addressText}>{stopAddressText(stop)}</dd>
              </div>
              <div>
                <dt>Sincronización</dt>
                <dd>
                  {stop.attentionCount > 0
                    ? "Requiere atención"
                    : stop.pendingCount > 0
                      ? `${stop.pendingCount} pendiente${stop.pendingCount === 1 ? "" : "s"}`
                      : state.phase === "offline"
                        ? "Información guardada"
                        : "Actualizada"}
                </dd>
              </div>
            </dl>
            <DriverStopsLink
              className={styles.primaryLink}
              href={`/driver/stops/${stop.order_id}`}
            >
              Ver detalle
            </DriverStopsLink>
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
  voiceApi,
  operations,
  reportRole,
  dispatchPhone,
  mutating,
  onEnqueue,
  onCollectCod,
  onReportFailedDelivery,
  onDiscard,
  onNewSession,
  onRetrySame,
  onRebuild,
}: Readonly<{
  orderId: string | null;
  stop: ProjectedDriverStop | undefined;
  headingRef: React.RefObject<HTMLHeadingElement | null>;
  synchronizedAt: string | null;
  offline: boolean;
  voiceApi: DriverVoiceApi | null;
  operations: readonly DriverOfflineOperation[];
  /** driverReportRoleOf what the phone knows of the roles getMyOrganizationRoles answers (ORG-MY-ROLES-2026-10-12). */
  reportRole: DriverReportRole;
  /** The organization's dispatch number (getMyDispatchContact), or null (DRV-DISPATCH-CONTACT-2026-10-12). */
  dispatchPhone: string | null;
  mutating: boolean;
  onEnqueue: (kind: DriverOperationKind, blob?: Blob) => Promise<boolean>;
  onCollectCod: () => Promise<boolean>;
  onReportFailedDelivery: (
    reason: DriverFailedDeliveryReason,
    photo: Blob,
  ) => Promise<DriverFailedDeliveryEnqueueResult>;
  onDiscard: (
    operationId: string,
    status: DriverOperationalStatus | null,
    version: number,
  ) => void;
  onNewSession: (operationId: string) => void;
  onRetrySame: (
    operationId: string,
    status: DriverOperationalStatus,
    version: number,
  ) => void;
  onRebuild: (
    operationId: string,
    status: DriverOperationalStatus,
    version: number,
  ) => void;
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
        <DriverStopsLink className={styles.secondaryLink} href="/driver/stops">
          Volver a mis paradas
        </DriverStopsLink>
      </section>
    );
  }

  return (
    <>
      {offline ? <OfflineBanner synchronizedAt={synchronizedAt} /> : null}
      <article className={styles.detail}>
        <p className={styles.cardLabel}>Detalle de parada</p>
        <StopLead stop={stop} />
        <h2 ref={headingRef} tabIndex={-1}>
          {stop.order_public_id}
        </h2>
        <NavigationLink stop={stop} />
        <dl className={styles.detailFacts}>
          <StopDetailFacts stop={stop} />
          <div>
            <dt>Tipo de parada</dt>
            <dd>{driverStopTypeLabel(stop.stop_type)}</dd>
          </div>
          <div>
            <dt>Estado confirmado</dt>
            <dd>
              <span className={styles.badge}>
                {driverConfirmedStatusLabel(stop.confirmedStatus)}
              </span>
            </dd>
          </div>
          {stop.pendingCount > 0 ? (
            <div>
              <dt>Estado por sincronizar</dt>
              <dd>
                <span className={styles.pendingBadge}>
                  {driverStopStatusLabel(stop.projectedStatus)}
                </span>
              </dd>
            </div>
          ) : null}
          <div>
            <dt>Última sincronización</dt>
            <dd>{formatTimestamp(synchronizedAt)}</dd>
          </div>
          <div>
            <dt>Conectividad</dt>
            <dd>{offline ? "Sin conexión" : "En línea"}</dd>
          </div>
        </dl>
        <StopAction
          key={stop.projectedStatus}
          stop={stop}
          disabled={mutating}
          onEnqueue={onEnqueue}
          onCollectCod={onCollectCod}
        />
        <RecipientCall
          api={voiceApi}
          orderId={stop.order_id}
          stopType={stop.stop_type}
          confirmedStatus={stop.confirmedStatus}
          offline={offline}
        />
        {/* UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: last of the delivery screen, as designed. */}
        <DriverFailedDelivery
          key={stop.order_id}
          stop={stop}
          operations={operations}
          reportRole={reportRole}
          dispatchPhone={dispatchPhone}
          disabled={mutating}
          onReport={onReportFailedDelivery}
          onDiscardCollection={(operationId) =>
            onDiscard(
              operationId,
              toOperationalStatus(stop.confirmedStatus),
              stop.confirmedVersion,
            )
          }
        />
        <ConflictResolution
          stop={stop}
          operations={operations}
          dispatchPhone={dispatchPhone}
          disabled={mutating}
          onDiscard={onDiscard}
          onNewSession={onNewSession}
          onRetrySame={onRetrySame}
          onRebuild={onRebuild}
        />
        <DriverStopsLink className={styles.secondaryLink} href="/driver/stops">
          Volver a mis paradas
        </DriverStopsLink>
      </article>
    </>
  );
}

function StopAction({
  stop,
  disabled,
  onEnqueue,
  onCollectCod,
}: Readonly<{
  stop: ProjectedDriverStop;
  disabled: boolean;
  onEnqueue: (kind: DriverOperationKind, blob?: Blob) => Promise<boolean>;
  onCollectCod: () => Promise<boolean>;
}>) {
  const { confirm, dialog } = useConfirmDialog();
  const kind = nextDriverOperationKind(stop.projectedStatus);
  if (!kind || stop.attentionCount > 0) return null;
  if (kind === "DELIVERY_PROOF" && stop.deliveryAwaitingCash) {
    // UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: the delivery and its photo are
    // already saved; they wait for the cash, so only "Recibí" is offered and
    // the delivery is sent right after the collection.
    return (
      <>
        <fieldset className={styles.actions} disabled={disabled}>
          <legend>Siguiente acción</legend>
          <DriverCodCollection
            stop={stop}
            disabled={disabled}
            confirm={confirm}
            onCollect={onCollectCod}
          />
          <p className={styles.deliveryWaitingHint}>
            La entrega ya está guardada en el teléfono. Se enviará después de
            registrar el cobro.
          </p>
        </fieldset>
        {dialog}
      </>
    );
  }
  const proofRequired = kind === "PICKUP_PROOF" || kind === "DELIVERY_PROOF";
  // UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: the delivery is never queued
  // before the cash is recorded, at least on the phone; the collection is
  // queued first, so it also reaches the server first.
  const cashFirst =
    kind === "DELIVERY_PROOF" &&
    stop.cod !== null &&
    stop.codCollection !== "recorded" &&
    stop.codCollection !== "queued";
  const cashHintId = `cash-first-${stop.order_id}`;
  return (
    <>
      <fieldset className={styles.actions} disabled={disabled}>
        <legend>Siguiente acción</legend>
        {kind === "DELIVERY_PROOF" && stop.cod !== null ? (
          <DriverCodCollection
            stop={stop}
            disabled={disabled}
            confirm={confirm}
            onCollect={onCollectCod}
          />
        ) : null}
        {proofRequired ? (
          <fieldset
            className={styles.proofStep}
            disabled={cashFirst}
            aria-describedby={cashFirst ? cashHintId : undefined}
          >
            {cashFirst ? (
              <p id={cashHintId} className={styles.cashFirstHint}>
                Registra el cobro antes de confirmar la entrega.
              </p>
            ) : null}
            {/* The photo is queued only from its preview ("Usar esta foto"). */}
            <DriverProofCapture
              orderId={stop.order_id}
              actionLabel={driverOperationLabel(kind)}
              onUse={(photo) => onEnqueue(kind, photo)}
            />
          </fieldset>
        ) : (
          <button
            type="button"
            disabled={disabled}
            onClick={() => void onEnqueue(kind)}
          >
            {driverOperationLabel(kind)}
          </button>
        )}
      </fieldset>
      {dialog}
    </>
  );
}

function ConflictResolution({
  stop,
  operations,
  dispatchPhone,
  disabled,
  onDiscard,
  onNewSession,
  onRetrySame,
  onRebuild,
}: Readonly<{
  stop: ProjectedDriverStop;
  operations: readonly DriverOfflineOperation[];
  dispatchPhone: string | null;
  disabled: boolean;
  onDiscard: (
    operationId: string,
    status: DriverOperationalStatus | null,
    version: number,
  ) => void;
  onNewSession: (operationId: string) => void;
  onRetrySame: (
    operationId: string,
    status: DriverOperationalStatus,
    version: number,
  ) => void;
  onRebuild: (
    operationId: string,
    status: DriverOperationalStatus,
    version: number,
  ) => void;
}>) {
  const [confirmation, setConfirmation] = useState<
    "new-session" | "rebuild" | "discard" | null
  >(null);
  const confirmationButtonRef = useRef<HTMLButtonElement>(null);
  const confirmationDialogRef = useRef<HTMLDialogElement>(null);
  const triggerRef = useRef<HTMLButtonElement | null>(null);
  useEffect(() => {
    if (confirmation) {
      const dialog = confirmationDialogRef.current;
      if (dialog && !dialog.open) dialog.showModal();
      confirmationButtonRef.current?.focus();
    }
  }, [confirmation]);
  const attention = operations.find(
    (operation) => operation.status === "NEEDS_ATTENTION",
  );
  if (!attention) return null;
  const blockedCount = operations.filter(
    (operation) => operation.status === "BLOCKED",
  ).length;
  const confirmedOperationalStatus = toOperationalStatus(stop.confirmedStatus);
  // UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: a "No se pudo entregar"
  // report explains itself (its incident, the cash as it is now, or the
  // attempt that moved on), is never rebuilt for another version (a new
  // attempt is reported again from the stop; an open incident stays with
  // dispatch) and is retried only where it still applies and the cash is not
  // recorded (then the driver calls dispatch).
  const report = attention.kind === "FAILED_DELIVERY";
  const cashHold =
    report && attention.safeError === "COD_COLLECTED"
      ? driverFailedReportHoldState(stop, operations)
      : null;
  const offerRetry =
    !report ||
    (cashHold !== "recorded" &&
      confirmedOperationalStatus !== null &&
      driverOperationApplies(
        attention,
        confirmedOperationalStatus,
        stop.confirmedVersion,
      ));
  const offerRebuild =
    driverOperationDefinitions[attention.kind].transition && !report;
  // DRV-DISPATCH-CONTACT-2026-10-12: a report's notice that asks to call
  // dispatch about the cash gets "Llamar a despacho" when the number is known.
  const reportAttention = report
    ? driverFailedReportAttention(attention, stop, operations)
    : null;
  const closeConfirmation = () => {
    confirmationDialogRef.current?.close();
    setConfirmation(null);
    queueMicrotask(() => triggerRef.current?.focus());
  };
  const requestConfirmation = (
    value: "new-session" | "rebuild" | "discard",
    trigger: HTMLButtonElement,
  ) => {
    triggerRef.current = trigger;
    setConfirmation(value);
  };
  const confirmAction = () => {
    if (confirmation === "new-session") {
      onNewSession(attention.id);
    } else if (
      confirmation === "rebuild" &&
      confirmedOperationalStatus
    ) {
      onRebuild(
        attention.id,
        confirmedOperationalStatus,
        stop.confirmedVersion,
      );
    } else if (confirmation === "discard") {
      onDiscard(
        attention.id,
        confirmedOperationalStatus,
        stop.confirmedVersion,
      );
    }
    closeConfirmation();
  };
  return (
    <section className={styles.conflict} aria-labelledby="conflict-heading">
      <h3 id="conflict-heading">Acción que requiere atención</h3>
      <p>
        {reportAttention
          ? reportAttention.message
          : driverRejectionMessage(attention.kind, attention.rejectionCode)}
      </p>
      {reportAttention?.callDispatch ? (
        <DriverDispatchCall phoneE164={dispatchPhone} />
      ) : null}
      <dl>
        <div>
          <dt>Hora registrada en el dispositivo</dt>
          <dd>{formatTimestamp(attention.clientOccurredAt)}</dd>
        </div>
        <div>
          <dt>Estado confirmado</dt>
          <dd>{driverStopStatusLabel(stop.confirmedStatus)}</dd>
        </div>
        <div>
          <dt>Acción prevista</dt>
          <dd>
            {driverOperationDefinitions[attention.kind].transition
              ? driverStopStatusLabel(attention.targetStatus)
              : driverOperationLabel(attention.kind)}
          </dd>
        </div>
        <div>
          <dt>Acciones bloqueadas después</dt>
          <dd>{blockedCount}</dd>
        </div>
      </dl>
      <div className={styles.conflictActions}>
        {attention.safeError === "SESSION_EXPIRED" ? (
          <button
            type="button"
            disabled={disabled}
            onClick={(event) =>
              requestConfirmation("new-session", event.currentTarget)
            }
          >
            Crear nueva sesión
          </button>
        ) : null}
        {offerRetry ? (
          <button
            type="button"
            disabled={disabled || confirmedOperationalStatus === null}
            onClick={() =>
              confirmedOperationalStatus
                ? onRetrySame(
                    attention.id,
                    confirmedOperationalStatus,
                    stop.confirmedVersion,
                  )
                : undefined
            }
          >
            Reintentar la misma acción
          </button>
        ) : null}
        {offerRebuild ? (
          <button
            type="button"
            disabled={disabled || confirmedOperationalStatus === null}
            onClick={(event) =>
              requestConfirmation("rebuild", event.currentTarget)
            }
          >
            Crear acción para versión actual
          </button>
        ) : null}
        <button
          type="button"
          disabled={disabled}
          onClick={(event) =>
            requestConfirmation("discard", event.currentTarget)
          }
        >
          Descartar
        </button>
      </div>
      {confirmation ? (
        <dialog
          ref={confirmationDialogRef}
          className={styles.confirmationDialog}
          aria-labelledby="confirmation-title"
          aria-describedby="confirmation-description"
          onCancel={(event) => {
            event.preventDefault();
            closeConfirmation();
          }}
        >
          <h4 id="confirmation-title">Confirma la acción</h4>
          <p id="confirmation-description">
            {confirmation === "new-session"
              ? "Se creará una nueva sesión de carga conservando la evidencia local."
              : confirmation === "rebuild"
                ? "Se descartará la operación en conflicto y se creará otra con la versión confirmada actual."
                : "Se eliminarán esta operación y su evidencia local del dispositivo."}
          </p>
          <div className={styles.dialogActions}>
            <button
              ref={confirmationButtonRef}
              type="button"
              disabled={disabled}
              onClick={confirmAction}
            >
              Confirmar
            </button>
            <button type="button" onClick={closeConfirmation}>
              Cancelar
            </button>
          </div>
        </dialog>
      ) : null}
    </section>
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

/**
 * DRV-PWA-INAPP-NAVIGATION: "Ver detalle" and "Volver a mis paradas". A real
 * anchor (a modified click still opens a new tab, and the Service Worker
 * shell still answers a page load); a plain click moves inside the app
 * without loading a page, so the session, the controllers and the open
 * partition stay as they are, also offline.
 */
function DriverStopsLink({
  href,
  className,
  children,
}: Readonly<{ href: string; className: string; children: React.ReactNode }>) {
  return (
    <a
      className={className}
      href={href}
      onClick={(event) => {
        if (isInAppNavigationClick(event) && navigateWithinDriverStops(href)) {
          event.preventDefault();
        }
      }}
    >
      {children}
    </a>
  );
}

/**
 * DRV-PWA-INAPP-NAVIGATION: the installed app has no address bar, so a
 * screen without a usable session offers "Iniciar sesión": /login with this
 * stops path as its validated return_url (buildSignInPromptHref). It appears
 * once the page's session check is over, never while it is still running.
 * Signing in needs the network, so offline the button is disabled and says
 * why, like "Cerrar sesión".
 */
function SignInAction({ route }: Readonly<{ route: DriverStopsRoute | null }>) {
  const online = useBrowserOnline();
  const settled = useSessionBootstrapSettled();
  if (!settled) return null;
  const returnPath =
    route?.kind === "detail" ? `/driver/stops/${route.orderId}` : "/driver/stops";
  if (!online) {
    return (
      <>
        <button className={styles.signInAction} type="button" disabled>
          Iniciar sesión
        </button>
        <span className={styles.signInHint}>
          Conéctate a internet para iniciar sesión.
        </span>
      </>
    );
  }
  return (
    <a className={styles.signInAction} href={buildSignInPromptHref(returnPath)}>
      Iniciar sesión
    </a>
  );
}

function useBrowserOnline(): boolean {
  const [online, setOnline] = useState(true);
  useEffect(() => {
    const refresh = () => setOnline(navigator.onLine);
    refresh();
    window.addEventListener("online", refresh);
    window.addEventListener("offline", refresh);
    return () => {
      window.removeEventListener("online", refresh);
      window.removeEventListener("offline", refresh);
    };
  }, []);
  return online;
}

function useSessionBootstrapSettled(): boolean {
  const [settled, setSettled] = useState(false);
  useEffect(() => {
    const refresh = () => setSettled(isSessionBootstrapSettled());
    refresh();
    window.addEventListener(sessionBootstrapSettledEvent, refresh);
    return () =>
      window.removeEventListener(sessionBootstrapSettledEvent, refresh);
  }, []);
  return settled;
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

function formatCommission(amountCents: number): string {
  return formatMxnCents(amountCents);
}

function toOperationalStatus(
  value: ProjectedDriverStop["confirmedStatus"],
): DriverOperationalStatus | null {
  return [
    "ASSIGNED",
    "AT_PICKUP",
    "PICKED_UP",
    "IN_TRANSIT",
    "DELIVERING",
  ].includes(value)
    ? (value as DriverOperationalStatus)
    : null;
}
