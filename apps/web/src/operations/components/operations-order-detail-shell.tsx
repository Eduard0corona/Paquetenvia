"use client";

import Link from "next/link";
import type { OperationsDashboardOrder } from "../contracts/operations-dashboard";
import {
  assignmentTypeLabel,
  serviceTypeLabel,
  timelineLabel,
} from "../contracts/operations-formatters";
import { formatServiceWindow } from "../contracts/service-window";
import { DateTime } from "../../components/ui/date-time";
import { DescriptionList } from "../../components/ui/description-list";
import { PageHeader } from "../../components/ui/page-header";
import { StatusBadge } from "../../components/ui/status-badge";
import { useOperationsOrderDetail } from "../state/use-operations-order-detail";
import { OperationsDriverAssignment } from "./operations-driver-assignment";
import { OperationsNextStep } from "./operations-next-step";
import { OperationsTrackingLink } from "./operations-tracking-link";

export function OperationsOrderDetailShell({
  orderId,
}: {
  readonly orderId: string;
}) {
  const state = useOperationsOrderDetail(orderId);
  if (state.notFound) {
    return (
      <div className="page">
        <PageHeader title="Orden no disponible" description="No es posible mostrar esta orden." />
        <div className="opsFormActions">
          <Link className="btn btnSecondary" href="/ops/dashboard">
            Volver al tablero
          </Link>
        </div>
      </div>
    );
  }
  if (state.accessUnavailable) {
    return (
      <div className="page">
        <PageHeader
          title="Acceso no disponible"
          description="No es posible mostrar información de operaciones."
        />
      </div>
    );
  }
  if (state.order === null || state.projection === null) {
    return (
      <div className="page" aria-busy={state.loading}>
        <PageHeader
          title="Detalle de orden"
          description={state.error ?? "Cargando información operativa…"}
        />
      </div>
    );
  }

  const { order, projection } = state;
  return (
    <div className="page" aria-busy={state.loading}>
      <PageHeader
        eyebrow="Orden"
        title={order.public_id}
        description={<StatusBadge status={order.status} />}
        live
        actions={
          <>
            <span className="opsConnection">{state.connection}</span>
            <span>
              Última actualización:{" "}
              {state.lastUpdated === null ? "Pendiente" : <DateTime value={state.lastUpdated} />}
            </span>
            <button type="button" className="btn btnSecondary" onClick={state.refresh}>
              Actualizar
            </button>
          </>
        }
      />
      <p className="pageNote">Horarios mostrados en hora de Mazatlán.</p>

      <DescriptionList
        variant="grid"
        label="Resumen de la orden"
        items={[
          { label: "Dueño", value: projection.owner.display_name },
          { label: "Opera", value: projection.operator?.display_name ?? "Sin operador" },
          { label: "Cliente", value: projection.client?.display_name ?? "No disponible" },
          { label: "Servicio", value: serviceTypeLabel(order.service_type) },
          { label: "Zona", value: projection.delivery_zone?.name ?? "Sin zona asignada" },
          { label: "Recolección", value: "Por confirmar" },
          { label: "Entrega", value: formatServiceWindow(order.service_window) },
          { label: "Asignación", value: assignmentText(projection) },
          {
            label: "Última posición",
            value:
              projection.latest_driver_location === null ? (
                "No disponible"
              ) : (
                <>
                  <DateTime value={projection.latest_driver_location.captured_at} />; precisión
                  aproximada {Math.round(projection.latest_driver_location.accuracy_m)} m
                </>
              ),
          },
          {
            label: "Precio",
            value: projection.cost_warning === null ? "Sin advertencias" : "Revisar precio",
          },
        ]}
      />

      {projection.unassigned_alert && (
        <p className="notice noticeCrit" role="status">
          Requiere asignación
        </p>
      )}

      <OperationsNextStep
        orderId={order.id}
        publicId={order.public_id}
        version={order.version}
        allowedTransitions={order.allowed_transitions}
        onOrderChanged={state.refresh}
      />

      <OperationsDriverAssignment
        orderId={order.id}
        publicId={order.public_id}
        assignable={admitsAssignment(order.status, projection)}
        onOrderChanged={state.refresh}
      />

      <section aria-labelledby="timeline-title">
        <h2 id="timeline-title">Historial</h2>
        <ol className="opsTimeline">
          {order.timeline.map((item, index) => (
            <li key={`${item.occurred_at}-${index}`}>
              <strong>{timelineLabel(item.event_type)}</strong>
              <DateTime value={item.occurred_at} />
            </li>
          ))}
        </ol>
      </section>

      <OperationsTrackingLink
        orderId={order.id}
        ownerOrganizationId={order.owner_org_id}
      />

      <section aria-labelledby="actions-title">
        <h2 id="actions-title">Otras acciones</h2>
        <p>
          Para publicar una oferta externa o agregar la orden a una ruta, búscala en el
          tablero de Operaciones. Los intentos fallidos se registran en Incidencias.
        </p>
        <div className="opsFormActions">
          <Link className="btn btnSecondary" href="/ops/dashboard">
            Publicar oferta externa desde el tablero
          </Link>
          <Link className="btn btnSecondary" href="/ops/incidents">
            Abrir incidencia
          </Link>
        </div>
      </section>
    </div>
  );
}

function assignmentText(order: OperationsDashboardOrder): string {
  if (order.assignment === null) return assignmentTypeLabel(null);
  return `${assignmentTypeLabel(order.assignment.assignment_type)} · ${order.assignment.driver_reference}`;
}

/** assignDriver accepts only READY_FOR_PICKUP or RESCHEDULED orders without an active assignment. */
function admitsAssignment(status: string, order: OperationsDashboardOrder): boolean {
  return (status === "READY_FOR_PICKUP" || status === "RESCHEDULED") && order.assignment === null;
}
