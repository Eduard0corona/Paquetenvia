# ADR-005 — Multi-tenancy con defensa en profundidad

**Estado:** Aprobado

## Decisión
Usar `owner_org_id`/`operator_org_id`, autorización por recurso, RLS PostgreSQL, claves de caché/storage tenant-aware y grupos SignalR server-side.

## Consecuencias
Mayor complejidad de pruebas, pero reduce el riesgo crítico de fuga entre aliados.
