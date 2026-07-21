# Entrega para validación independiente

Valida únicamente este bundle: `v0.6-full-canonical-sync-2-adr032-033-registered`.

## Comprobaciones mínimas

1. Ejecutar `python3 tools/validate_contracts.py`.
2. Ejecutar `sha256sum -c CHECKSUMS_SHA256.txt`.
3. Confirmar que `database/AI-06_SCHEMA.sql` tiene SHA-256 `4b5fe5397ff088b63e0c288770903512665c5fe8a8dc7401d7e4d3af64643505`.
4. Confirmar que las firmas de `requeue_stale_*` tienen tres parámetros tanto en AI-06 como en AI-18.
5. Confirmar promoción a `DEAD`, pisos de purga, backoff de settle y ausencia de privilegios directos SELECT/UPDATE/DELETE para runtime sobre ambos outbox.
6. No sustituir archivos por adjuntos sueltos con el mismo nombre.

7. Confirmar que ADR-032 y ADR-033 conservan el estado `ACEPTADO como referencia de diseño para v0.7` y que los cuatro documentos registrados coinciden byte por byte con el paquete de decisión congelado.
8. Confirmar que esta revisión no modifica AI-02, AI-04, AI-05, AI-06, AI-18 ni otros contratos normativos v0.6.

## Áreas pendientes de ejecución real

- Aplicar AI-06 y AI-18 sobre PostgreSQL/PostGIS limpio.
- Ejecutar bootstrap y tracking con pgcrypto real.
- Ejecutar claim/settle/requeue concurrente con dos conexiones.
- Verificar catálogo de roles y privilegios.
- Verificar EF Core sin `RETURNING` para productores de outbox.
