# ADR-ORD-AUTO-CLOSE: cierre automático de órdenes entregadas

- Estado: aceptado (ORD-AUTO-CLOSE-2026-10-10)
- Fecha: 2026-10-10
- Alcance: transición ORD-002 `DELIVERED -> CLOSED`, Worker, rol de base de datos para el descubrimiento

## Contexto

Hasta ahora una orden entregada solo pasaba a `CLOSED` con "Cerrar orden" (`transitionOrder`), pedida por un
DISPATCHER o un PLATFORM_ADMIN con MFA de la organización dueña. El servidor solo lo permite si se cumplen las guardas
AI-04 de `CLOSED`: `no_unresolved_incident`, `if_cod_expected_then_cod_status_reconciled`,
`financial_reconciliation_complete` y `claim_window_ends_at_set`.

Pregunta al project owner, literal: "Cierre de órdenes: hoy una orden entregada se cierra a mano con "Cerrar orden".
El servidor solo lo permite si no tiene incidencias abiertas y el cobro contra entrega ya está conciliado. Aun cerrada,
se puede abrir una reclamación dentro del plazo. ¿Quieres que se cierre sola cuando cumpla esas reglas?". Respuesta
literal: "Sí, que se cierre sola" (descripción de la opción: "Agrego en la fase 3 un proceso en el servidor que la
cierra en cuanto cumple las reglas. Requiere ADR, porque cambia el comportamiento."). También dijo "avanza con la fase
3". Es un cambio MAJOR (AI-01 §7).

Restricciones que la solución debe respetar: el Worker es `NOBYPASSRLS` y `orders.orders` aplica `FORCE ROW LEVEL
SECURITY`, así que no puede ver órdenes sin un contexto tenant (ADR-025); todo acceso elevado exige función, consumer y
ADR específicos (ADR-025, ADR-034); las guardas no pueden tener una segunda implementación divergente; solo existen los
cinco flujos atómicos de AI-13 §4.

## Decisión

1. **Job del Worker.** `orders.auto-close`, detrás de `IJobScheduler` (el `BackgroundService` periódico de ADR-034),
   apagado por defecto (`Orders:AutoClose:Enabled`) y encendido en el piloto como LIF-001. Cada ciclo procesa lotes
   acotados (`BatchSize` 100, `MaxBatchesPerCycle` 10, intervalo `PollIntervalSeconds` 60).
2. **Descubrimiento (único paso cross-tenant).** Nuevo rol `paqueteria_auto_close_executor NOLOGIN BYPASSRLS`, dueño
   solo de `security.list_auto_close_owner_organizations(uuid, integer)`: `STABLE SECURITY DEFINER`,
   `search_path=pg_catalog, orders, pg_temp`, sin SQL dinámico, `EXECUTE` revocado a `PUBLIC` y concedido solo a
   `paqueteria_worker`. Con `USAGE` en `orders` y `SELECT (owner_org_id, status)` sobre `orders.orders` y nada más,
   devuelve solo identificadores de organizaciones dueñas que tienen al menos una orden `DELIVERED`, en orden
   ascendente, estrictamente después de un cursor y con un límite de 1 a 1000. No devuelve órdenes, estados, montos ni
   datos de guardas, no escribe y no decide. No se reutiliza `paqueteria_lifecycle_executor`: ADR-034 lo limita a una
   sola función y a no enumerar tenants ni órdenes.
3. **Candidatas bajo RLS.** Por cada organización dueña, una transacción tenant explícita del Worker (`BEGIN`, luego
   `set_config(..., true)` con solo esa organización en `app.current_org_ids` como `uuid[]`, luego `SET LOCAL ROLE
   paqueteria_worker`) lee sus órdenes `DELIVERED` (`owner_org_id` = esa organización) por id ascendente, con su
   versión.
4. **Cierre con la transición de siempre.** Por cada orden, otra transacción tenant de su dueña ejecuta
   `PostgreSqlOrderTransitionService` como actor de sistema (`IOrderSystemTransitionService.CloseDeliveredAsync`):
   mismo bloqueo `FOR UPDATE`, misma versión optimista, misma matriz AI-04, **las mismas guardas** y las mismas
   escrituras que el cierre manual (orden, `order_events` con `public_event_code` nulo, `orders.status-changed`,
   `orders.timeline-event-added`, auditoría `ORDER_STATUS_CHANGED` e idempotencia), en una sola transacción. Lo único
   distinto es el actor:
   - solo puede tomar `DELIVERED -> CLOSED` (`OrderSystemTransitionPolicy`); cualquier otra arista es 403 interno;
   - no es un usuario: no se lee membresía, y `actor_id` queda nulo en el evento y en la auditoría;
   - el motivo es "Cierre automático: entregada, sin incidencias abiertas y con el cobro conciliado (si había cobro
     contra entrega)";
   - `app.current_user_id` es un identificador aleatorio por transacción que no corresponde a ningún usuario,
     membresía ni repartidor, así que RLS no le concede nada;
   - la llave de idempotencia es aleatoria por intento: un cierre automático nunca reproduce una respuesta guardada.
