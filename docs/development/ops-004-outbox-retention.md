# OPS-004: retención y purga segura de outbox

OPS-004 convierte las funciones normativas de purga (AI-06) y el rol de
mantenimiento (AI-18, ADR-030) en un job acotado, observable y con dry-run
dentro del Worker. No agrega migraciones ni funciones nuevas: el job solo llama
`security.purge_outbox(...)` y `security.purge_location_outbox(...)` con el rol
`paqueteria_worker`. Nunca consulta, actualiza ni borra filas del outbox de
forma directa y nunca cambia el estado de un mensaje.

## Componentes

- `Paqueteria.Infrastructure/Database/Outbox/Retention`: opciones y validación,
  gateway PostgreSQL, servicio de ciclo, telemetría y `BackgroundService`.
- `Paqueteria.Worker`: una línea de registro, `AddOutboxRetention(...)`, y la
  sección `OutboxRetention` de `appsettings.json`.

## Configuración

| Clave | Default | Límite validado al arrancar |
| --- | --- | --- |
| `OutboxRetention:Enabled` | `false` | — |
| `OutboxRetention:DryRun` | `true` | — |
| `OutboxRetention:PollInterval` | `00:15:00` | 1 min – 1 día |
| `OutboxRetention:InitialDelay` | `00:01:00` | 0 – 1 h |
| `OutboxRetention:CommandTimeoutSeconds` | `30` | 1 – 300 |
| `Business:ProcessedRetention` | `7.00:00:00` | ≥ 1 día |
| `Business:DeadRetention` | `30.00:00:00` | ≥ 7 días |
| `Business:BatchSize` | `1000` | 1 – 10 000 |
| `Business:MaxBatchesPerRun` | `10` | 1 – 100 |
| `Location:ProcessedRetention` | `1.00:00:00` | ≥ 1 hora |
| `Location:DeadRetention` | `7.00:00:00` | ≥ 1 día |
| `Location:BatchSize` | `5000` | 1 – 50 000 |
| `Location:MaxBatchesPerRun` | `10` | 1 – 100 |

Las retenciones tienen un tope de 3650 días. El job usa
`ConnectionStrings:PaqueteriaWorker`. Si `Enabled=true` y esa cadena falta, el
Worker no arranca. La activación es explícita y tiene dos pasos: primero
`Enabled=true` con `DryRun=true`, y luego `DryRun=false`.

## Mínimos normativos

Los mínimos pertenecen a AI-06 y la base de datos los sigue imponiendo:

| Lane | `PROCESSED` | `DEAD` | Lote máximo por llamada |
| --- | --- | --- | --- |
| business (`purge_outbox`) | 1 día | 7 días | 10 000 |
| location (`purge_location_outbox`) | 1 hora | 1 día | 50 000 |

La validación de opciones rechaza de antemano valores dentro de la ventana
mínima o lotes que la función recortaría en silencio. Si aun así llega un
cutoff dentro de la ventana, PostgreSQL responde `22023`. La lane falla sin
mutar nada y la otra lane continúa. `PROCESSED` se mide por `processed_at` y
`DEAD` por `COALESCE(processed_at, created_at)`. Los cutoffs se calculan una
vez por lane y por corrida con el reloj del Worker, que debe estar sincronizado
por NTP. Los defaults dejan margen amplio sobre los mínimos.

## Comportamiento de un ciclo

Cada ciclo recorre primero business y después location. Cada lane usa sus
propios cutoffs, lote y techo:

1. calcula `processed_before` y `dead_before`;
2. en modo destructivo ejecuta lotes hasta `MaxBatchesPerRun` o hasta que un
   lote devuelva menos filas que `BatchSize`. Cada lote es una transacción
   corta, así que los locks se liberan entre lotes;
3. en dry-run hace exactamente una llamada con `p_dry_run=true` por lane;
4. emite métricas y un registro estructurado de evidencia.

El trabajo pendiente queda para el siguiente ciclo: una corrida nunca vacía la
tabla completa. La cancelación se atiende entre lotes. Un lote interrumpido
hace rollback completo. Un fallo se registra y se reintenta en el siguiente
`PollInterval`; nunca detiene el Worker.

Cada réplica del Worker ejecuta su propio ciclo. La purga concurrente es segura
porque cada fila elegible se borra una sola vez (contrato ARC-002 de dos
conexiones). El trabajo máximo por intervalo es réplicas × `BatchSize` ×
`MaxBatchesPerRun` por lane.

## Procedimiento dry-run

1. Configurar `OutboxRetention__Enabled=true` y `OutboxRetention__DryRun=true`.
2. Reiniciar o desplegar el Worker. El primer ciclo corre tras `InitialDelay`.
3. Revisar el evento `OutboxRetentionLaneCompleted` de cada lane:
   `affected_rows` es el conteo elegible, acotado por `BatchSize`, y
   `exhausted=true` indica que no hay más candidatos con esos cutoffs.
