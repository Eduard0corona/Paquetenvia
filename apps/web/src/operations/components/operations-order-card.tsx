"use client";

import Link from "next/link";
import { useRef, useState } from "react";
import type { OperationsDashboardOrder } from "../contracts/operations-dashboard";
import {
  formatMazatlanTime,
  orderStatusLabels,
  serviceTypeLabel,
} from "../contracts/operations-formatters";
import { parseMxnToCents } from "../contracts/money";
import { formatServiceWindow } from "../contracts/service-window";
import { operationsOrderHref } from "../routing/operations-routing";

export function OperationsOrderCard({
  order,
  onPublishExternalOffer,
}: {
  readonly order: OperationsDashboardOrder;
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
        <span className="opsStatus">{orderStatusLabels[order.status]}</span>
      </div>
      <dl>
        <Row label="Owner" value={order.owner.display_name} />
        <Row
          label="Operator"
          value={order.operator?.display_name ?? "Sin operador"}
        />
        <Row label="Cliente" value={order.client?.display_name ?? "No disponible"} />
        <Row
          label="Zona"
          value={order.delivery_zone?.name ?? "Sin zona asignada"}
        />
        <Row label="Servicio" value={serviceTypeLabel(order.service_type)} />
        <Row label="Recolección" value="Por confirmar" />
        <Row label="Entrega" value={formatServiceWindow(order.delivery_window)} />
        <Row
          label="Asignación"
          value={assignmentLabel(order.assignment?.assignment_type)}
        />
        <Row
          label="Repartidor"
          value={order.assignment?.driver_reference ?? "Sin repartidor"}
        />
        <Row
          label="Actualizada"
          value={formatMazatlanTime(order.updated_at)}
        />
      </dl>
      {order.unassigned_alert && (
        <p className="opsAlert" role="status">
          Requiere asignación
        </p>
      )}
      {order.cost_warning !== null && (
        <p className="opsWarning" title="Revisión operativa de precio requerida.">
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
            if (commissionCents === null || !expires) {
              setMessage("Completa una comisión y una expiración válidas.");
              return;
            }
            const key = keyRef.current ?? crypto.randomUUID();
            keyRef.current = key;
            setPublishing(true);
            setMessage(null);
            void onPublishExternalOffer(
              order.order_id,
              commissionCents,
              new Date(expires).toISOString(),
              vehicle,
              key,
            ).then(() => {
              keyRef.current = null;
              setMessage("Oferta externa publicada.");
            }).catch(() => {
              setMessage("No fue posible publicar la oferta.");
            }).finally(() => setPublishing(false));
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
          <button className="opsPrimary" type="submit" disabled={publishing}>
            {publishing ? "Publicando..." : "Publicar oferta"}
          </button>
          <span role="status" aria-live="polite">{message}</span>
        </form>
      ) : null}
      <Link className="opsPrimary" href={operationsOrderHref(order.order_id)}>
        Abrir orden
      </Link>
      {order.assignment?.assignment_type === "OWN" &&
      ["ACCEPTED", "ACTIVE"].includes(order.assignment.status) ? (
        <Link className="opsPrimary" href={`/ops/routes?orderId=${encodeURIComponent(order.order_id)}`}>
          Agregar a ruta
        </Link>
      ) : null}
    </article>
  );
}

function Row({ label, value }: { readonly label: string; readonly value: string }) {
  return (
    <div>
      <dt>{label}</dt>
      <dd>{value}</dd>
    </div>
  );
}

function assignmentLabel(value?: string): string {
  return (
    {
      OWN: "Flota propia",
      EXTERNAL: "Externa",
      ALLY_CAPACITY: "Capacidad aliada",
    }[value ?? ""] ?? "Sin asignación"
  );
}
