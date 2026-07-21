# ADR-030 — Retención de outbox mediante rol de mantenimiento

**Estado:** Aprobado

`paqueteria_maintenance NOLOGIN BYPASSRLS` recibe únicamente SELECT/DELETE sobre los dos outbox y propiedad de `purge_outbox`/`purge_location_outbox`.

Las funciones solo eliminan `PROCESSED` y `DEAD` anteriores a cutoffs mínimos, en lotes y con dry-run. No pueden claim, settle ni tocar estados activos. Worker accede solo por EXECUTE.