4. Repetir el dry-run no cambia nada. La misma llamada con `p_dry_run=false`
   borra exactamente esos candidatos.

El servicio también expone `DryRunAsync`, que siempre usa `p_dry_run=true`
aunque la configuración sea destructiva. El modo destructivo solo se elige por
configuración.

## Métricas y evidencia

Meter `Paquetenvia.Outbox.Retention`. Las dimensiones son `lane`
(`business|location`), `mode` (`dry_run|delete`), `outcome`
(`success|failure|cancelled`) y `error_class` (SQLSTATE o
`transient|npgsql|timeout|unexpected`). No se usan ids, mensajes ni tenants.

| Instrumento | Tipo | Dimensiones |
| --- | --- | --- |
| `outbox.retention.runs` | counter | lane, mode, outcome |
| `outbox.retention.batches` | counter | lane, mode |
| `outbox.retention.dry_run_eligible` | counter | lane |
| `outbox.retention.deleted_rows` | counter | lane |
| `outbox.retention.failures` | counter | lane, mode, error_class |
| `outbox.retention.run_duration` | histogram (ms) | lane, mode, outcome |
| `outbox.retention.last_success` | gauge (unix s) | lane, mode |

Cada lane y cada corrida produce un log JSON con `EventId` 4004
(`OutboxRetentionLaneCompleted`). Es `Information`, o `Error` si falla, y
contiene `Lane`, `DryRun`, `ProcessedBefore`, `DeadBefore`, `BatchSize`,
`MaxBatchesPerRun`, `Batches`, `AffectedRows`, `Exhausted`, `StartedAt`,
`CompletedAt`, `Outcome` y `ErrorClass`. Al arrancar, el Worker registra si el
job está deshabilitado o con qué modo e intervalo quedó programado.

`platform.audit_logs` es por tenant (`org_id NOT NULL`) y la purga es global,
así que no se inventa un tenant. La evidencia auditable de OPS-004 está en
estos logs estructurados inmutables del runtime, las métricas y este runbook.

## Deshabilitar temporalmente

Configurar `OutboxRetention__Enabled=false` y reiniciar el Worker. El job
registra que está deshabilitado y no llama a ninguna función. Para detener
solo los borrados y seguir observando, basta con `OutboxRetention__DryRun=true`.

## Ejecutar un ciclo acotado

Configurar `Enabled=true`, el `DryRun` deseado y `InitialDelay=00:00:00`, y
reiniciar una réplica del Worker. El primer ciclo corre de inmediato y respeta
`MaxBatchesPerRun`; el siguiente corre tras `PollInterval`. Después se vuelve
a la configuración previa.

Para un diagnóstico puntual sin Worker, se puede usar la misma función con la
credencial del Worker y un solo lote en dry-run:

```sql
BEGIN;
SET LOCAL ROLE paqueteria_worker;
SELECT security.purge_outbox(clock_timestamp() - interval '7 days',
                             clock_timestamp() - interval '30 days', 1000, true);
SELECT security.purge_location_outbox(clock_timestamp() - interval '1 day',
                                      clock_timestamp() - interval '7 days', 5000, true);
COMMIT;
```

## Rollback

Deshabilitar el job (`Enabled=false`). No hay migración que revertir. Las filas
terminales borradas no se pueden reconstruir desde el outbox, y eso es
intencional: `PROCESSED` ya se entregó y `DEAD` agotó sus reintentos. El
estado de negocio vive en las tablas de dominio y en sus eventos, no en el
outbox. Si hace falta investigar mensajes `DEAD`, hay que aumentar
`DeadRetention` antes de que venza la ventana.

## Por qué los estados activos están protegidos

`PENDING`, `RETRY` y `PROCESSING` representan entregas aún pendientes o en
curso. Un `PROCESSING` con lease vigente pertenece a un worker que todavía
puede liquidarlo, y uno con lease vencido espera `requeue_stale_*`. Borrar
cualquiera de ellos perdería una entrega. Las funciones de AI-06 filtran solo
`PROCESSED` y `DEAD` y vuelven a verificar el estado dentro del `DELETE`. El
rol `paqueteria_maintenance` no tiene `UPDATE` ni capacidad de
claim/settle/requeue, y el job no transforma estados ni introduce semántica
`DEAD` nueva.

## Pruebas

- Unitarias (`Paqueteria.UnitTests/Operations`): validación, cutoffs, techo de
  lotes, dry-run, cancelación, aislamiento de lanes, evidencia, dimensiones de
  métricas y ciclo del `BackgroundService`.
- Contratos PostgreSQL (`OutboxRetentionContractTests`, categoría
  `PostgreSqlContract`): propiedad y grants en catálogo, protección de estados
  activos, retención terminal, rechazo de mínimos, dry-run, techo, idempotencia,
  independencia de lanes y job alojado contra PostgreSQL real.
- Worker (`Ops004OutboxRetentionWorkerTests`): registro inerte por defecto y
  rechazo de configuración insegura al arrancar.
