# Plantilla de ejecución de tarea para un agente

## Entrada

```text
Task ID:
Release:
Dependencies verified:
Normative files read:
```

## Ejecución obligatoria

1. Releer la tarea y dependencias en `AI-08_BACKLOG.yaml`.
2. Identificar invariantes en `AI-04` y `AI-24`.
3. Actualizar primero pruebas/contratos afectados.
4. Implementar el cambio mínimo dentro del monolito modular.
5. Ejecutar pruebas unitarias, integración, arquitectura y seguridad indicadas.
6. Comprobar migración up/down y privilegios con PostgreSQL real.
7. Registrar decisión o desviación.
8. Entregar resumen y rollback.

## Verificaciones v0.6

- Ninguna consulta tenant fuera de transacción.
- `current_org_ids` parametrizado como `uuid[]`; `{}` vacío.
- Ninguna credencial runtime con `BYPASSRLS`.
- Ningún `SELECT/UPDATE/DELETE` directo sobre outbox.
- Inserción de outbox sin `RETURNING`; valores generados por aplicación.
- Lease requerido para settle y recuperación de stale processing.
- Tracking token C#/SQL produce hash idéntico.
- Mapa público cubre exactamente 17 estados.
- Timeline público solo usa `public_event_code`.
- Evidencia legal coincide con vector canónico.
- Montos `long/bigint/int64`.
- No PII en snapshots, logs, outbox o eventos públicos.
- No sexto flujo transaccional cross-module.

## Salida

```text
Task:
Status:
Changed files:
Database migration:
Commands run:
Tests and evidence:
Acceptance criteria:
Security assertions:
Contract drift checks:
Residual risks:
Rollback:
```
