# ADR-019 — Contexto tenant limitado a transacción

**Estado:** Aprobado

Toda consulta tenant ocurre en una transacción explícita. Después de BEGIN se aplican `set_config(..., true)` para usuario y organizaciones. La unidad se reejecuta completa bajo la estrategia de reintentos. Consultas fuera de transacción fallan cerradas. Antes de PgBouncer se exige prueba con transaction pooling real.
