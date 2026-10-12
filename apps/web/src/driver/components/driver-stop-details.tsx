"use client";

import { Money } from "../../components/ui/money";
import { sumCents } from "../../operations/contracts/money";
import {
  DriverStopDetailsUnavailableText,
  driverStopActionVerb,
  driverStopContactName,
  driverStopLead,
  driverStopsDayLabel,
  formatDriverServiceWindow,
  googleMapsSearchUrl,
} from "../contracts/stop-details";
import type { ProjectedDriverStop } from "../offline/operation-projection";
import styles from "./driver-stops.module.css";

/**
 * UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: what the driver needs at the door,
 * from the stop the server returned for the driver's own assignment. Nothing
 * here calls the network or stores anything.
 */

/**
 * The cash still to collect, in integer cents: not recorded on the server and
 * not saved on the phone; null when there is nothing to collect.
 */
export function codStillToCollect(stop: ProjectedDriverStop): number | null {
  return stop.cod !== null &&
    (stop.codCollection === "pending" || stop.codCollection === "attention")
    ? stop.cod.expected.amount_cents
    : null;
}

/**
 * "Hoy · 5 paradas · 2 hechas · 1 cobro pendiente $1,250.00" above the list.
 * `completedToday` is today's count (completedTodayAt), or null when the app
 * does not know it.
 */
export function StopsSummary({
  stops,
  completedToday,
}: Readonly<{
  stops: readonly ProjectedDriverStop[];
  completedToday: number | null;
}>) {
  // With no open stop the header is shown only once something was done today.
  if (stops.length === 0 && !completedToday) return null;
  const pending = stops
    .map(codStillToCollect)
    .filter((amount): amount is number => amount !== null);
  const total = sumCents(pending);
  return (
    <p className={styles.stopsSummary}>
      {driverStopsDayLabel(stops.length, completedToday)}
      {pending.length > 0 && total !== null ? (
        <>
          {" · "}
          {pending.length}{" "}
          {pending.length === 1 ? "cobro pendiente" : "cobros pendientes"}{" "}
          <Money cents={total} currency={false} />
        </>
      ) : null}
    </p>
  );
}

/** "3 · Entregar": position in the route order and what to do there. */
export function stopRowLabel(stop: ProjectedDriverStop, index: number): string {
  return `${index + 1} · ${driverStopActionVerb(stop.stop_type)}`;
}

/**
 * "Tres Ríos · M. López" (or the full contact name of a pickup or return) and
 * "13:00–15:00 · Cobrar $1,250.00" on a list row; "Datos de entrega no
 * disponibles por ahora" when the stop has no details.
 */
export function StopHighlights({
  stop,
}: Readonly<{ stop: ProjectedDriverStop }>) {
  const who = [stop.zone_name, driverStopContactName(stop)].filter(
    (value): value is string => value !== null,
  );
  const collect = codStillToCollect(stop);
  return (
    <>
      {who.length > 0 ? (
        <p className={styles.stopHighlight}>{who.join(" · ")}</p>
      ) : null}
      {stop.details_available ? null : <DetailsUnavailable />}
      {stop.service_window !== null || collect !== null ? (
        <p className={styles.stopHighlight}>
          {stop.service_window !== null
            ? formatDriverServiceWindow(stop.service_window)
            : null}
          {stop.service_window !== null && collect !== null ? " · " : null}
          {collect !== null ? (
            <strong>
              Cobrar <Money cents={collect} currency={false} />
            </strong>
          ) : null}
        </p>
      ) : null}
    </>
  );
}

/** "Entregar a M. López" above the tracking number of the detail. */
export function StopLead({ stop }: Readonly<{ stop: ProjectedDriverStop }>) {
  return <p className={styles.stopLead}>{driverStopLead(stop)}</p>;
}

/** The notice of a stop whose details the server could not read. */
export function DetailsUnavailable() {
  return (
    <p className={styles.detailsUnavailable}>
      {DriverStopDetailsUnavailableText}
    </p>
  );
}

/**
 * The address a stop shows: the full address with its details, the address
 * summary (colony and city) without them.
 */
export function stopAddressText(stop: ProjectedDriverStop): string {
  return stop.navigation?.address_text ?? stop.address_summary;
}

/**
 * "Cómo llegar" (owner answer "Google Maps"): opens the phone's maps app, or
 * the Google Maps web page, on the stop's point. It is a plain link in a new
 * tab: our system sends no request and computes no route or ETA (GATE-003).
 * Without details there is no point to open, so the notice is shown instead.
 */
export function NavigationLink({
  stop,
}: Readonly<{ stop: ProjectedDriverStop }>) {
  if (stop.navigation === null) return <DetailsUnavailable />;
  return (
    <div className={styles.navigation}>
      <a
        className={styles.navigationLink}
        href={googleMapsSearchUrl(stop.navigation)}
        target="_blank"
        rel="noopener noreferrer"
        aria-describedby={`navigation-hint-${stop.order_id}`}
      >
        Cómo llegar
      </a>
      <p id={`navigation-hint-${stop.order_id}`} className={styles.navigationHint}>
        Abre Google Maps con la ubicación de la parada.
      </p>
    </div>
  );
}

/** Address, references, window and cash facts of the detail. */
export function StopDetailFacts({
  stop,
}: Readonly<{ stop: ProjectedDriverStop }>) {
  return (
    <>
      <div>
        <dt>Dirección</dt>
        <dd className={styles.addressText}>{stopAddressText(stop)}</dd>
      </div>
      {stop.navigation !== null && stop.navigation.references !== null ? (
        <div>
          <dt>Referencias</dt>
          <dd className={styles.addressText}>{stop.navigation.references}</dd>
        </div>
      ) : null}
      {stop.zone_name !== null ? (
        <div>
          <dt>Zona</dt>
          <dd>{stop.zone_name}</dd>
        </div>
      ) : null}
      {stop.service_window !== null ? (
        <div>
          <dt>Ventana de entrega</dt>
          <dd>{formatDriverServiceWindow(stop.service_window)}</dd>
        </div>
      ) : null}
      {stop.cod !== null ? (
        <div>
          <dt>Cobro</dt>
          <dd>
            <Money cents={stop.cod.expected.amount_cents} currency={false} strong />{" "}
            {codCollectionText(stop)}
          </dd>
        </div>
      ) : null}
    </>
  );
}

function codCollectionText(stop: ProjectedDriverStop): string {
  switch (stop.codCollection) {
    case "recorded":
      return "registrado";
    case "queued":
      return "guardado en el teléfono; se registrará al sincronizar";
    case "attention":
      return "requiere atención";
    default:
      return "por cobrar al entregar";
  }
}
