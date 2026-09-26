# OPS-004: retención y purga segura de outbox

OPS-004 convierte las funciones normativas de purga (AI-06) y el rol de
mantenimiento (AI-18, ADR-030) en un job acotado, observable y con dry-run
dentro del Worker. No agrega migraciones ni funciones nuevas: el job solo llama
`security.purge_outbox(...)` y `security.purge_location_outbox(...)` con el rol
`paqueteria_worker`. Nunca consulta, actualiza ni borra filas del outbox de
forma directa y nunca cambia el estado de un mensaje.

## Componentes

- `Paqueteria.Infrastructure/Database/Outbox/Retention`: opciones y validación,
  gateway PostgreSQL, servicio de ciclo, telemetría, `OutboxRetentionJob`
  (`IScheduledJob`) y un `BackgroundService` que solo entrega el job al
  `IJobScheduler` compartido (ADR-034, `PeriodicJobScheduler`). OPS-004 no tiene
  un planificador propio.
- `Paqueteria.Worker`: una línea de registro, `AddOutboxRetention(...)`, y la
  sección `OutboxRetention` de `appsettings.json`.

## Configuración

| Clave | Default | Límite validado al arrancar |
| --- | --- | --- |
| `OutboxRetention:Enabled` | `false` | — |
| `OutboxRetention:DryRun` | `true` | — |
| `OutboxRetention:PollInterval` | `00:15:00` | 1 min – 1 h (máximo de `PeriodicJobScheduler`) |
| `OutboxRetention:CommandTimeoutSeconds` | `30` | 1 – 300 |
| `Business:ProcessedRetention` | `7.00:00:00` | ≥ 1 día + 5 min |
| `Business:DeadRetention` | `30.00:00:00` | ≥ 7 días + 5 min |
| `Business:BatchSize` | `1000` | 1 – 10 000 |
| `Business:MaxBatchesPerRun` | `10` | 1 – 100 |
| `Location:ProcessedRetention` | `1.00:00:00` | ≥ 1 hora + 5 min |
| `Location:DeadRetention` | `7.00:00:00` | ≥ 1 día + 5 min |
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
mínima o lotes que la función recortaría en silencio. Además exige un margen de
reloj (`OutboxRetentionOptionsValidator.ClockSkewMargin`, 5 minutos) sobre cada
mínimo: el cutoff sale del reloj del Worker y la función lo compara con
`clock_timestamp()` de PostgreSQL, así que una retención exactamente igual al
mínimo fallaría con `22023` en cuanto el Worker adelantara unos milisegundos.
El desfase entre relojes debe mantenerse por debajo de ese margen. Si aun así llega un
cutoff dentro de la ventana, PostgreSQL responde `22023`. La lane falla sin
mutar nada y la otra lane continúa. `PROCESSED` se mide por `processed_at` y
`DEAD` por `COALESCE(processed_at, created_at)`. Los cutoffs se calculan una
vez por lane y por corrida con el reloj del Worker, que debe estar sincronizado
por NTP. Los defaults dejan margen amplio sobre los mínimos más el margen de reloj.

## Comportamiento de un ciclo

Cada ciclo recorre primero business y después location. Cada lane usa sus
propios cutoffs, lote y techo:

1. calcula `processed_before` y `dead_before`;
2. sondea las filas `DEAD` que ya pasaron `dead_before` (ver «Visibilidad de
   `DEAD`»); si el sondeo falla, la lane falla sin ejecutar lotes;
3. en modo destructivo ejecuta lotes hasta `MaxBatchesPerRun` o hasta que un
   lote devuelva menos filas que `BatchSize`. Cada lote es una transacción
   corta, así que los locks se liberan entre lotes;
4. en dry-run hace exactamente una llamada con `p_dry_run=true` por lane;
5. emite métricas y un registro estructurado de evidencia.

El trabajo pendiente queda para el siguiente ciclo: una corrida nunca vacía la
tabla completa. La cancelación se atiende entre lotes. Un lote interrumpido
hace rollback completo. Un fallo se registra y se reintenta en el siguiente
`PollInterval`; nunca detiene el Worker.

El `IJobScheduler` compartido es dueño del tiempo, la repetición y la
cancelación: ejecuta el primer ciclo al arrancar el Worker y luego uno por
`PollInterval`. Cada invocación del job es exactamente un ciclo acotado sobre
ambos lanes. Un ciclo que falla fuera de los lanes queda registrado por el
scheduler (`Scheduled job outbox.retention ... CYCLE_FAILURE`) y se reintenta en
el siguiente intervalo.

Cada réplica del Worker ejecuta su propio ciclo. La purga concurrente es segura
porque cada fila elegible se borra una sola vez (contrato ARC-002 de dos
conexiones). El trabajo máximo por intervalo es réplicas × `BatchSize` ×
`MaxBatchesPerRun` por lane.

## Procedimiento dry-run

1. Configurar `OutboxRetention__Enabled=true` y `OutboxRetention__DryRun=true`.
2. Reiniciar o desplegar el Worker. El primer ciclo corre al arrancar.
3. Revisar el evento `OutboxRetentionLaneCompleted` de cada lane:
   `affected_rows` es el conteo elegible, acotado por `BatchSize`, y
   `exhausted=true` indica que no hay más candidatos con esos cutoffs.
4. Repetir el dry-run no cambia nada. Con los **mismos** cutoffs
   (`ProcessedBefore`/`DeadBefore` registrados) y el mismo lote, una llamada con
   `p_dry_run=false` borra esos candidatos, salvo que entre ambas llamadas
   cambien filas (nuevas filas terminales anteriores al cutoff, o filas ya
   borradas por otra réplica). El siguiente ciclo del job **no** reutiliza esos
   cutoffs: los recalcula con su propio reloj, así que puede borrar más filas
   que las contadas en el dry-run anterior (todas fuera de la retención
   configurada).

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
| `outbox.retention.dead_eligible` | gauge (filas) | lane |

