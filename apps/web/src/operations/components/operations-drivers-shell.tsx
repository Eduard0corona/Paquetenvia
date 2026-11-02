"use client";

import { useState } from "react";
import { useConfirmDialog } from "../../components/confirm-dialog";
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
import type { DriverDocumentSlot } from "../contracts/driver-document";
import { useDrivers } from "../state/use-drivers";
import { useMinuteClock } from "../state/use-minute-clock";
import { DriverDocumentsCell, DriverDocumentsDialog } from "./operations-driver-documents";

const toneClass = {
  muted: "statusToneMuted",
  ok: "statusToneOk",
  warn: "statusToneWarn",
} as const;

/**
 * UI-PHASE3-DRIVERS-2026-10-11 ("Lista de consulta"): the dispatcher's list of the organization's own drivers -
 * reference, vehicle, status, active stops and the time of the latest location. No personal data (GATE-007), no
 * coordinates, and drivers are not added or removed here. The backend and RLS remain the barrier; this screen only
 * shows what listOrganizationDrivers returns.
 *
 * DRV-DOC-001-IN-PERSON-2026-11-02 (AI-07 driver_roster documents): while listDriverDocuments answers
 * documents_enabled true, the "Documentos" column shows each driver's documents in one state and its button opens
 * them, to record what dispatch reviewed in person or revoke a current one. Off (the pilot until GATE-007), there is
 * no column, button or dialog.
 */
export function OperationsDriversShell() {
  const { state, controller } = useDrivers();
  const { confirm, dialog: confirmDialog } = useConfirmDialog();
  const [openDriverId, setOpenDriverId] = useState<string | null>(null);
  const blocked = state.phase === "no_session" || state.phase === "access_unavailable";
  const now = useMinuteClock();
  const openDriver =
    state.documentsEnabled && openDriverId !== null
      ? state.drivers.find((driver) => driver.driver_id === openDriverId)
      : undefined;
  const openSlots = openDriver === undefined ? undefined : state.documents[openDriver.driver_id];
  // The driver left the list (an organization switch, lost access, documents turned off): its dialog closes for good,
  // so it never reopens by itself when the same driver comes back.
  if (openDriverId !== null && openSlots === undefined) setOpenDriverId(null);
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
          {state.documentsEnabled && state.documentsError !== null && (
            <p className="notice noticeWarn" role="alert">
              {state.documentsError}
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
                    {state.documentsEnabled && <th scope="col">Documentos</th>}
                    <th scope="col">Paradas activas</th>
                    <th scope="col">Última ubicación</th>
                  </tr>
                </thead>
                <tbody>
                  {state.drivers.map((driver) => (
                    <DriverRow
                      key={driver.driver_id}
                      driver={driver}
                      documents={state.documentsEnabled ? { slots: state.documents[driver.driver_id], now } : null}
                      onOpenDocuments={() => setOpenDriverId(driver.driver_id)}
                    />
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

      {openDriver !== undefined && openSlots !== undefined && (
        <DriverDocumentsDialog
          key={openDriver.driver_id}
          reference={openDriver.driver_reference}
          slots={openSlots}
          onClose={() => setOpenDriverId(null)}
          onRecord={(input, idempotencyKey) => controller.recordDocument(openDriver.driver_id, input, idempotencyKey)}
          onRevoke={(documentId) => controller.revokeDocument(openDriver.driver_id, documentId)}
          confirm={confirm}
        />
      )}
      {confirmDialog}
    </div>
  );
}

function DriverRow({
  driver,
  documents,
  onOpenDocuments,
}: {
  readonly driver: OrganizationDriver;
  /** Null while documents are off: no column. */
  readonly documents: { readonly slots: readonly DriverDocumentSlot[] | undefined; readonly now: number } | null;
  readonly onOpenDocuments: () => void;
}) {
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
      {documents !== null && (
        <DriverDocumentsCell
          reference={driver.driver_reference}
          slots={documents.slots}
          now={documents.now}
          onOpen={onOpenDocuments}
        />
      )}
      <td>{activeStopsText(driver.active_stop_count)}</td>
      <td>{driver.last_position_at === null ? "Sin ubicación registrada" : <DateTime value={driver.last_position_at} />}</td>
    </tr>
  );
}
