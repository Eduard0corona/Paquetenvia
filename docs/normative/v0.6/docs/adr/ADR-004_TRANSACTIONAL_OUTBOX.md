# ADR-004 — Transactional Outbox

**Estado:** Aprobado

## Decisión
Persistir eventos externos en la misma transacción que el aggregate y procesarlos mediante Worker idempotente.

## Consecuencias
Consistencia entre DB y notificaciones; requiere monitoreo, retry y estado de revisión.
