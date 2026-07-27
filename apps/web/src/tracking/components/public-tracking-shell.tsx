"use client";

import { useState } from "react";
import {
  publicStatusLabels,
  publicTimelineLabels,
} from "../contracts/public-tracking-labels";
import { parsePublicTrackingPathname } from "../routing/public-tracking-route";
import { usePublicTracking } from "../state/use-public-tracking";

export function PublicTrackingShell() {
  const [token] = useState<string | null | undefined>(() => {
    if (typeof window === "undefined") return undefined;
    const route = parsePublicTrackingPathname(window.location.pathname);
    return route.kind === "tracking" ? route.token : null;
  });
  const apiBaseUrl =
    process.env.NEXT_PUBLIC_API_BASE_URL ??
    (typeof window === "undefined" ? "http://localhost" : window.location.origin);
  const state = usePublicTracking(
    token ?? null,
    apiBaseUrl,
  );
  const brand = safeBrand(process.env.NEXT_PUBLIC_TRACKING_BRAND_NAME);
  const supportUrl = safeSupportUrl(
    process.env.NEXT_PUBLIC_TRACKING_SUPPORT_URL,
  );

  if (token === undefined || state.view === "loading") {
    return (
      <main className="trackingShell" aria-busy="true">
        <section className="trackingCard">
          <p className="trackingBrand">{brand}</p>
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
          <p className="trackingBrand">{brand}</p>
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
          <p className="trackingBrand">{brand}</p>
          <h1>No pudimos encontrar este seguimiento.</h1>
          <p>Revisa el enlace o solicita uno nuevo.</p>
          <Support url={supportUrl} />
        </section>
      </main>
    );
  }

  const temporarilyUnavailable =
    state.view === "unavailable" || state.view === "rate-limited";
  return (
    <main className="trackingShell">
      <article className="trackingCard">
        <p className="trackingBrand">{brand}</p>
        <p className="trackingEyebrow">Seguimiento de envío</p>
        <h1 className="trackingPublicId">{state.projection.public_id}</h1>

        <section aria-labelledby="tracking-status-title">
          <h2 id="tracking-status-title">Estado</h2>
          <p className="trackingStatus">
            {publicStatusLabels[state.projection.public_status]}
          </p>
        </section>

        <section aria-labelledby="tracking-window-title">
          <h2 id="tracking-window-title">Ventana de entrega</h2>
          <p>{formatEstimatedWindow(state.projection.estimated_window)}</p>
        </section>

        <section aria-labelledby="tracking-timeline-title">
          <h2 id="tracking-timeline-title">Historial</h2>
          <ol className="trackingTimeline">
            {state.projection.timeline.map((item, index) => (
              <li key={`${item.occurred_at}-${index}`}>
                <span>{publicTimelineLabels[item.code]}</span>
                <time dateTime={item.occurred_at}>
                  {formatTimestamp(item.occurred_at)}
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
                {formatTimestamp(state.lastUpdated.toISOString())}
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

function safeBrand(value: string | undefined): string {
  const brand = value?.trim();
  return brand && brand.length <= 80 ? brand : "Paquetenvia";
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

function formatEstimatedWindow(
  window: Readonly<Record<string, string | null>> | null,
): string {
  const from = window?.from;
  const to = window?.to;
  if (
    typeof from !== "string" ||
    typeof to !== "string" ||
    !Number.isFinite(Date.parse(from)) ||
    !Number.isFinite(Date.parse(to))
  ) {
    return "Ventana de entrega por confirmar";
  }
  return `${formatTimestamp(from)} – ${formatTimestamp(to)}`;
}

function formatTimestamp(value: string): string {
  return new Intl.DateTimeFormat("es-MX", {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(new Date(value));
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
