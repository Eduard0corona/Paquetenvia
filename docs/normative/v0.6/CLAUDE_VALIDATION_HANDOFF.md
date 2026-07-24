# Entrega para validación independiente

Valida únicamente este bundle: `v0.6-full-canonical-sync-4-dsp002-contract-remediation`.

## Comprobaciones mínimas

1. Ejecutar `python3 tools/validate_contracts.py`.
2. Ejecutar `sha256sum -c CHECKSUMS_SHA256.txt`.
3. Confirmar que `database/AI-06_SCHEMA.sql` tiene SHA-256 `c7681336856421487b208ea220d05017c4b8f820f1a34e1e7e838d5da09b7b96`.
4. Confirmar que las firmas de `requeue_stale_*` tienen tres parámetros tanto en AI-06 como en AI-18.
5. Confirmar promoción a `DEAD`, pisos de purga, backoff de settle y ausencia de privilegios directos SELECT/UPDATE/DELETE para runtime sobre ambos outbox.
6. No sustituir archivos por adjuntos sueltos con el mismo nombre.

7. Confirmar que ambos `purge_*` omiten `FOR UPDATE SKIP LOCKED` y vuelven a comprobar estado terminal y cutoff en el `DELETE` objetivo.
8. Confirmar que AI-18 conserva su hash y que maintenance mantiene exactamente `SELECT,DELETE`, sin `UPDATE`.
9. Confirmar que ADR-032 y ADR-033 conservan el estado `ACEPTADO como referencia de diseño para v0.7`; sólo se actualiza su referencia al hash canónico de AI-06.
10. Confirmar que `assignDriver` declara 201/401/403/404/409, usa el Problem
    Details DSP-002 para 409 y conserva 404 uniforme para recursos ausentes o
    cross-tenant.
11. Confirmar que `route_id` es nullable pero solo permite ausencia/null hasta
    RTE-001, y que únicamente `OWN` está habilitado en DSP-002.
12. Confirmar que AI-06 y AI-18 conservan sus hashes canónicos.

## Ejecución real completada por ARC-002

- AI-06 y AI-18 aplicados únicamente a PostgreSQL 18/PostGIS 3.6 efímero.
- Bootstrap, tracking, pgcrypto, RLS, roles y privilegios validados.
- Claim, settle, requeue y purge real validados para ambos lanes.
- Purge concurrente validado sin doble conteo, deadlock ni locks residuales.
- Productores runtime validados sin acceso directo ni `RETURNING`.
