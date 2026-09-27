# OPS-003: limpieza operativa y rechazo de operaciones offline de más de 72 h

## Alcance implementado

OPS-003 aplica tres decisiones del owner: `OPS-003-OFFLINE-72H` (una operación
offline se reintenta hasta 72 h y las llaves de idempotencia se purgan después),
`OPS-003-CLEANUP-ROLE` (rol dedicado, patrón ADR-034) y
`OPS-003-SERVER-72H-REJECTION` (el servidor también rechaza lo que tenga más de
72 h). No hay estados, eventos, SignalR ni UI nuevos.

## Ejecución privilegiada

La migración Custody `20260927000100_AddOperationalCleanupExecutor`:

- verifica la forma canónica de `platform.idempotency_keys` y
  `custody.proof_upload_sessions`, `FORCE RLS`, los índices de expiración y la
  ausencia de triggers de usuario;
- crea `paqueteria_cleanup_executor NOLOGIN BYPASSRLS` si falta y falla si ya
  existe con atributos, membresías u objetos fuera de contrato;
- concede sólo `USAGE` sobre `platform` y `custody`,
  `SELECT (owner_org_id, scope, idempotency_key, created_at, expires_at)` más
  `DELETE` (PostgreSQL no tiene `DELETE` por columna) sobre
  `platform.idempotency_keys`, y `SELECT (id, status, expires_at)` más
  `UPDATE (status, updated_at)` sobre `custody.proof_upload_sessions`;
- instala dos funciones `SECURITY DEFINER` con `search_path` fijo, `EXECUTE`
  revocado a `PUBLIC` y concedido sólo a `paqueteria_worker`, y verifica el
  catálogo resultante.

| Función | Lote | Regla |
| --- | --- | --- |
| `security.purge_expired_idempotency_keys(timestamptz, integer, boolean)` | 1–5000 | borra (o cuenta en dry-run) llaves con `expires_at < LEAST(corte, now)` y `created_at < now - 72 h`; ningún argumento mueve el piso |
| `security.expire_proof_upload_sessions(integer)` | 1–1000 | `CREATED/UPLOADED/VALIDATING/READY` con `expires_at <= now` pasan a `EXPIRED`, con `FOR UPDATE SKIP LOCKED` |

Ambas devuelven sólo un conteo `integer`: ningún identificador de tenant, llave o
sesión cruza la frontera. AI-06 no cambia (como en ADR-034, la función la instala
el lane del módulo); AI-18 recoge el rol, los grants y las aserciones 13–16.

Punto de extensión: la purga de sesiones BFF (`BFF-SESSION-STORE-POSTGRESQL`) se
añadirá al mismo rol con su propia migración cuando exista
`identity.bff_sessions`; esta migración no cambia una vez aplicada.

## Jobs del Worker

`AddCustodyOperationalCleanup` registra dos `IScheduledJob` sobre el
`IJobScheduler` compartido (precedente LIF-001/OPS-004), un health check `ready`
(`custody_operational_cleanup`) y el medidor `Paquetenvia.Operations.Cleanup`
(`operations.cleanup.cycles`, `.rows`, `.failures`, `.cycle_duration`; dimensiones
`job`, `mode`, `outcome`, `error_class`, sin PII).

```text
OperationalCleanup:CommandTimeoutSeconds                 30   (1–300)
OperationalCleanup:IdempotencyKeys:Enabled               false
OperationalCleanup:IdempotencyKeys:DryRun                true
OperationalCleanup:IdempotencyKeys:PollIntervalSeconds   900  (1–3600)
OperationalCleanup:IdempotencyKeys:BatchSize             1000 (1–5000)
OperationalCleanup:IdempotencyKeys:MaxBatchesPerCycle    10   (1–100)
OperationalCleanup:ProofUploadSessions:Enabled           false
OperationalCleanup:ProofUploadSessions:PollIntervalSeconds 60 (1–3600)
OperationalCleanup:ProofUploadSessions:BatchSize         500  (1–1000)
OperationalCleanup:ProofUploadSessions:MaxBatchesPerCycle 10  (1–100)
```

Habilitar cualquiera exige `ConnectionStrings:PaqueteriaWorker`. Un dry-run es una
sola llamada; un ciclo destructivo termina con un lote corto o al llegar al tope.
Los objetos en cuarentena siguen la política de retención del bucket (GATE-007);
el job no borra objetos.

## Rechazo en el servidor

AI-05 `x-offline-operation-age`: `transitionOrder` y `createProofUploadSession`
aceptan `client_occurred_at` opcional y `finalizeProof` usa `captured_at`. El
endpoint compara con el reloj del servidor antes de llamar al servicio:

| Marca del cliente | Resultado |
| --- | --- |
| hasta 72 h exactas en el pasado | continúa |
| más de 72 h en el pasado | 409 `OFFLINE_OPERATION_EXPIRED` |
| más adelante que ahora + tolerancia | 409 inválido (`INVALID_REQUEST` en POD, sin código en transiciones) |
| `client_occurred_at` ausente | operación online, continúa |
| `captured_at` ausente | 409 `INVALID_REQUEST`, como antes |

La tolerancia es `OfflineOperations:ClockToleranceSeconds` (0–300, por defecto
300); las 72 h son fijas. La marca no forma parte del hash idempotente, así que un
replay con más de 72 h se rechaza aunque su llave siga existiendo.

La PWA del conductor todavía no envía `client_occurred_at` en transiciones ni en
sesiones de carga (su prueba lo excluye a propósito); hasta que lo haga, el
rechazo aplica a `finalizeProof` y a cualquier cliente que declare la marca.

## Rollback

`Down` falla cerrado con `OPS003_SCHEMA_DOWNGRADE_NOT_SUPPORTED`: las llaves
purgadas y las sesiones expiradas son hechos del ciclo de vida. Rollback
operativo: `OperationalCleanup:*:Enabled=false` y, si la base debe dejar de
limpiar, `AddOperationalCleanupExecutor.OperationalRollbackSql` (revoca `EXECUTE`
al Worker) con la credencial de despliegue.
