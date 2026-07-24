# Paquete de implementación para agentes de IA

**Proyecto:** [NOMBRE COMERCIAL PENDIENTE] — plataforma de paquetería local en Culiacán  
**Versión:** 0.6  
**Fecha:** 2026-07-23
**Estado:** línea base normativa de seguridad/runtime y contrato incremental DSP-002 validados en PostgreSQL 18/PostGIS 3.6. Sustituye completamente a v0.5.

## Propósito

Este paquete traduce el expediente de negocio y operación a contratos ejecutables para un agente de desarrollo. YAML, SQL, OpenAPI y Gherkin son normativos; Markdown explica decisiones, orden de trabajo y riesgos.

## Orden obligatorio de lectura

1. `AI-01_AGENT_OPERATING_CONTRACT.md`
2. `specs/AI-02_PRODUCT_CONTRACT.yaml`
3. `specs/AI-03_ARCHITECTURE.md`
4. `specs/AI-04_DOMAIN_MODEL.yaml`
5. `specs/AI-24_RUNTIME_HARDENING_CONTRACT.yaml`
6. `contracts/AI-05_OPENAPI.yaml`
7. `database/AI-06_SCHEMA.sql`
8. `database/AI-18_DATABASE_ROLE_MODEL.sql`
9. `contracts/AI-12_SIGNALR_CONTRACT.yaml`
10. `specs/AI-13_DOTNET_SOLUTION_BLUEPRINT.md`
11. `specs/AI-15_SCALABILITY_CONTRACT.yaml`
12. `specs/AI-07_UI_CONTRACTS.yaml`
13. `specs/AI-08_BACKLOG.yaml`
14. `tests/AI-09_ACCEPTANCE_TESTS.feature`
15. `specs/AI-10_DECISIONS_AND_GATES.yaml`
16. `docs/adr/`
17. `docs/AI-25_V0.6_CHANGESET.md`
18. `docs/AI-26_VALIDATION_REPORT.md`
19. `AI-11_TASK_PROMPT_TEMPLATE.md`

## Jerarquía de autoridad

1. Producto y decisiones aprobadas: `AI-02`.
2. Dominio, estados públicos y seguridad: `AI-04`.
3. Runtime hardening: `AI-24`.
4. OpenAPI, SQL, roles y SignalR.
5. Backlog y Gherkin.
6. ADR y `decision-log.md`.

Una contradicción no se resuelve silenciosamente: se registra, se bloquea la tarea afectada y se solicita decisión.

## Decisiones v0.6

- `pgcrypto` vive en `extensions`; PostGIS permanece en `public` sin permiso runtime de creación.
- El token público usa 32 bytes aleatorios, Base64URL sin padding y SHA-256 sobre sus bytes UTF-8 exactos.
- Los outbox usan leases. Claim, settle y requeue pertenecen a un rol executor; purga pertenece a maintenance.
- API y Worker no tienen `SELECT`, `UPDATE` ni `DELETE` directo sobre los lanes.
- Los productores generan UUID, timestamps y estados; EF Core no usa valores generados ni `RETURNING`.
- Tracking público usa vocabulario propio y timeline privado por defecto mediante `public_event_code`.
- `order_acceptances` conserva evidencia legal canónica, RLS y append-only.
- Todo dinero se almacena como `bigint` en centavos y se expone como `int64`.
- Provisioning de usuario/organización usa UUIDs preautorizados solo dentro de la transacción.
- `app.current_org_ids` se envía como parámetro `uuid[]`, vacío `{}`, nunca `NULL`.

## Tareas temporalmente bloqueadas hasta consumir v0.6

`SEC-002`, `TRK-001`, `TEN-001`, `ORD-001`, `OPS-001`, `DBA-001`, `DRV-003`, `SCL-006`, `OPS-003` y `OPS-004`.

Pueden iniciar con estos contratos: `FND-001`, `ARC-001` y `FND-002`.

## Resultado MVP-0

Un entorno reproducible debe completar 20 entregas sintéticas sin pérdida de eventos, fuga tenant ni POD incompleto; recuperar leases abandonados; negar settles obsoletos; producir tracking público minimizado y validar bootstrap/hash/mapeos contra PostgreSQL real.
## Identidad de esta entrega consolidada

Este directorio pertenece al bundle `v0.6-full-canonical-sync-4-dsp002-contract-remediation`. Para evitar mezclar copias intermedias, validar primero `CANONICAL_SOURCE_OF_TRUTH.md`, `CLAUDE_VALIDATION_HANDOFF.md`, `MANIFEST.json` y `CHECKSUMS_SHA256.txt`.

ARC-002 está `DONE` después de pasar los cinco jobs de CI: AI-06 y AI-18 se
ejecutaron en PostgreSQL 18/PostGIS 3.6 efímero, incluida purga real y
concurrente para ambos lanes. Esto no autoriza migraciones ni ejecución contra
la base persistente de FND-002.

DSP-002 formaliza en AI-05 su matriz 201/401/403/404/409, `route_id` nullable
pero no nulo durante este incremento y `OWN` como única capacidad habilitada.
La adopción valida el AI-06 existente sin modificar su schema canónico.
