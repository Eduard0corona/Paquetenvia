# ADR-004 — Transactional Outbox

**Estado:** Aprobado

## Decisión
Persistir eventos externos en la misma transacción que el aggregate y procesarlos mediante Worker idempotente.

## Consecuencias
Consistencia entre DB y notificaciones; requiere monitoreo, retry y estado de revisión.

## Adenda 2026-09-26: consumo interno entre módulos (D8)

Decisión del project owner `D8-DISPATCH-OUTBOX-CLOSURE`: el outbox también
transporta hechos internos entre módulos cuando se acepta la consistencia
eventual. Dispatch cierra asignaciones reaccionando a cambios de estado de la
orden, sin coordinador transaccional nuevo.

Hoy el ruteo `security.resolve_outbox_consumer(topic)` es exclusivo, un solo
consumer por fila. Por eso un segundo consumer requiere un topic propio
(propuesta del PR #90: `dispatch.order-status-reaction-requested` con lane
`DISPATCH`) o un fan-out por consumer, que exigiría un ADR aparte. La
implementación del lane queda pendiente de la aprobación del cambio SQL de
AI-06/AI-18 y de su migración.
