# ADR-008 — Almacenamiento de evidencias en objetos

**Estado:** Aprobado

## Decisión
Guardar binarios en S3-compatible y metadata/hash en PostgreSQL. Acceso mediante URL firmada y flujo de cuarentena/validación.

## Consecuencias
API stateless, menor presión en DB y retención controlable.
