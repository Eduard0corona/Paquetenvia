# AI-26 — Informe de validación v0.6

**Fecha:** 2026-07-21
**Alcance:** validación estructural, contractual, criptográfica, de integridad y ejecución real del paquete.

## Resultado

La línea base v0.6 pasa las validaciones estáticas incluidas en `tools/validate_contracts.py`:

- 10 archivos YAML analizados correctamente.
- 56 tareas de backlog con IDs únicos, dependencias existentes y grafo acíclico.
- OpenAPI con 23 paths, 47 schemas, operationIds únicos y 180 referencias internas resueltas.
- 17 estados internos consistentes entre producto, dominio, OpenAPI y SQL.
- 17 mapeos internos→públicos consistentes entre AI-04, SQL, SignalR y OpenAPI.
- 36 tablas SQL y todas las referencias FK dirigidas a tablas existentes.
- Ocho funciones de lifecycle de outbox presentes y asociadas a roles dedicados.
- Runtime sin `SELECT`, `UPDATE` ni `DELETE` directo sobre los dos lanes.
- `pgcrypto` en `extensions`, PostGIS en `public` y digest sobre UTF-8 explícito.
- Todas las columnas monetarias SQL en `bigint`; campos OpenAPI de centavos en `int64`.
- `order_acceptances` con tabla, RLS, grants y trigger append-only.
- Lane GPS con `UNIQUE(driver_position_id)` sin FK.
- Campos de aceptación/versionado restaurados en `external_offers`.
- Vectores SHA-256 de token y aceptación recalculados correctamente.
- Escenarios Gherkin v0.6 presentes.
- Delimitadores SQL `$$` balanceados.
- Hashes SHA-256 del MANIFEST verificados después de generarlo.
- Purge real elimina únicamente terminales antiguos, respeta lote y es idempotente.
- Dos conexiones Worker concurrentes purgan sin doble conteo, deadlock ni lock residual.
- Maintenance conserva `SELECT,DELETE` sobre ambos lanes y no recibe `UPDATE`.

## Verificación contra documentación oficial

- PostgreSQL 18 exige privilegio `SELECT` sobre las columnas usadas por `RETURNING`; por ello los inserts runtime del outbox no deben emitir `RETURNING`.
- `pgcrypto.digest` acepta `bytea` y devuelve `bytea`; el contrato usa `convert_to(...,'UTF8')` para evitar ambigüedad de encoding.
- PostgreSQL exige que toda restricción única de una tabla particionada incluya la clave de partición; SCL-006 conserva esta advertencia.
- EF Core permite deshabilitar generación de valores mediante `ValueGeneratedNever`.
- PostGIS permanece normalmente en `public` y su extensión no es relocatable de forma ordinaria.

## Ejecución real ARC-002

AI-06 y AI-18 se ejecutaron, en ese orden, sobre una base efímera y limpia de
Testcontainers con PostgreSQL 18/PostGIS 3.6. Las pruebas usan logins runtime
no-superuser, pooling real y credenciales sintéticas. Bootstrap, extensiones,
roles, RLS, provisioning, outbox, tracking, aceptación, snapshots y dinero
pasaron contra PostgreSQL real. La remediación queda pendiente de los cinco
jobs del PR antes de declarar ARC-002 `DONE`.

La validación no crea migraciones ni aplica los scripts a la base persistente de
FND-002. `SEC-002` y `DBA-001` permanecen tareas independientes.
