# AI-23 — Resumen de liberación v0.5

**Estado:** NORMATIVE_BASELINE

La v0.5 consolida la revisión de seguridad, aislamiento, contratos y escalabilidad previa al desarrollo. Sustituye a v0.4 y debe utilizarse como única fuente normativa para generar migraciones y código.

## Cifras del paquete

- 17 estados de orden sincronizados entre producto, dominio, OpenAPI y SQL.
- 23 paths API y 43 schemas OpenAPI.
- 54 tareas de backlog sin ciclos ni dependencias faltantes.
- 15 schemas de módulos de negocio más `platform` y `security`.
- 10 ADR nuevos, ADR-016 a ADR-025.

## Bloqueos cerrados

- bootstrap de identidad/tracking bajo FORCE RLS;
- Worker sin BYPASSRLS global;
- privilegios append-only;
- contexto tenant transaction-local;
- evidencia por carga firmada;
- cotización de uso único y snapshot sin PII;
- multi-ciudad inicial;
- GPS batch con lane separado;
- política uniforme 401/403/404;
- COD mínimo y trazabilidad de liquidaciones.

## Inicio de implementación

Ejecutar en orden topológico desde `ARC-002`. La primera migración debe generarse desde los contratos v0.5; no reutilizar una migración basada en v0.4.
