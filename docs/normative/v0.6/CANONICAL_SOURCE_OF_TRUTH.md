# Fuente única de verdad — paquete canónico v0.6

**Identificador de bundle:** `v0.6-full-canonical-sync-2-adr032-033-registered`  
**Fecha de reconstrucción:** 2026-07-21  
**Estado:** normativa consolidada para revisión cruzada con Claude.

## Regla de autoridad

Este ZIP completo es la única entrega que debe validarse. No mezclar archivos sueltos, versiones en caché ni ZIP anteriores. Si existe una discrepancia, prevalece el archivo dentro de este bundle cuyo hash aparece en `MANIFEST.json`.

## Archivos críticos

- `database/AI-06_SCHEMA.sql` SHA-256: `4b5fe5397ff088b63e0c288770903512665c5fe8a8dc7401d7e4d3af64643505`
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


## Referencias de diseño registradas

ADR-032 y ADR-033, su contrato de guard compuesto y su plan de pruebas están registrados como referencias de diseño aceptadas para v0.7. No forman parte del comportamiento normativo ejecutable de v0.6 y no modifican AI-02/AI-04/AI-05/AI-06/AI-18 ni otros contratos funcionales.

## Validación local incluida

Ejecutar desde la raíz del paquete:

```bash
python3 tools/validate_contracts.py
sha256sum -c CHECKSUMS_SHA256.txt
```

`CHECKSUMS_SHA256.txt` cubre todos los archivos funcionales excepto el propio archivo de checksums y `MANIFEST.json`. `MANIFEST.json` registra el inventario consolidado completo.

## Limitación honesta

La validación incluida es estática. SQL, funciones RLS, privilegios y concurrencia deben ejecutarse contra PostgreSQL/PostGIS real mediante Testcontainers antes de generar migraciones productivas.