5. **Solo la organización dueña.** El contexto tenant de cada cierre contiene únicamente a la dueña. Una organización
   operadora nunca cambia el estado (ORD-002 es solo del dueño); el outbox queda etiquetado al dueño exactamente como en
   el cierre manual, de modo que SignalR, el tablero y el tracking público (CLOSED se publica como DELIVERED) se
   comportan igual.

## Consecuencias

- Una guarda que no se cumple deja la orden en `DELIVERED`; no se escribe nada y el job la vuelve a intentar en otra
  pasada. Se cuenta por código de regla AI-05 (`UNRESOLVED_INCIDENT`, `COD_NOT_RECONCILED`,
  `FINANCIAL_RECONCILIATION_INCOMPLETE`, `CLAIM_WINDOW_NOT_SET`), sin log por orden.
- Concurrencia: no hay bloqueo de job. Dos Workers, o un cierre manual simultáneo, se serializan en el bloqueo de la
  fila; el segundo encuentra otra versión o estado y no escribe nada. Cada orden se cierra a lo sumo una vez.
- Un error inesperado en una orden se revierte, no detiene el resto del lote y deja el ciclo como fallido
  (`ScheduledJobCycle` con outcome `failure`, alerta OBS-002).
- El ciclo se detiene al final de las órdenes `DELIVERED` o en el tope de lotes; el siguiente continúa después de la
  última orden intentada, así que órdenes que todavía no cumplen las guardas no bloquean a las demás.
- `CLOSED -> CLAIM_OPEN` sigue permitido hasta `claim_window_ends_at` y LIF-001 finaliza después; el cierre manual no
  cambia.
- No es un sexto flujo de AI-13 §4: cada transacción es la transición ORD-002 de una orden. El flujo 4 (conciliación
  COD -> cierre) sigue sin implementarse; el job cubre por igual órdenes con y sin COD.
- Sin datos personales ni identificadores en logs ni métricas.

## Alternativas descartadas

- **Cerrar dentro de la conciliación COD (flujo 4 de AI-13).** No cubre órdenes sin COD ni incidencias resueltas más
  tarde.
- **Usar el outbox como temporizador.** ADR-034 lo descarta; además exigiría filas nuevas en tres módulos.
- **Evaluar las guardas en SQL dentro de la función.** Duplicaría las guardas de ORD-002.
- **Ampliar `paqueteria_lifecycle_executor`.** Contradice ADR-034 (una función, sin enumerar tenants ni órdenes).

## Despliegue y rollback

- Migración del lane Orders `20261010000100_AddOrderAutoCloseDiscovery`: crea el rol si falta, concede sus dos grants,
  instala la función y verifica el catálogo; su `Down` elimina solo la función (rol y grants quedan inertes).
- AI-18 declara el rol, sus grants y las aserciones 32-34 para instalaciones nuevas.
- Azure: en una base del piloto anterior a este cambio, un administrador crea `paqueteria_auto_close_executor NOLOGIN
  BYPASSRLS` con `SET` para el rol de despliegue antes de que corra el lane Orders (igual que con los ejecutores de
  ciclo de vida y de outbox del operador); sin eso el puente E-002 se detiene con
  `E002_EFFECTIVE_ROLE_CAPABILITY_MISSING roles=paqueteria_auto_close_executor` antes de escribir nada.
- Rollback operativo: `Orders:AutoClose:Enabled=false` y reiniciar el Worker; si la base misma debe dejar de exponer el
  descubrimiento, `REVOKE EXECUTE ON FUNCTION security.list_auto_close_owner_organizations(uuid,integer) FROM
  paqueteria_worker;`. Las órdenes ya cerradas siguen cerradas (eventos y auditoría son append-only).
