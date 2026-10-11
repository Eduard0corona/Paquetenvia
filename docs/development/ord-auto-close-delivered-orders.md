# ORD-AUTO-CLOSE: cierre automático de órdenes entregadas

Decisión: `ORD-AUTO-CLOSE-2026-10-10` (project owner: "Sí, que se cierre sola"). ADR:
[`docs/adr/ADR-ORD-AUTO-CLOSE.md`](../adr/ADR-ORD-AUTO-CLOSE.md). Ítem de backlog: LIF-001 (AI-08).

## Qué hace

Un job del Worker cierra (`DELIVERED -> CLOSED`) cada orden entregada en cuanto cumple todas las guardas AI-04 de
`CLOSED`: sin incidencias abiertas, COD conciliado si había COD, integridad monetaria y ventana de reclamación fijada.
Usa la misma transición ORD-002 que "Cerrar orden", así que escribe el mismo evento, outbox y auditoría y la UI, SignalR
y el tracking público se comportan igual. El cierre manual no cambia; el que llegue primero gana y el otro recibe un
conflicto sin escribir nada.

## Flujo

1. `security.list_auto_close_owner_organizations(uuid, integer)` (como `paqueteria_worker`, sin contexto tenant)
   devuelve las organizaciones dueñas con alguna orden `DELIVERED`. Es el único paso cross-tenant; no devuelve
   órdenes.
2. Por organización, una transacción tenant del Worker lee sus órdenes `DELIVERED` (id y versión) bajo FORCE RLS.
3. Por orden, `OrderAutoCloseAttempter` resuelve `IOrderSystemTransitionService` en su propio scope y llama
   `PostgreSqlOrderTransitionService.CloseDeliveredAsync`, que ejecuta la transición en una transacción tenant de la
   dueña con el actor de sistema (solo `DELIVERED -> CLOSED`, `actor_id` nulo, motivo "Cierre automático: …").

| Resultado | Qué significa | Qué se escribe |
| --- | --- | --- |
| `Closed` | la transición se confirmó | orden, evento, 2 filas de outbox, auditoría, idempotencia |
| `NotEligible` | una guarda de `CLOSED` no se cumple (código AI-05) | nada; se reintenta en otra pasada |
| `Superseded` | la orden cambió desde que se leyó (versión, estado, visibilidad, concurrencia) | nada |
| fallo | error inesperado | nada (rollback); el ciclo termina como `failure` |

## Configuración

| Setting | Default | Rango |
| --- | --- | --- |
| `Orders:AutoClose:Enabled` | `false` | — |
| `Orders:AutoClose:PollIntervalSeconds` | `60` | 1–3600 |
| `Orders:AutoClose:BatchSize` | `100` | 1–1000 |
| `Orders:AutoClose:MaxBatchesPerCycle` | `10` | 1–100 |

Con `Enabled=true` el Worker exige `ConnectionStrings:PaqueteriaWorker`. El piloto lo enciende en
`deploy/azure/pilot/apps.bicep` con los valores por defecto. La transición usa además `Orders:CommandTimeoutSeconds`,
`Orders:IdempotencyLifetimeMinutes` y `Orders:TransitionMetadataMaximumBytes`, con los mismos rangos que la API.

`AddOrdersAutoClose` registra `OrderTransitionGuardRegistry` con la misma fábrica que la API
(`new OrderTransitionGuardRegistry()`, las guardas AI-04), y el registro ya no expone otro constructor público
([ORD-002-API-GUARD-REGISTRY](ord-002-api-guard-registry.md)): ningún contenedor puede componerlo sin guardas; sin
ellas el job cerraría cualquier orden entregada. Las pruebas unitarias, de integración del Worker y de PostgreSQL lo
fijan.

El ciclo termina al llegar al final de las órdenes `DELIVERED` (`drained`) o al tope de lotes (`capped`); en ese caso
el siguiente ciclo continúa después de la última orden intentada (pista en memoria; un reinicio empieza de nuevo).

