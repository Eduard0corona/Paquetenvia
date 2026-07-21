# ADR-007 — Redis no autoritativo

**Estado:** Aprobado

## Decisión
Redis se usa para caché, rate limit, locks, idempotencia temporal y SignalR backplane. No conserva la única copia de órdenes, pagos, tarifas o POD.

## Consecuencias
La caída de Redis puede degradar funciones, pero no perder estado oficial.
