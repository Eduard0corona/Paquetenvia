# LIF-001: ventana de reclamación y finalización

## Alcance implementado

LIF-001 completa el ciclo de vida de reclamaciones que ORD-002 ya aplicaba: una
orden `CLOSED` cuya ventana de reclamación venció recibe `finalized_at` mediante un
job idempotente del Worker. Se rige por ADR-024 y ADR-034.

No hay estados, endpoints, eventos públicos, SignalR, UI ni archivado nuevos. El
estado sigue siendo `CLOSED`, y `archived_at` permanece bajo GATE-007.

## Frontera temporal

| Momento | `CLAIM_OPEN` (ORD-002) | Finalizador (LIF-001) |
| --- | --- | --- |
| `now < claim_window_ends_at` | admisible | no selecciona |
| `now = claim_window_ends_at` | admisible (`<=`) | no selecciona (`<`) |
| `now > claim_window_ends_at` | rechazada | fija `finalized_at` |
| `finalized_at` no nulo | rechazada | no selecciona |

`CLAIM_RESOLVED` es final de inmediato: la transición ya fija `finalized_at` y el
estado es terminal. La duración de la ventana sigue siendo
`Orders:ClaimWindowHours`, validada entre 1 y 720.

## Ejecución privilegiada

La migración Orders `20260925020000_AddOrderLifecycleFinalizationExecutor`:

- verifica la forma canónica de `orders.orders`, `FORCE RLS`, el índice
  `orders_claim_window_idx` y la ausencia de triggers de usuario;
- crea `paqueteria_lifecycle_executor NOLOGIN BYPASSRLS` si falta, y falla si ya
  existe con atributos, membresías u objetos fuera de contrato;
- concede sólo `USAGE` sobre `orders`, `SELECT (id, status, claim_window_ends_at,
  finalized_at)` y `UPDATE (finalized_at)` sobre `orders.orders`;
- instala `security.finalize_expired_orders(p_batch_size integer)` como
  `SECURITY DEFINER` con `search_path=pg_catalog, orders, pg_temp`, `EXECUTE`
  revocado a `PUBLIC` y concedido sólo a `paqueteria_worker`;
- verifica el catálogo resultante y falla si queda algo más amplio.

La función valida el lote (1–1000). En una sola sentencia selecciona candidatas
por `claim_window_ends_at, id` con `FOR UPDATE SKIP LOCKED`, revalida la
elegibilidad en el `UPDATE`, fija `finalized_at` con el mismo instante
`clock_timestamp()` que usó para decidir y devuelve el número de filas afectadas.

No modifica `status`, `version`, `updated_at`, `archived_at`, montos,
`order_events`, `proofs`, outbox ni auditoría.

`DatabaseBaselineAssertions` exige la misma frontera en cuanto el rol o la función
existen. Una instalación anterior a LIF-001, sin ninguno de los dos, sigue siendo
válida hasta aplicar el lane Orders.

## Worker

`AddOrdersClaimWindowFinalization` registra el job detrás de `IJobScheduler`.
`PeriodicJobScheduler` es la implementación MVP aprobada por ADR-034. `Program.cs`
sólo agrega esa llamada.

| Setting | Default | Rango |
| --- | --- | --- |
| `Orders:ClaimWindowFinalization:Enabled` | `false` | — |
| `Orders:ClaimWindowFinalization:PollIntervalSeconds` | `60` | 1–3600 |
| `Orders:ClaimWindowFinalization:BatchSize` | `100` | 1–1000 |
| `Orders:ClaimWindowFinalization:MaxBatchesPerCycle` | `10` | 1–100 |

Con `Enabled=true` el Worker exige `ConnectionStrings:PaqueteriaWorker` y no
arranca sin ella.

Cada ciclo ejecuta lotes hasta que uno finaliza cero filas o se alcanza
`MaxBatchesPerCycle`. Un ciclo fallido se registra con el outcome `CYCLE_FAILURE`
y el siguiente se ejecuta tras el intervalo; la mutación en base de datos es el
mecanismo de idempotencia, por lo que reinicios y Workers concurrentes no duplican
efectos.

Telemetría: meter `Paquetenvia.Orders.ClaimWindowFinalization`, con los contadores
`orders.claim_window_finalization.cycles` (`outcome`: `drained`, `capped` o
`failed`) y `orders.claim_window_finalization.finalized`.

El health check `orders_claim_window_finalization` (tag `ready`) informa
`Healthy` cuando el job está desactivado y, cuando está activo, comprueba que el
Worker puede ejecutar la función.

## Rollback

El esquema no se revierte: `Down` falla con `LIF001_SCHEMA_DOWNGRADE_NOT_SUPPORTED`
y cada `finalized_at` escrito se conserva. Para detener la finalización:

1. `Orders:ClaimWindowFinalization:Enabled=false` y reiniciar el Worker.
2. Si la base misma debe dejar de finalizar, con la credencial de despliegue:
   `REVOKE EXECUTE ON FUNCTION security.finalize_expired_orders(integer) FROM paqueteria_worker;`

## Azure

El bridge E-002/AZR-001 no se amplió para el nuevo rol. En un clúster donde el rol
no existe, el `CREATE ROLE` de AI-18 o de la migración lo crea y el deployer
obtiene `ADMIN` automáticamente. La transferencia de propiedad de la función como
no superusuario todavía necesita `SET` sobre el rol y `CREATE` temporal en
`security`, igual que el bridge de NTF-001. Ese trabajo queda pendiente antes de
cualquier despliegue Azure de LIF-001.

## Validación

- `Paqueteria.UnitTests`: frontera de ventana, ciclo acotado, opciones, registro,
  gating del hosted service y scheduler.
- `Paqueteria.ContractTests`: `ClaimWindowFinalizationPostgreSqlContractTests`
  (elegibilidad, invariantes, lotes, orden, concurrencia, locks, catálogo,
  credenciales, assertions, migración y upgrade poblado) y
  `OrdersTransitionPostgreSqlContractTests.Claim_window_boundary_and_lifecycle_finalization_gate_claim_open`.
- `Paqueteria.IntegrationTests`: `ClaimWindowFinalizationWorkerTests` sobre la
  composición real del Worker.
