import type { DriverStop } from "./driver-stop";

const typeLabels: Readonly<Record<DriverStop["stop_type"], string>> = {
  PICKUP: "Recolección",
  DELIVERY: "Entrega",
  RETURN: "Devolución",
};

const statusLabels: Readonly<Record<DriverStop["status"], string>> = {
  ASSIGNED: "Asignada",
  AT_PICKUP: "En punto de recolección",
  PICKED_UP: "Recolectada",
  IN_TRANSIT: "En tránsito",
  DELIVERING: "En reparto",
  FAILED_ATTEMPT: "Intento fallido",
  RESCHEDULED: "Reprogramada",
  RETURNING: "En devolución",
};

export function driverStopTypeLabel(value: DriverStop["stop_type"]): string {
  return typeLabels[value];
}

export function driverStopStatusLabel(value: DriverStop["status"]): string {
  return statusLabels[value];
}
