import type {
  PublicOrderStatus,
  PublicTimelineCode,
} from "./public-tracking";

export const publicStatusLabels: Readonly<Record<PublicOrderStatus, string>> = {
  CREATED: "Pedido creado",
  SCHEDULED: "Recolección programada",
  IN_TRANSIT: "En tránsito",
  OUT_FOR_DELIVERY: "En reparto",
  DELIVERY_EXCEPTION: "Hay una incidencia en la entrega",
  DELIVERED: "Entregado",
  RETURNING: "En devolución",
  RETURNED: "Devuelto",
  CANCELLED: "Cancelado",
};

export const publicTimelineLabels: Readonly<
  Record<PublicTimelineCode, string>
> = {
  ORDER_CREATED: "Pedido creado",
  PICKUP_SCHEDULED: "Recolección programada",
  PICKED_UP: "Paquete recolectado",
  IN_TRANSIT: "En tránsito",
  OUT_FOR_DELIVERY: "En reparto",
  DELIVERY_ATTEMPTED: "Intento de entrega",
  RESCHEDULED: "Entrega reprogramada",
  DELIVERED: "Entregado",
  RETURNING: "En devolución",
  RETURNED: "Devuelto",
  CANCELLED: "Cancelado",
};
