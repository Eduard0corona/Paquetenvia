import type { OrderStatus } from "./operations-dashboard";

export const orderStatusLabels: Readonly<Record<OrderStatus, string>> = {
  DRAFT: "Borrador",
  CONFIRMED: "Confirmada",
  READY_FOR_PICKUP: "Lista para recolección",
  ASSIGNED: "Asignada",
  AT_PICKUP: "En punto de recolección",
  PICKED_UP: "Recolectada",
  IN_TRANSIT: "En tránsito",
  DELIVERING: "En reparto",
  FAILED_ATTEMPT: "Intento fallido",
  RESCHEDULED: "Reprogramada",
  RETURNING: "En devolución",
  RETURNED: "Devuelta",
  DELIVERED: "Entregada",
  CLOSED: "Cerrada",
  CLAIM_OPEN: "Reclamación abierta",
  CLAIM_RESOLVED: "Reclamación resuelta",
  CANCELLED: "Cancelada",
};

const timelineLabels: Readonly<Record<string, string>> = {
  ORDER_CREATED: "Orden creada",
  ORDER_STATUS_CHANGED: "Estado actualizado",
  ASSIGNMENT_CREATED: "Asignación creada",
  PICKUP_SCHEDULED: "Recolección programada",
  PICKED_UP: "Orden recolectada",
  IN_TRANSIT: "Orden en tránsito",
  OUT_FOR_DELIVERY: "Orden en reparto",
  DELIVERY_ATTEMPTED: "Intento de entrega",
  RESCHEDULED: "Orden reprogramada",
  DELIVERED: "Orden entregada",
  RETURNING: "Orden en devolución",
  RETURNED: "Orden devuelta",
  CANCELLED: "Orden cancelada",
};

const mazatlanFormatter = new Intl.DateTimeFormat("es-MX", {
  timeZone: "America/Mazatlan",
  dateStyle: "medium",
  timeStyle: "short",
  hourCycle: "h23",
});

export function formatMazatlanTime(value: string | Date): string {
  const date = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(date.getTime())) throw new Error("Invalid UTC timestamp.");
  return mazatlanFormatter.format(date);
}

export function timelineLabel(eventType: string): string {
  return timelineLabels[eventType] ?? "Actualización de la orden";
}

const assignmentTypeLabels: Readonly<Record<string, string>> = {
  OWN: "Flota propia",
  EXTERNAL: "Externa",
  ALLY_CAPACITY: "Capacidad aliada",
};

/** Human label of an assignment type; the dashboard card and the order detail share it. */
export function assignmentTypeLabel(value: string | null | undefined): string {
  if (value === null || value === undefined) return "Sin asignación";
  return assignmentTypeLabels[value] ?? "Asignada";
}

export function serviceTypeLabel(value: string): string {
  return (
    {
      SAME_DAY: "Mismo día",
      URGENT: "Urgente",
      SCHEDULED_ROUTE: "Ruta programada",
    }[value] ?? "Servicio"
  );
}
