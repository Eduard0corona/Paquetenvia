# Fuente única de verdad — paquete canónico v0.6

**Identificador de bundle:** `v0.6-full-canonical-sync-7-fin001-mvp1`
**Fecha de reconstrucción:** 2026-07-31
**Estado:** normativa consolidada y validada; REL-000-DEF-001 `RESOLVED` mediante la opción A aprobada por el project owner.

FIN-001 pertenece completo a MVP-1 con prioridad P0 y sus dependencias
preservadas. El inventario MVP-0/P0 contiene 29 elementos y no incluye una
variante reducida de FIN-001. Esta sincronización no modifica contratos
funcionales, OpenAPI, SQL, roles ni SignalR; no aprueba REL-000 o
MVP-0_INTERNAL y no autoriza EXT-001.

## Regla de autoridad

Este ZIP completo es la única entrega que debe validarse. No mezclar archivos sueltos, versiones en caché ni ZIP anteriores. Si existe una discrepancia, prevalece el archivo dentro de este bundle cuyo hash aparece en `MANIFEST.json`.

## Archivos críticos

- `database/AI-06_SCHEMA.sql` SHA-256: `f06c3ff24ca28621e1924a85d8d3368d5eef892cd8d4ce666e8e11815a2a98df`
- `database/AI-18_DATABASE_ROLE_MODEL.sql` SHA-256: `53c486c12178a470aa35764be2b117105f3ac077f384f7f3d0b7cca01a2b810f`

El SQL canónico contiene:

- `requeue_stale_outbox(interval, integer, integer)`;
- `requeue_stale_location_outbox(interval, integer, integer)`;
- `p_max_attempts` y promoción `RETRY`/`DEAD`;
- `lease_token` y `lease_expires_at`;
- pisos de seguridad para purga;
- backoff predeterminado en `settle_*`;
- `pgcrypto` en `extensions` y PostGIS en `public`;
- tracking público fail-closed y `order_acceptances` append-only.
- purga terminal sin privilegio `UPDATE`, con estado y cutoff revalidados en el `DELETE`.

AI-05 declara para DSP-002:

- respuestas 201/401/403/404/409;
- Problem Details 409 con códigos públicos cerrados;
- `route_id` ausente o `null` hasta RTE-001;
- vocabulario global de assignment conservado y únicamente `OWN` habilitado.
- capability-first para actores sin capacidad Dispatch;
- resolución autorizada estable `order_packages -> driver_profile_documents`,
  sin delays artificiales, antes de un único 404.

AI-06 y AI-18 no cambian en esta revisión. La migración de adopción de Dispatch
es la responsable de detectar drift contra el catálogo canónico existente.

## Registro posterior — 2026-09-26

Los hashes de "Archivos críticos" son los vigentes en `MANIFEST.json` y
`CHECKSUMS_SHA256.txt`. Las revisiones anteriores (AI-06 `c7681336…`, AI-18
`7b4d2638…`) cambiaron por los cursores de resincronización RTM-002
(2026-07-24, commit `4738861`: AI-05, AI-06 y AI-18) y por LIF-001/ADR-034
(AI-18, PR #83), y por OPS-003 (`OPS-003-CLEANUP-ROLE`: AI-18; AI-05
`x-offline-operation-age`), por los deltas del piloto (AI-06 y AI-18) y por el
almacén PostgreSQL de sesiones BFF (`BFF-SESSION-STORE-IMPLEMENTATION`: AI-06
`identity.bff_sessions`; AI-18 `paqueteria_session_executor`). AI-05 recibió además cambios aditivos de EXT-001, RTE-001,
CSV-001, INC-001, FIN-001 y SET-001; ver `CHANGELOG.md` y `decision-log.md`.
El identificador de bundle no se reemitió.


## Referencias de diseño registradas

ADR-032 y ADR-033, su contrato de guard compuesto y su plan de pruebas están registrados como referencias de diseño aceptadas para v0.7. No forman parte del comportamiento normativo ejecutable de v0.6 y no modifican AI-02/AI-04/AI-05/AI-06/AI-18 ni otros contratos funcionales.

## Validación local incluida

Ejecutar desde la raíz del paquete:

```bash
python3 tools/validate_contracts.py
sha256sum -c CHECKSUMS_SHA256.txt
```

`CHECKSUMS_SHA256.txt` cubre todos los archivos funcionales excepto el propio archivo de checksums y `MANIFEST.json`. `MANIFEST.json` registra el inventario consolidado completo.

DSP-002 distingue validación pura de forma y acceso productivo. Un request
válido exige capacidad tenant-aware antes de cualquier lock/lectura
idempotente, evidencia de replay u otros recursos de negocio.

## Evidencia de ejecución real

ARC-002 ejecutó AI-06 seguido de AI-18 en Testcontainers sobre PostgreSQL 18 y
PostGIS 3.6. Las dos familias de outbox completaron claim, settle, requeue,
purge real e invocaciones purge concurrentes sin doble conteo. Esta validación
no autoriza migraciones productivas ni la aplicación de AI-06/AI-18 a la base
persistente de desarrollo.
