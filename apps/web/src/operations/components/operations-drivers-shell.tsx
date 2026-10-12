"use client";

import { DateTime } from "../../components/ui/date-time";
import { EmptyState } from "../../components/ui/empty-state";
import { Feedback, ScreenGate } from "../../components/ui/feedback";
import { PageHeader } from "../../components/ui/page-header";
import { vehicleLabel } from "../contracts/assignable-driver";
import {
  activeStopsText,
  organizationDriverStatusLabels,
  organizationDriverStatusTones,
  type OrganizationDriver,
} from "../contracts/organization-driver";
import { useDrivers } from "../state/use-drivers";

const toneClass = {
  muted: "statusToneMuted",
  ok: "statusToneOk",
  warn: "statusToneWarn",
} as const;

/**
 * UI-PHASE3-DRIVERS-2026-10-11 ("Lista de consulta"): the dispatcher's read-only list of the organization's own
 * drivers - reference, vehicle, status, active stops and the time of the latest location. No personal data
 * (GATE-007), no coordinates and no actions: drivers are not added or removed here. The backend and RLS remain the
 * barrier; this screen only shows what listOrganizationDrivers returns.
 */
export function OperationsDriversShell() {
  const { state, controller } = useDrivers();
  const blocked = state.phase === "no_session" || state.phase === "access_unavailable";
  return (
    <div className="page" aria-busy={state.loading || state.loadingMore}>
      <PageHeader
        eyebrow="Despacho"
        title="Repartidores"
        description="Consulta de los repartidores de tu flota. Aquí no se dan de alta ni de baja."
        live
        actions={
          <>
            <span>
              Última actualización:{" "}
              {state.lastUpdated === null ? "Pendiente" : <DateTime value={state.lastUpdated} />}
            </span>
            <button
              type="button"
              className="btn btnSecondary"
              onClick={() => void controller.refresh("manual")}
              disabled={state.phase !== "ready" || state.loading}
            >
              Actualizar
            </button>
          </>
        }
      />

      <p className="pageNote">Horarios mostrados en hora de Mazatlán.</p>

      <ScreenGate phase={state.phase} accessMessage="Tu rol en la organización activa no consulta repartidores." />

      {!blocked && (
        <>
          <Feedback errors={[]} message={state.message} stepUpHref={state.stepUpHref} />
          {state.error !== null && (
            <p className="notice noticeCrit" role="alert">
              {state.error}
            </p>
          )}

          {state.drivers.length > 0 && (
            <div className="opsTableScroll" role="region" aria-labelledby="drivers-caption" tabIndex={0}>
              <table className="opsTable">
                <caption id="drivers-caption">
                  Repartidores de tu flota, por referencia. Las paradas activas son las que cada uno tiene en curso
                  ahora, las mismas que ve en su app.
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Repartidor</th>
                    <th scope="col">Vehículo</th>
                    <th scope="col">Estado</th>
                    <th scope="col">Paradas activas</th>
                    <th scope="col">Última ubicación</th>
                  </tr>
                </thead>
                <tbody>
                  {state.drivers.map((driver) => (
                    <DriverRow key={driver.driver_id} driver={driver} />
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {state.loaded && state.drivers.length === 0 && state.nextCursor === null && state.message === null && (
            <EmptyState title="Sin repartidores">Tu flota todavía no tiene repartidores registrados.</EmptyState>
          )}
          {state.phase === "ready" && !state.loaded && state.error === null && state.message === null && (
            <p className="live" aria-live="polite">
              Cargando repartidores…
            </p>
          )}

          {state.drivers.length > 0 && (
            <p className="opsSummaryNote">
              {state.drivers.length === 1 ? "Se muestra 1 repartidor" : `Se muestran ${state.drivers.length} repartidores`}
              {state.nextCursor !== null ? "; hay más por cargar." : "."}
            </p>
          )}
          {state.nextCursor !== null && (
            <button type="button" className="btn btnSecondary" onClick={() => void controller.loadMore()}
              disabled={state.loadingMore}>
              {state.loadingMore ? "Cargando…" : "Cargar más"}
            </button>
          )}
          <p className="live" aria-live="polite">
            {state.loading && state.loaded ? "Actualizando la lista de repartidores." : ""}
          </p>
        </>
      )}
    </div>
  );
}

function DriverRow({ driver }: { readonly driver: OrganizationDriver }) {
  return (
    <tr>
      <th scope="row">{driver.driver_reference}</th>
      <td>{vehicleLabel(driver.vehicle_type)}</td>
      <td>
        <span className={`statusBadge ${toneClass[organizationDriverStatusTones[driver.status]]}`}>
          <span className="statusDot" aria-hidden="true" />
          <span className="statusBadgeLabel">{organizationDriverStatusLabels[driver.status]}</span>
        </span>
      </td>
      <td>{activeStopsText(driver.active_stop_count)}</td>
      <td>{driver.last_position_at === null ? "Sin ubicación registrada" : <DateTime value={driver.last_position_at} />}</td>
    </tr>
  );
}
