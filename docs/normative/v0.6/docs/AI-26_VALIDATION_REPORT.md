# AI-26 — Informe de validación v0.6

**Fecha:** 2026-07-20  
**Alcance:** validación estructural, contractual, criptográfica y de integridad del paquete.

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

## Verificación contra documentación oficial

- PostgreSQL 18 exige privilegio `SELECT` sobre las columnas usadas por `RETURNING`; por ello los inserts runtime del outbox no deben emitir `RETURNING`.
- `pgcrypto.digest` acepta `bytea` y devuelve `bytea`; el contrato usa `convert_to(...,'UTF8')` para evitar ambigüedad de encoding.
- PostgreSQL exige que toda restricción única de una tabla particionada incluya la clave de partición; SCL-006 conserva esta advertencia.
- EF Core permite deshabilitar generación de valores mediante `ValueGeneratedNever`.
- PostGIS permanece normalmente en `public` y su extensión no es relocatable de forma ordinaria.

## Limitación honesta

Este entorno no dispone de un servidor PostgreSQL/PostGIS ni Docker/Testcontainers, por lo que no se ejecutaron AI-06 y AI-18 contra una base real. `SEC-002`, `DBA-001` y `ARC-002` exigen esa prueba antes de considerar la primera migración implementada.
