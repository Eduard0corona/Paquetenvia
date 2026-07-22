# AI-27 — Resumen de liberación v0.6

**Estado:** NORMATIVE_BASELINE_RUNTIME_VALIDATED

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

ARC-002 completó la primera verificación sobre PostgreSQL 18/PostGIS 3.6
efímero: AI-06 seguido de AI-18, bootstrap, catálogo de privilegios, RLS,
hashing C#/SQL, mapa público de 17 estados y lifecycle completo de ambos outbox,
incluida purga real y concurrente.

ARC-002 queda `DONE` después de los cinco jobs verdes del PR.

Esto valida el contrato; no constituye una migración ni una aplicación sobre la
base persistente de FND-002.
