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
consumer por fila. Por eso un segundo consumer requiere un topic propio.
Decisión del project owner `D8-OUTBOX-LANE-DISPATCH` (2026-09-27), según la
propuesta del PR #90: topic interno `dispatch.order-status-reaction-requested`
con lane `DISPATCH` (`claim_dispatch_outbox` y `requeue_stale_dispatch_outbox`),
propiedad de `paqueteria_outbox_executor` y con `EXECUTE` solo para
`paqueteria_worker`. No se adopta el fan-out por consumer. La traducción a
AI-06/AI-18 llega con su migración en el PR de implementación.
