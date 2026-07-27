import Link from "next/link";
import type { OperationsDashboardOrder } from "../contracts/operations-dashboard";
import {
  formatMazatlanTime,
  orderStatusLabels,
  serviceTypeLabel,
} from "../contracts/operations-formatters";
import { operationsOrderHref } from "../routing/operations-routing";

export function OperationsOrderCard({
  order,
}: {
  readonly order: OperationsDashboardOrder;
}) {
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
        <Row label="Entrega" value="Por confirmar" />
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
      <Link className="opsPrimary" href={operationsOrderHref(order.order_id)}>
        Abrir orden
      </Link>
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
