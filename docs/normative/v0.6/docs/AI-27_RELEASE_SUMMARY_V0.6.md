# AI-27 — Resumen de liberación v0.6

**Estado:** NORMATIVE_BASELINE

v0.6 sustituye a v0.5 como fuente de verdad para código y migraciones. Conserva el monolito modular .NET/Next.js y endurece los bordes de seguridad y operación.

## Puede iniciar

- FND-001
- ARC-001
- FND-002

## Requiere consumir primero los contratos v0.6

- DBA-001
- SEC-002
- TEN-001/TEN-002/TEN-003
- TRK-001
- ORD-001
- DRV-003
- OPS-001/OPS-003/OPS-004
- SCL-006

## Primera verificación de implementación

Aplicar AI-06 y AI-18 en PostgreSQL/PostGIS real, ejecutar funciones bootstrap/outbox, verificar catálogo de privilegios, comparar hashing C#/SQL y recorrer el mapa público de 17 estados.