## Observabilidad

- Meter `Paquetenvia.Orders.AutoClose`: `orders.auto_close.cycles` (`outcome`: `drained`, `capped`, `failed`) y
  `orders.auto_close.attempts` (`outcome`: `closed`, `superseded`, `failed` o el código de regla en minúsculas, por
  ejemplo `cod_not_reconciled`).
- Un log por ciclo con conteos cuando cerró algo, quedó en el tope o falló. Nunca identificadores ni datos personales.
- Health check `orders_auto_close` (tag `ready`): `Healthy` si está apagado; encendido, comprueba que el Worker puede
  ejecutar la función de descubrimiento.

## Base de datos

Migración del lane Orders `20261010000100_AddOrderAutoCloseDiscovery`:

- verifica `orders.orders` (columnas `owner_org_id`, `status`), FORCE RLS y el índice `orders_owner_status_idx`;
- crea `paqueteria_auto_close_executor NOLOGIN BYPASSRLS` si falta y falla si existe con atributos, membresías u
  objetos fuera de contrato;
- concede solo `USAGE` en `orders` y `SELECT (owner_org_id, status)` sobre `orders.orders`;
- instala la función `STABLE SECURITY DEFINER` con `search_path=pg_catalog, orders, pg_temp`, `EXECUTE` solo para
  `paqueteria_worker`, y verifica el catálogo resultante.

`Down` elimina solo la función. AI-18 declara el rol, sus grants y las aserciones 32-34; `DatabaseBaselineAssertions`
y el mapa E-002 (`_PLUS_ORDAUTOCLOSE`) exigen la misma frontera.

Azure: el puente E-002 del lane Orders comprueba `SET` y concede `CREATE` temporal en `security` a los dos ejecutores
del lane (ciclo de vida y cierre automático). En una base del piloto anterior a este cambio, un administrador crea antes
`paqueteria_auto_close_executor NOLOGIN BYPASSRLS` con `SET` para el rol de despliegue; sin eso el lane se detiene con
`E002_EFFECTIVE_ROLE_CAPABILITY_MISSING roles=paqueteria_auto_close_executor` sin escribir nada.

## Rollback

1. `Orders:AutoClose:Enabled=false` y reiniciar el Worker.
2. Si la base debe dejar de exponer el descubrimiento, con la credencial de despliegue:
   `REVOKE EXECUTE ON FUNCTION security.list_auto_close_owner_organizations(uuid,integer) FROM paqueteria_worker;`
3. Las órdenes ya cerradas siguen cerradas; sus eventos y auditoría son append-only.

## Pruebas

- `Paqueteria.UnitTests`: `OrderAutoCloseTests` (lotes, cursor, tope, resultados, fallos, opciones, registro, gating
  del job y política del actor de sistema).
- `Paqueteria.ContractTests`: `OrderAutoClosePostgreSqlContractTests` (16 pruebas sobre PostgreSQL real con la
  composición del Worker: mismas filas que un cierre manual sin actor; COD solo tras conciliar por el importe esperado;
  incidencia abierta, ventana sin fijar, COD sobrante e integridad monetaria rota dejan la orden en `DELIVERED` sin
  escribir nada; aislamiento de tenant y de operador; fallo inyectado en cada etapa sin escrituras; dos Workers en
  paralelo cierran cada orden una vez; CLAIM_OPEN y LIF-001 después del cierre; semántica y catálogo de la función;
  credenciales de runtime; health check; baseline; migración idempotente, down/up y actualización de una base poblada)
  y las pruebas de despliegue E-002 del lane Orders.
- `Paqueteria.IntegrationTests`: `OrderAutoCloseWorkerTests` sobre la composición real del Worker (apagado por defecto,
  validación de opciones, health check, transición solo como actor de sistema con todas las guardas AI-04).
