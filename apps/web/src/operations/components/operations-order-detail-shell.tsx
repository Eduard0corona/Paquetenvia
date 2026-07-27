"use client";

import Link from "next/link";
import type { OperationsDashboardOrder } from "../contracts/operations-dashboard";
import {
  formatMazatlanTime,
  orderStatusLabels,
  serviceTypeLabel,
  timelineLabel,
} from "../contracts/operations-formatters";
import { useOperationsOrderDetail } from "../state/use-operations-order-detail";

export function OperationsOrderDetailShell({
  orderId,
}: {
  readonly orderId: string;
}) {
  const state = useOperationsOrderDetail(orderId);
  if (state.notFound) {
    return (
      <main className="opsShell opsDetail">
        <h1>Orden no disponible</h1>
        <p>No es posible mostrar esta orden.</p>
        <Link className="opsPrimary" href="/ops/dashboard">
          Volver al tablero
        </Link>
      </main>
    );
  }
  if (state.accessUnavailable) {
    return (
      <main className="opsShell opsDetail">
        <h1>Acceso no disponible</h1>
        <p>No es posible mostrar información de operaciones.</p>
      </main>
    );
  }
  if (state.order === null || state.projection === null) {
    return (
      <main className="opsShell opsDetail" aria-busy={state.loading}>
        <h1>Detalle de orden</h1>
        <p>{state.error ?? "Cargando información operativa…"}</p>
      </main>
    );
  }

  const { order, projection } = state;
  return (
    <main className="opsShell opsDetail" aria-busy={state.loading}>
      <header className="opsHeader">
        <div>
          <p className="opsEyebrow">Orden</p>
          <h1>{order.public_id}</h1>
          <p className="opsStatus">{orderStatusLabels[order.status]}</p>
        </div>
        <div className="opsHeaderStatus" aria-live="polite">
          <span>{state.connection}</span>
          <span>
            Última actualización:{" "}
            {state.lastUpdated === null
              ? "Pendiente"
              : formatMazatlanTime(state.lastUpdated)}
          </span>
          <button type="button" className="opsPrimary" onClick={state.refresh}>
            Actualizar
          </button>
          <Link className="opsSecondary" href="/ops/dashboard">
            Volver al tablero
          </Link>
        </div>
      </header>
      <p className="opsTimezone">Horarios mostrados en hora de Mazatlán.</p>

      <section className="opsDetailGrid" aria-label="Resumen de la orden">
        <Detail label="Owner" value={projection.owner.display_name} />
        <Detail
          label="Operator"
          value={projection.operator?.display_name ?? "Sin operador"}
        />
        <Detail
          label="Cliente"
          value={projection.client?.display_name ?? "No disponible"}
        />
        <Detail label="Servicio" value={serviceTypeLabel(order.service_type)} />
        <Detail
          label="Zona"
          value={projection.delivery_zone?.name ?? "Sin zona asignada"}
        />
        <Detail label="Recolección" value="Por confirmar" />
        <Detail label="Entrega" value="Por confirmar" />
        <Detail
          label="Asignación"
          value={assignmentText(projection)}
        />
        <Detail
          label="Última posición"
          value={
            projection.latest_driver_location === null
              ? "No disponible"
              : `${formatMazatlanTime(
                  projection.latest_driver_location.captured_at,
                )}; precisión aproximada ${Math.round(
                  projection.latest_driver_location.accuracy_m,
                )} m`
          }
        />
        <Detail
          label="Precio"
          value={
            projection.cost_warning === null
              ? "Sin advertencias"
              : "Revisar precio"
          }
        />
      </section>

      {projection.unassigned_alert && (
        <p className="opsAlert" role="status">
          Requiere asignación
        </p>
      )}

      <section aria-labelledby="timeline-title">
        <h2 id="timeline-title">Timeline</h2>
        <ol className="opsTimeline">
          {order.timeline.map((item, index) => (
            <li key={`${item.occurred_at}-${index}`}>
              <strong>{timelineLabel(item.event_type)}</strong>
              <time dateTime={item.occurred_at}>
                {formatMazatlanTime(item.occurred_at)}
              </time>
            </li>
          ))}
        </ol>
      </section>

      <Unavailable
        title="Proofs"
        text="Resumen no disponible en el contrato de lectura actual."
      />
      <Unavailable
        title="Incidencias"
        text="Disponible después de INC-001."
      />
      <Unavailable title="Ruta" text="Disponible después de RTE-001." />

      <section aria-labelledby="actions-title">
        <h2 id="actions-title">Acciones futuras</h2>
        <div className="opsDisabledActions">
          {[
            ["Asignar repartidor propio", "Requiere discovery de conductores."],
            ["Publicar oferta externa", "Disponible después de EXT-001."],
            ["Agregar a ruta", "Disponible después de RTE-001."],
            ["Abrir incidencia", "Disponible después de INC-001."],
          ].map(([label, reason]) => (
            <button key={label} type="button" disabled title={reason}>
              {label}
            </button>
          ))}
        </div>
      </section>
    </main>
  );
}

function Detail({ label, value }: { readonly label: string; readonly value: string }) {
  return (
    <article>
      <h2>{label}</h2>
      <p>{value}</p>
    </article>
  );
}

function Unavailable({
  title,
  text,
}: {
  readonly title: string;
  readonly text: string;
}) {
  return (
    <section className="opsUnavailable">
      <h2>{title}</h2>
      <p>{text}</p>
    </section>
  );
}

function assignmentText(order: OperationsDashboardOrder): string {
  if (order.assignment === null) return "Sin asignación";
  return `${order.assignment.assignment_type} · ${order.assignment.driver_reference}`;
}
