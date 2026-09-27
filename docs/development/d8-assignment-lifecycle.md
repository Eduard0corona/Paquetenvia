# D8: cierre de asignaciones por reacción al outbox

## Decisiones

- `D8-DISPATCH-OUTBOX-CLOSURE` (2026-09-26): Dispatch cierra sus asignaciones
  reaccionando por outbox a las transiciones de orden ya confirmadas, con
  consistencia eventual. No hay un sexto flujo atómico (AI-13 §4).
- `D8-OUTBOX-LANE-DISPATCH` (2026-09-27): lane `DISPATCH` con
  `claim_dispatch_outbox` y `requeue_stale_dispatch_outbox`, propiedad de
  `paqueteria_outbox_executor`, `EXECUTE` sólo para `paqueteria_worker`.
- `AI12-ASSIGNMENT-TERMINAL-STATES` (2026-09-27): `AssignmentChanged` admite
  `COMPLETED` y `CANCELLED`.
- `D8-REASSIGNMENT-NEW-ASSIGNMENT` (2026-09-26/27): al reprogramar, la asignación
  anterior se cierra como `CANCELLED`; la reasignación siempre crea una
  asignación nueva (DSP-002 u oferta externa nueva).

## Fan-out

`security.resolve_outbox_consumer` devuelve un único consumidor por topic y cada
fila tiene un único `status`/`lease_token`, así que una fila no puede atenderse
en dos lanes. Por eso ORD-002 escribe, en la misma transacción, una fila
adicional `dispatch.order-status-reaction-requested` (payload
`order-status-reaction-v1`) sólo cuando:

1. la transición cierra una asignación (`DispatchReactionRequestPolicy`, igual al
   mapa de Dispatch; una prueba unitaria lo verifica), y
2. hay una asignación `ACCEPTED/ACTIVE` bajo el lock de la orden; la fila nombra
   su `assignment_id`.

`orders.status-changed` no cambia: mismo payload, mismo topic, lane `REALTIME`.

## Mapa de cierre (las 30 transiciones de AI-04)

| Transición | Asignación |
| --- | --- |
| `*→CANCELLED` | `CANCELLED` |
| `ASSIGNED→READY_FOR_PICKUP` (desasignar) | `CANCELLED` |
| `FAILED_ATTEMPT→RESCHEDULED` | `CANCELLED` |
| `RESCHEDULED→READY_FOR_PICKUP` | `CANCELLED` (respaldo) |
| `DELIVERING→DELIVERED`, `RETURNING→RETURNED` | `COMPLETED` |
| resto, incluido `FAILED_ATTEMPT→DELIVERING` (reintento) y `*→RETURNING` | sin cambio |

`RESCHEDULED→ASSIGNED` no cierra nada: exige una asignación nueva, que DSP-002
sólo crea cuando ya no hay una activa.

## Consumidor

`AssignmentLifecycleDispatcher` (Worker, `Dispatch:AssignmentLifecycle`,
`Provider=Disabled` por defecto) reclama con `claim_dispatch_outbox` y, en una
sola transacción como `paqueteria_worker` con el contexto tenant del dueño:

1. `UPDATE dispatch.assignments` de la asignación nombrada, sólo si sigue
   `ACCEPTED/ACTIVE`;
2. si cerró: fila `dispatch.assignment-changed` (`assignment-changed-v1`,
   `aggregate_version` = versión de la orden en la transición) y auditoría
   `ASSIGNMENT_CLOSED`;
3. `settle_outbox(PROCESSED)` con el `lease_token`; si el lease se perdió, todo
   se revierte.

Una reentrega encuentra la asignación cerrada (no-op). Una reentrega tardía no
toca una asignación nueva porque su id es otro. Payload inválido → `DEAD`
(`INVALID_PAYLOAD`); fallo de reacción → `RETRY` con backoff hasta
`MaximumAttempts`, luego `DEAD`. Leases vencidos: `requeue_stale_dispatch_outbox`.

Realtime valida un `AssignmentChanged` cerrado contra la asignación en ese
estado y el `order_event` de esa versión; el conductor afectado recibe el evento
sólo si su perfil, usuario y membresía `DRIVER` siguen activos.

## Migración

Lane Notifications (dueño del resolvedor, precedente NTF-001/EXT-001/RTE-001):
`20260927000200_AddDispatchOutboxLane`. `Down` retira el lane sólo si no hay
filas `dispatch.order-status-reaction-requested` activas.

## Riesgos abiertos

- `RESCHEDULED→DELIVERING` exige una asignación válida (guard de ORD-002), pero
  entrar a `RESCHEDULED` la cancela; con D8 esa arista sólo pasa si la reacción
  aún no corrió. Las guardas de AI-04 son de PR #91.
- Hasta que la reacción corre, DSP-002 responde `ActiveAssignmentExists`
  (consistencia eventual).
