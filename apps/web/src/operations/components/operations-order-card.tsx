"use client";

import Link from "next/link";
import { useRef, useState } from "react";
import type { ConfirmRequest } from "../../components/confirm-dialog";
import { externalOfferConfirmation } from "../contracts/external-offer-confirmation";
import type { OperationsDashboardOrder } from "../contracts/operations-dashboard";
import {
  assignmentTypeLabel,
  serviceTypeLabel,
} from "../contracts/operations-formatters";
import { DateTime } from "../../components/ui/date-time";
import { DescriptionList } from "../../components/ui/description-list";
import { StatusBadge } from "../../components/ui/status-badge";
import { parseMxnToCents } from "../contracts/money";
import { formatServiceWindow } from "../contracts/service-window";
import { operationsOrderHref } from "../routing/operations-routing";

export function OperationsOrderCard({
  order,
  confirm,
  onPublishExternalOffer,
}: {
  readonly order: OperationsDashboardOrder;
  readonly confirm: (request: ConfirmRequest) => void;
  readonly onPublishExternalOffer: (
    orderId: string,
    commissionCents: number,
    expiresAt: string,
    vehicleType: "MOTORCYCLE" | "CAR" | "VAN" | "BICYCLE" | "WALKER",
    idempotencyKey: string,
  ) => Promise<void>;
}) {
  const [publishing, setPublishing] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const keyRef = useRef<string | null>(null);
  const canPublish = order.assignment === null &&
    ["READY_FOR_PICKUP", "RESCHEDULED"].includes(order.status);
  return (
    <article className="opsOrderCard">
      <div className="opsCardHeading">
        <h3>{order.public_id}</h3>
        <StatusBadge status={order.status} showGroup={false} />
      </div>
      <DescriptionList
        items={[
          { label: "Dueño", value: order.owner.display_name },
          { label: "Opera", value: order.operator?.display_name ?? "Sin operador" },
          { label: "Cliente", value: order.client?.display_name ?? "No disponible" },
          { label: "Zona", value: order.delivery_zone?.name ?? "Sin zona asignada" },
          { label: "Servicio", value: serviceTypeLabel(order.service_type) },
          { label: "Recolección", value: "Por confirmar" },
          { label: "Entrega", value: formatServiceWindow(order.delivery_window) },
          { label: "Asignación", value: assignmentTypeLabel(order.assignment?.assignment_type) },
          { label: "Repartidor", value: order.assignment?.driver_reference ?? "Sin repartidor" },
          { label: "Actualizada", value: <DateTime value={order.updated_at} /> },
        ]}
      />
      {order.unassigned_alert && (
        <p className="notice noticeCrit" role="status">
          Requiere asignación
        </p>
      )}
      {order.cost_warning !== null && (
        <p className="notice noticeWarn" title="Revisión operativa de precio requerida.">
          Revisar precio
        </p>
      )}
      {canPublish ? (
        <form
          className="opsExternalOffer"
          onSubmit={(event) => {
            event.preventDefault();
            if (publishing) return;
            const data = new FormData(event.currentTarget);
            const commissionCents = parseMxnToCents(String(data.get("commission") ?? ""));
            const expires = String(data.get("expires"));
            const vehicle = String(data.get("vehicle")) as
              | "MOTORCYCLE" | "CAR" | "VAN" | "BICYCLE" | "WALKER";
            const expiresAt = new Date(expires);
            if (commissionCents === null || !expires || Number.isNaN(expiresAt.getTime())) {
              setMessage("Completa una comisión y una expiración válidas.");
              return;
            }
            confirm({
              ...externalOfferConfirmation(order.public_id, commissionCents, expiresAt, vehicle),
              onConfirm: () => {
                const key = keyRef.current ?? crypto.randomUUID();
                keyRef.current = key;
                setPublishing(true);
                setMessage(null);
                void onPublishExternalOffer(
                  order.order_id,
                  commissionCents,
                  expiresAt.toISOString(),
                  vehicle,
                  key,
                ).then(() => {
                  keyRef.current = null;
                  setMessage("Oferta externa publicada.");
                }).catch(() => {
                  setMessage("No fue posible publicar la oferta.");
                }).finally(() => setPublishing(false));
              },
            });
          }}
        >
          <strong>Publicar oferta externa</strong>
          <label>Comisión (MXN)<input name="commission" type="text" inputMode="decimal" pattern="\d{1,13}(\.\d{1,2})?" placeholder="45.00" required /></label>
          <label>Expiración<input name="expires" type="datetime-local" required /></label>
          <label>Vehículo<select name="vehicle" defaultValue="MOTORCYCLE">
            <option value="MOTORCYCLE">Motocicleta</option>
            <option value="CAR">Automóvil</option>
            <option value="VAN">Van</option>
            <option value="BICYCLE">Bicicleta</option>
            <option value="WALKER">A pie</option>
          </select></label>
          <button className="btn btnPrimary" type="submit" disabled={publishing}>
            {publishing ? "Publicando..." : "Publicar oferta"}
          </button>
          <span role="status" aria-live="polite">{message}</span>
        </form>
      ) : null}
      <Link className="btn btnPrimary" href={operationsOrderHref(order.order_id)}>
        Abrir orden
      </Link>
      {order.assignment?.assignment_type === "OWN" &&
      ["ACCEPTED", "ACTIVE"].includes(order.assignment.status) ? (
        <Link className="btn btnPrimary" href={`/ops/routes?orderId=${encodeURIComponent(order.order_id)}`}>
          Agregar a ruta
        </Link>
      ) : null}
    </article>
  );
}
