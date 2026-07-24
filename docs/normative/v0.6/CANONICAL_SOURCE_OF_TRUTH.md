# Fuente única de verdad — paquete canónico v0.6

**Identificador de bundle:** `v0.6-full-canonical-sync-6-dsp002-capability-before-state`
**Fecha de reconstrucción:** 2026-07-24
**Estado:** normativa consolidada y validada; ARC-002, contrato DSP-002, visibilidad no enumerable y capability-before-persisted-state `DONE`.

## Regla de autoridad

Este ZIP completo es la única entrega que debe validarse. No mezclar archivos sueltos, versiones en caché ni ZIP anteriores. Si existe una discrepancia, prevalece el archivo dentro de este bundle cuyo hash aparece en `MANIFEST.json`.

## Archivos críticos

- `database/AI-06_SCHEMA.sql` SHA-256: `c7681336856421487b208ea220d05017c4b8f820f1a34e1e7e838d5da09b7b96`
- `database/AI-18_DATABASE_ROLE_MODEL.sql` SHA-256: `7b4d263843e3ba49812fedb1167bd8ab92b2e33efa2558abf0833af1c13760dd`

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
