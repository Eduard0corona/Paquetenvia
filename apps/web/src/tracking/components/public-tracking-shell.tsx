"use client";

import { clientApiBaseUrl } from "../../lib/api-base-url";
import { useState } from "react";
import {
  publicTrackingHeading,
  resolvePublicTrackingBrand,
} from "../contracts/public-tracking-branding";
import {
  publicStatusLabels,
  publicTimelineLabels,
} from "../contracts/public-tracking-labels";
import {
  formatPublicTrackingEstimatedWindow,
  formatPublicTrackingTimestamp,
} from "../contracts/public-tracking-formatters";
import { parsePublicTrackingPathname } from "../routing/public-tracking-route";
import { usePublicTracking } from "../state/use-public-tracking";

export function PublicTrackingShell() {
  const [token] = useState<string | null | undefined>(() => {
    if (typeof window === "undefined") return undefined;
    const route = parsePublicTrackingPathname(window.location.pathname);
    return route.kind === "tracking" ? route.token : null;
  });
  const apiBaseUrl = clientApiBaseUrl();
  const state = usePublicTracking(
    token ?? null,
    apiBaseUrl,
  );
  const brand = resolvePublicTrackingBrand(
    process.env.NEXT_PUBLIC_TRACKING_BRAND_NAME,
  );
  const supportUrl = safeSupportUrl(
    process.env.NEXT_PUBLIC_TRACKING_SUPPORT_URL,
  );

  if (token === undefined || state.view === "loading") {
    return (
      <main className="trackingShell" aria-busy="true">
        <section className="trackingCard">
          <TrackingHeading brand={brand} />
          <h1>Consultando tu envío…</h1>
        </section>
      </main>
    );
  }

  if (
    state.projection === null &&
    (state.view === "unavailable" || state.view === "rate-limited")
  ) {
    return (
      <main className="trackingShell">
        <section className="trackingCard" aria-live="polite">
          <TrackingHeading brand={brand} />
          <h1>
            {state.view === "rate-limited"
              ? "Espera un momento antes de volver a intentarlo."
              : "El seguimiento no está disponible temporalmente."}
          </h1>
          <button type="button" onClick={state.refresh}>
            Reintentar
          </button>
          <Support url={supportUrl} />
        </section>
      </main>
    );
  }

  if (state.view === "not-found" || state.projection === null) {
    return (
      <main className="trackingShell">
        <section className="trackingCard">
          <TrackingHeading brand={brand} />
          <h1>No pudimos encontrar este seguimiento.</h1>
          <p>Revisa el enlace o solicita uno nuevo.</p>
          <Support url={supportUrl} />
        </section>
      </main>
    );
  }

  const temporarilyUnavailable =
    state.view === "unavailable" || state.view === "rate-limited";
  const estimatedWindow = formatPublicTrackingEstimatedWindow(
    state.projection.estimated_window,
  );
  return (
    <main className="trackingShell">
      <article className="trackingCard">
        <TrackingHeading brand={brand} />
        <h1 className="trackingPublicId">{state.projection.public_id}</h1>

        <section aria-labelledby="tracking-status-title">
          <h2 id="tracking-status-title">Estado</h2>
          <p className="trackingStatus">
            {publicStatusLabels[state.projection.public_status]}
          </p>
        </section>

        <section aria-labelledby="tracking-window-title">
          <h2 id="tracking-window-title">Ventana de entrega</h2>
          <p>{estimatedWindow ?? "Ventana de entrega por confirmar"}</p>
        </section>

        <section aria-labelledby="tracking-timeline-title">
          <h2 id="tracking-timeline-title">Historial</h2>
          <p className="trackingTimeZoneNotice">
            Horarios mostrados en hora de Mazatlán.
          </p>
          <ol className="trackingTimeline">
            {state.projection.timeline.map((item, index) => (
              <li key={`${item.occurred_at}-${index}`}>
                <span>{publicTimelineLabels[item.code]}</span>
                <time dateTime={item.occurred_at}>
                  {formatPublicTrackingTimestamp(item.occurred_at)}
                </time>
              </li>
            ))}
          </ol>
        </section>

        <section className="trackingUpdate" aria-live="polite">
          <h2>Actualización</h2>
          <p>{connectionText(state.connection)}</p>
          {state.connection === "offline" && (
            <p>La información podría no estar actualizada.</p>
          )}
          {temporarilyUnavailable && (
            <p>
              {state.view === "rate-limited"
                ? "Espera un momento antes de volver a intentarlo."
                : "El seguimiento no está disponible temporalmente."}
            </p>
          )}
          {state.lastUpdated !== null && (
            <p>
              Última actualización:{" "}
              <time dateTime={state.lastUpdated.toISOString()}>
                {formatPublicTrackingTimestamp(state.lastUpdated)}
              </time>
            </p>
          )}
          <button type="button" onClick={state.refresh}>
            {temporarilyUnavailable ? "Reintentar" : "Actualizar"}
          </button>
        </section>

        <Support url={supportUrl} />
      </article>
    </main>
  );
}

/**
 * GATE-001-NEUTRAL-PUBLIC-BRAND-2026-10-10: every state opens with the neutral
 * heading and never names the unvalidated commercial name; a configured brand
 * only goes above it.
 */
function TrackingHeading({ brand }: Readonly<{ brand: string | null }>) {
  if (brand === null) {
    return <p className="trackingHeading">{publicTrackingHeading}</p>;
  }
  return (
    <>
      <p className="trackingHeading">{brand}</p>
      <p className="trackingEyebrow">{publicTrackingHeading}</p>
    </>
  );
}

function Support({ url }: Readonly<{ url: string | null }>) {
  return (
    <section aria-labelledby="tracking-support-title">
      <h2 id="tracking-support-title">Soporte</h2>
      {url === null ? (
        <p>Comunícate por el mismo canal donde recibiste este enlace.</p>
      ) : (
        <a href={url} rel="noreferrer noopener">
          Contactar a soporte
        </a>
      )}
    </section>
  );
}

function safeSupportUrl(value: string | undefined): string | null {
  if (!value) return null;
  try {
    const url = new URL(value);
    return url.protocol === "https:" || url.protocol === "mailto:"
      ? url.toString()
      : null;
  } catch {
    return null;
  }
}

function connectionText(
  state: "connecting" | "connected" | "reconnecting" | "offline",
): string {
  switch (state) {
    case "connected":
      return "Conectado";
    case "reconnecting":
      return "Reconectando…";
    case "offline":
      return "Sin conexión";
    default:
      return "Conectando…";
  }
}
