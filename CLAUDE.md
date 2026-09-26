# CLAUDE.md — reglas de revisión y trabajo en Paquetenvia

Monolito modular .NET 10 + PostgreSQL/PostGIS multi-tenant con RLS, outbox transaccional
y tracking público seguro. Este archivo resume; la autoridad son los contratos normativos.

## Fuente de verdad

- Contratos normativos v0.6: `docs/normative/v0.6/` (orden de lectura en `AI-00_README_FIRST.md`).
  - Contrato del agente: `AI-01_AGENT_OPERATING_CONTRACT.md` (leer primero).
  - Dominio/estados: `specs/AI-04_DOMAIN_MODEL.yaml`; runtime: `specs/AI-24_RUNTIME_HARDENING_CONTRACT.yaml`.
  - API: `contracts/AI-05_OPENAPI.yaml`; SignalR: `contracts/AI-12_SIGNALR_CONTRACT.yaml`.
  - SQL/roles: `database/AI-06_SCHEMA.sql`, `database/AI-18_DATABASE_ROLE_MODEL.sql`.
  - Solución .NET y flujos atómicos: `specs/AI-13_DOTNET_SOLUTION_BLUEPRINT.md`; backlog: `specs/AI-08_BACKLOG.yaml`.
- Cualquier cambio en `docs/normative/v0.6/**` debe mantener sincronizados `CHECKSUMS_SHA256.txt`
  y `MANIFEST.json` (`python3 docs/normative/v0.6/tools/validate_contracts.py` → `VALIDATION_OK`).
- Notas de implementación por tarea: `docs/development/`; CI y ramas: `docs/development/branching-and-ci.md`.

## Invariantes v0.6 que la revisión debe verificar (AI-01 §4)

1. Toda consulta tenant corre dentro de una transacción explícita; `set_config(..., true)` después
   de `BEGIN`; `current_org_ids` es parámetro `uuid[]` (`{}` vacío, nunca `NULL`).
2. API y Worker son `NOBYPASSRLS`; roles privilegiados `NOLOGIN`, accesibles solo vía `EXECUTE`.
   Autorización en backend + RLS; la UI nunca es barrera. `owner_org_id` ≠ `operator_org_id`.
3. Outbox: sin `SELECT/UPDATE/DELETE` directo desde runtime; productores dan todos los valores
   (`ValueGeneratedNever`, sin `RETURNING`); claim/settle/requeue con `lease_token`; purga solo
   `PROCESSED`/`DEAD` antiguos vía funciones de maintenance. Outbox y auditoría en la misma transacción.
4. Dinero siempre en centavos `long`/`bigint`/`int64`; prohibido punto flotante.
5. Sin PII en logs, snapshots, outbox ni eventos públicos; sin secretos ni datos de tarjeta.
   Tracking público solo expone el mapa AI-04 + `public_event_code`; falla cerrado (404 uniforme).
6. `order_acceptances`, `order_events`, `proofs` y `audit_logs` son append-only.
7. Solo los 5 flujos atómicos cross-module de AI-13 §4 (quote→order, assignment→order,
   POD/custody→order, COD reconciliation→close, external offer→assignment).
   Un sexto flujo requiere ADR y pruebas.
8. `pgcrypto` como `extensions.*`; PostGIS en `public`, sin `CREATE` en runtime.
9. Migraciones por módulo con up/down probados en PostgreSQL real; nada de NestJS, BullMQ,
   microservicios ni Kubernetes sin ADR.

Severidad de hallazgos (AI-01 §7): `BLOCKER` seguridad, dinero, legal, privacidad, aislamiento o
cobertura; `MAJOR` cambio reversible de experiencia/operación (requiere ADR); `MINOR` convención.

## Pull requests

- Siempre en **borrador**; el owner decide cuándo marcar listo y fusionar. Nunca auto-merge,
  push directo a `main`/`development`, rebase ni force-push de ramas compartidas.
- Flujo: rama de tarea → `development` (PR Validation) → PR de promoción `development` → `main`
  (Foundation CI, 13 jobs). Excepción: PR de dependencias hacia `main` + back-sync (ver doc de CI).
- El cuerpo del PR usa la salida de `AI-11_TASK_PROMPT_TEMPLATE.md`:

```text
Task / Status / Changed files / Database migration / Commands run / Tests and evidence /
Acceptance criteria / Security assertions / Contract drift checks / Residual risks / Rollback
```

- Un PR = una tarea del backlog. Cambios en `docs/normative/**` solo con decisión registrada en
  `decision-log.md`.

## Revisión automática

El bot de revisión lee el diff (`gh pr diff`), comenta en línea solo problemas concretos y publica
un único resumen con conteo BLOCKER/MAJOR/MINOR. No edita archivos, no aprueba, no fusiona.