Cada lane y cada corrida produce un log JSON con `EventId` 4004
(`OutboxRetentionLaneCompleted`). Es `Information`, o `Error` si falla, y
contiene `Lane`, `DryRun`, `ProcessedBefore`, `DeadBefore`, `BatchSize`,
`MaxBatchesPerRun`, `Batches`, `AffectedRows`, `DeadEligible`, `Exhausted`, `StartedAt`,
`CompletedAt`, `Outcome` y `ErrorClass`. Al arrancar, el Worker registra si el
job está deshabilitado o con qué modo e intervalo quedó programado.

### Visibilidad de `DEAD`

`AffectedRows` mezcla `PROCESSED` y `DEAD`, porque la función devuelve un solo
conteo. Para que la purga de mensajes muertos no pase inadvertida, cada lane
hace antes de su primer lote un sondeo de solo `DEAD`: la misma función
aprobada en dry-run con `p_processed_before='-infinity'` (ninguna fila
`PROCESSED` puede calificar), `dead_before` de la corrida y el lote máximo de
la lane (10 000 / 50 000). El resultado:

- queda en `DeadEligible` del evento 4004 y del reporte (`null` si la lane
  falló antes del sondeo);
- alimenta el gauge `outbox.retention.dead_eligible` (último valor por lane);
- en modo destructivo, si es mayor que cero, emite un `Warning` con `EventId`
  4005 (`OutboxRetentionDeadPurgePending`) con `Lane`, `DeadBefore`,
  `DeadEligible` y `DeadEligibleBound` antes de borrar.

Un valor igual al tope significa «al menos». El conteo no incluye los `DEAD`
más recientes que `dead_before`: la función rechaza cutoffs dentro de la
ventana mínima y el Worker no tiene `SELECT` sobre el outbox. Alertar si el
gauge es distinto de cero de forma sostenida y, si hace falta investigar,
aumentar `DeadRetention` antes de pasar a `DryRun=false`. El sondeo cuesta una
lectura adicional por lane y corrida (ver «Drenado inicial»).

`platform.audit_logs` es por tenant (`org_id NOT NULL`) y la purga es global,
así que no se inventa un tenant. La evidencia auditable de OPS-004 está en
estos logs estructurados inmutables del runtime, las métricas y este runbook.

## Deshabilitar temporalmente

Configurar `OutboxRetention__Enabled=false` y reiniciar el Worker. El job
registra que está deshabilitado y no llama a ninguna función. Para detener
solo los borrados y seguir observando, basta con `OutboxRetention__DryRun=true`.

## Ejecutar un ciclo acotado

Configurar `Enabled=true` y el `DryRun` deseado, y reiniciar una réplica del
Worker. El primer ciclo corre de inmediato y respeta
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

## Drenado inicial (tablas grandes)

AI-06 no define un índice que cubra el filtro de purga
(`status IN ('PROCESSED','DEAD')` por `processed_at`/`created_at`), así que cada
llamada —lote, dry-run o sondeo `DEAD`— recorre la tabla secuencialmente
mientras no encuentre suficientes candidatos. Con 1 M de filas se midió ≈2,2 s
por lote. Mientras el índice no exista (cambio normativo pendiente en AI-06),
la primera activación sobre un outbox con backlog acumulado se hace así:

1. Medir con dry-run (`Enabled=true`, `DryRun=true`) en una ventana de poca
   carga y anotar `affected_rows`, `dead_eligible` y la duración
   (`outbox.retention.run_duration`).
2. Subir `CommandTimeoutSeconds` si la duración de un lote se acerca al límite
   (máximo 300 s); un lote que excede el timeout hace rollback y la lane falla.
3. Activar `DryRun=false` en **una sola réplica** del Worker durante el
   drenado, con `MaxBatchesPerRun` moderado (p. ej. 10–20) y el `PollInterval`
   por defecto, y vigilar `outbox.retention.deleted_rows`, la latencia de
   `claim_outbox` y el crecimiento de autovacuum.
4. El drenado termina cuando `exhausted=true` en cada ciclo. Después se
   ejecuta `VACUUM (ANALYZE)` sobre ambas tablas del outbox (operación de DBA,
   no del Worker) y se vuelve a la configuración normal de réplicas.

En régimen, con el backlog drenado, cada ciclo borra poco y el costo por ciclo
es el de recorrer la tabla ya reducida.

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
  lotes, dry-run, margen de reloj, sondeo `DEAD` (evento 4005 y gauge),
  cancelación, aislamiento de lanes, evidencia, dimensiones de
  métricas, `OutboxRetentionJob` como `IScheduledJob` (un ciclo acotado por
  invocación) y delegación del host al `IJobScheduler` compartido.
- Integración (`Paqueteria.IntegrationTests/Operations`): composición del Worker,
  arranque fail-closed y coexistencia con el job de LIF-001 sobre un solo
  `IJobScheduler`.
- Contratos PostgreSQL (`OutboxRetentionContractTests`, categoría
  `PostgreSqlContract`): propiedad y grants en catálogo, protección de estados
  activos, retención terminal, rechazo de mínimos, dry-run, techo, idempotencia,
  independencia de lanes, sondeo `DEAD` sin mutación y retención mínima válida
  con el reloj del Worker adelantado, y job alojado contra PostgreSQL real.
- Worker (`Ops004OutboxRetentionWorkerTests`): registro inerte por defecto y
  rechazo de configuración insegura al arrancar.
