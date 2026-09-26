# ADR-034 — Ejecutor dedicado para la finalización del ciclo de vida de órdenes

**Estado:** Aprobado

## Contexto

ADR-024 aprueba un job idempotente que fija `finalized_at` cuando vence `claim_window_ends_at` de una orden `CLOSED`. El Worker es `NOBYPASSRLS` (ADR-025) y `orders.orders` aplica `FORCE ROW LEVEL SECURITY`, así que el Worker no puede descubrir órdenes vencidas entre tenants. ADR-025 exige función, consumer y ADR específicos para todo mantenimiento elevado.

## Decisión

Se crea `paqueteria_lifecycle_executor NOLOGIN BYPASSRLS`, dedicado exclusivamente a la finalización acotada del ciclo de vida de órdenes. Es propietario de una sola función: `security.finalize_expired_orders(p_batch_size integer) RETURNS integer`.

No se reutilizan los roles existentes: `paqueteria_bootstrap` sólo resuelve identidad y tracking público previos al tenant (ADR-017); `paqueteria_outbox_executor` sólo posee el lifecycle claim/settle/requeue del outbox (ADR-027); `paqueteria_maintenance` sólo purga filas terminales del outbox (ADR-030). Ampliar cualquiera mezclaría capacidades de seguridad distintas.

### Privilegios exactos

- `USAGE` sobre el esquema `orders` y ningún otro esquema de aplicación.
- `SELECT (id, status, claim_window_ends_at, finalized_at)` y `UPDATE (finalized_at)` sobre `orders.orders`.
- Sin `INSERT`, `DELETE`, grants a nivel tabla, `order_events`, `proofs`, incidents, finance ni outbox.
- No hereda roles, no se concede a `paqueteria_app` ni a `paqueteria_worker` y no posee tablas, esquemas ni tipos.
- La función es `SECURITY DEFINER` con `search_path=pg_catalog, orders, pg_temp`, sin SQL dinámico, `EXECUTE` revocado a `PUBLIC` y concedido sólo a `paqueteria_worker`.

AI-18 crea el rol y sus grants en instalaciones nuevas. La migración Orders de LIF-001 lo crea de forma idempotente en instalaciones pobladas, instala la función y rechaza cualquier rol o forma de tabla previa que difiera del contrato.

### Finalización atómica y acotada

La función no recibe tenant, orden, timestamp, estado ni fragmentos SQL. En una sola sentencia captura `v_now := clock_timestamp()`, selecciona candidatas `status='CLOSED'`, `finalized_at IS NULL`, `claim_window_ends_at IS NOT NULL` y `claim_window_ends_at < v_now`, ordenadas por `claim_window_ends_at, id`, limitadas por un lote validado entre 1 y 1000 y bloqueadas con `FOR UPDATE SKIP LOCKED`. Después fija `finalized_at = v_now` revalidando la elegibilidad en el `UPDATE` y devuelve el número de filas afectadas.

El límite estricto `<` complementa la guarda `now_before_or_equal_claim_window_ends_at` de `CLAIM_OPEN`, así que ningún instante admite una reclamación sobre una orden ya finalizada. No cambia `status`, `version`, `updated_at`, `archived_at`, montos, `order_events` ni `proofs`, y no emite eventos públicos ni SignalR. El archivado sigue bloqueado por GATE-007.

La aplicación nunca enumera tenants ni órdenes. El outbox no se usa como temporizador.

### Concurrencia e idempotencia

La mutación en base de datos es el mecanismo de idempotencia: una vez fijado `finalized_at`, la fila deja de ser elegible. `SKIP LOCKED` reparte las candidatas entre Workers concurrentes y la revalidación del `UPDATE` impide un segundo efecto. La transición `CLOSED → CLAIM_OPEN` bloquea la fila con `FOR UPDATE`, por lo que reclamación y finalización se serializan. No se requieren columnas de lease ni estados nuevos. Repetir la llamada, el job, dos Workers o un reinicio producen una sola finalización semántica.

### Planificación en el Worker

Esta ADR aprueba la implementación concreta MVP del puerto `IJobScheduler` de AI-03: un `BackgroundService` hospedado en `Paqueteria.Worker` que ejecuta ciclos periódicos, cancelables y con intervalo acotado, detrás de `IJobScheduler`. No se adopta Quartz, Hangfire ni otro scheduler externo.

Orders es dueño del job. Cada ciclo invoca únicamente la función privilegiada, con `Orders:ClaimWindowFinalization:BatchSize` y como máximo `MaxBatchesPerCycle` lotes; un lote con cero filas termina el ciclo. El job está desactivado por defecto (`Enabled=false`) y requiere `ConnectionStrings:PaqueteriaWorker`. La duración de la ventana sigue siendo `Orders:ClaimWindowHours`.

## Rollback

El esquema no se revierte: cada `finalized_at` escrito es un hecho legítimo del ciclo de vida (ADR-024), y la migración falla cerrada con `LIF001_SCHEMA_DOWNGRADE_NOT_SUPPORTED`. El rollback operativo desactiva `Orders:ClaimWindowFinalization:Enabled` y, si la base misma debe dejar de finalizar, revoca `EXECUTE` de la función a `paqueteria_worker` con la credencial de despliegue.
