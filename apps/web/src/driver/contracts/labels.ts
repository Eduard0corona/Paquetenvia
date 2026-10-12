import type {
  DriverOperationKind,
  DriverOperationalStatus,
} from "../offline/operation-contract";
import type { DriverStop } from "./driver-stop";

const typeLabels: Readonly<Record<DriverStop["stop_type"], string>> = {
  PICKUP: "Recolección",
  DELIVERY: "Entrega",
  RETURN: "Devolución",
};

type DriverDisplayStatus = DriverStop["status"] | DriverOperationalStatus;

const statusLabels: Readonly<Record<DriverDisplayStatus, string>> = {
  ASSIGNED: "Asignada",
  AT_PICKUP: "En punto de recolección",
  PICKED_UP: "Recolectada",
  IN_TRANSIT: "En tránsito",
  DELIVERING: "En reparto",
  FAILED_ATTEMPT: "Intento fallido",
  RESCHEDULED: "Reprogramada",
  RETURNING: "En devolución",
  DELIVERED: "Entregada",
};

export function driverStopTypeLabel(value: DriverStop["stop_type"]): string {
  return typeLabels[value];
}

export function driverStopStatusLabel(value: DriverDisplayStatus): string {
  return statusLabels[value];
}

/**
 * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: the status REST confirms, as
 * the stop shows it. After a failed attempt the driver has no next step
 * (INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11), so a confirmed
 * FAILED_ATTEMPT reads "Intento fallido · El despacho decide". A projected
 * status that still waits to sync keeps the plain label.
 */
export const DriverFailedAttemptConfirmedLabel =
  "Intento fallido · El despacho decide";

export function driverConfirmedStatusLabel(value: DriverDisplayStatus): string {
  return value === "FAILED_ATTEMPT"
    ? DriverFailedAttemptConfirmedLabel
    : statusLabels[value];
}

const operationLabels: Readonly<Record<DriverOperationKind, string>> = {
  CHECK_IN: "Llegué a recolección",
  PICKUP_PROOF: "Confirmar recolección",
  START_TRANSIT: "Iniciar traslado",
  START_DELIVERY: "Llegué al destino",
  DELIVERY_PROOF: "Confirmar entrega",
  COD_COLLECTION: "Registrar cobro",
  FAILED_DELIVERY: "No se pudo entregar",
};

export function driverOperationLabel(kind: DriverOperationKind): string {
  return operationLabels[kind];
}
