# RTM-002: publicación confiable de outbox a SignalR

RTM-002 enlaza eventos confirmados de negocio y ubicación con el puerto
`IRealtimePublisher` de RTM-001. PostgreSQL y REST siguen siendo autoritativos;
SignalR es una distribución *at-least-once* que puede repetirse, retrasarse o
perderse durante una interrupción. Los clientes deben deduplicar y
resincronizarse desde REST.

## Arquitectura y colocación

`Realtime.Application` contiene contratos de mensajes reclamados, parsers
estrictos, políticas de retry y construcción de envelopes. No depende de
Npgsql ni de SignalR. `Realtime.Infrastructure` implementa el lifecycle
PostgreSQL, relee evidencia persistida, publica mediante `IRealtimePublisher`,
expone métricas/health y hospeda dos `BackgroundService` independientes.
`Paqueteria.Api` sólo compone esos servicios.

Los dispatchers están temporalmente colocados en la única instancia de API
porque el backplane aprobado sigue siendo `InProcess`. Publicar desde el Worker
standalone enviaría a otro proceso sin acceso a las conexiones del hub. Esta
colocación se registra como `RTM-002-INPROCESS-DISPATCHER-COLOCATION`; no
resuelve GATE-013. Los componentes no dependen de tipos de API y pueden moverse
al Worker cuando exista un backplane o relay aprobado.

No se agregó broker, relay HTTP, Redis, Azure SignalR ni estado autoritativo en
memoria.

## Lanes y lifecycle

Hay dos loops, opciones y límites de concurrencia separados:

- **business**: `platform.outbox_events`, batch 10, concurrencia 4, poll 250 ms,
  lease 120 s y máximo 10 intentos;
- **location**: `platform.location_outbox_events`, batch 25, concurrencia 8,
  poll 500 ms, lease 120 s y máximo 5 intentos.

El tamaño enviado a cada función de claim es
`min(BatchSize, MaximumConcurrency)`. Por tanto, un ciclo nunca toma más filas
que slots que puede iniciar inmediatamente. La configuración reserva cinco
segundos internos para settle y exige, independientemente en cada lane,
`LeaseSeconds >= PublishTimeoutSeconds + 5`; una combinación insegura falla
durante startup.

Ambos usan únicamente las funciones canónicas:

```text
claim_* -> settle_* (PROCESSED | RETRY | DEAD)
        \-> requeue_stale_*
```

La conexión usa una credencial login sintética separada y
`SET LOCAL ROLE paqueteria_worker`. No hay `INSERT`, `UPDATE` o `DELETE`
directo sobre outbox desde los consumidores. Cada claim se confirma antes de
publicar. El settle exige el lease token; perderlo se registra y nunca se
simula éxito: un `false` incrementa `lease_lost`, no registra un settlement
exitoso y deja el lifecycle canónico gobernar la fila. En shutdown no se
reclaman batches nuevos ni se inicia otro elemento ya reclamado. Los elementos
iniciados reciben una ventana de drenado acotada a publish timeout más el
margen de settle; después se cancelan y el lease queda disponible para
expiración y requeue canónico. Si la publicación concluye dentro de esa ventana,
el settle todavía usa un token vigente.

Fallos de schema, payload, topic o evidencia son permanentes y terminan
`DEAD`. Fallos transitorios de PostgreSQL/SignalR y timeouts terminan `RETRY`
con backoff exponencial, máximo acotado y jitter de ±20 %. El requeue de leases
stale se ejecuta independientemente por lane. Un poison message de un lane no
bloquea al otro.

`Realtime:OutboxDispatcher:Provider=Disabled` es el kill switch. En ese modo no
se abre PostgreSQL, no se reclama una fila y health informa degradación
explícita.

## Topics, payloads e IDs

Allowlist cerrada:

| Lane | Topic | Schema |
| --- | --- | --- |
| business | `orders.status-changed` | `order-status-changed-v1` |
| business | `orders.timeline-event-added` | `order-timeline-event-added-v1` |
| business | `dispatch.assignment-changed` | `assignment-changed-v1` |
| location | `drivers.location-updated` | `driver-location-updated-v1` |

Los parsers exigen propiedades exactas, UUID canónico no vacío, timestamps UTC,
aggregate y tenant congruentes, estados canónicos y coordenadas finitas dentro
de rango. Topic/schema desconocido no se interpreta de forma tolerante.

Cada significado semántico obtiene una fila y UUID distintos. El `event_id`
visible en el envelope es exactamente el ID estable de esa fila de outbox; un
retry o redelivery conserva el mismo ID. Se registra como
`RTM-002-STABLE-OUTBOX-ID-AS-EVENT-ID`. `correlation_id` permanece ausente
porque no existe evidencia canónica suficiente para fabricarlo.

ORD-002 persiste dos filas en la misma transacción que el cambio:
status y timeline. DSP-002 persiste tres: status, timeline y assignment. Un
rollback o replay idempotente no deja efectos parciales ni crea IDs nuevos.

## Evidencia y audiencias

Antes de publicar, el consumidor vuelve a leer evidencia mínima persistida con
contexto tenant transaccional:

- status/timeline: `orders.order_events` y public ID de la orden;
- assignment: assignment, evento/version correspondiente y elegibilidad
  vigente del driver;
- location: `drivers.driver_positions` con `publish_realtime=true`.

Las audiencias nunca provienen de strings de grupo dentro del payload:

- Operations recibe status, timeline, assignment y coordenadas sólo en
  `org:{owner_org_id}`;
- Driver recibe status/assignment sólo si assignment, perfil OWN, usuario y
  membresía DRIVER siguen activos al publicar;
- Tracking recibe sólo `PublicOrderStatusChanged.v1` cuando existe
  `public_event_code` congruente con el mapping canónico.

Una revocación antes del consumo elimina la audiencia Driver. Tracking nunca
recibe coordenadas, estado interno, driver ID, token o PII. Location se publica
exclusivamente a Operations.

La autorización de assignment produce una sola fila lógica. La membresía no se
resuelve con un join abierto ni con la primera fila: usa un `EXISTS` exacto para
el mismo usuario y organización con `role='DRIVER'` y `status='ACTIVE'`.
También exige assignment `OWN` en estado `ACCEPTED`/`ACTIVE`, perfil `OWN`
activo en la misma organización y usuario activo. Una membresía `VIEWER`
adicional, en cualquier orden de inserción, no cambia el resultado. El evento y
su versión persistidos anclan el contenido histórico de `AssignmentChanged`;
el estado actual de assignment sólo decide si todavía se permite la audiencia
Driver. Así, una cancelación posterior omite Driver sin convertir la entrega a
Operations en poison.

## At-least-once, deduplicación y resincronización

El settle ocurre después del envío. Una caída entre envío y settle puede
redeliver el mismo envelope. El cliente usa `event_id` para deduplicación
visible y `aggregate_version` para ignorar eventos viejos. Esto no convierte
SignalR en fuente de verdad.

Los cursores REST son:

- Operations: versiones de aggregate ya presentes en sus snapshots;
- DriverStop: `order_id` + `aggregate_version`;
- PublicTrackingProjection: `aggregate_version`;
- location: milisegundos UTC enteros derivados de `captured_at`.

El cursor de ubicación se registra como `RTM-002-LOCATION-CURSOR-UTC-MS`. Es
determinista, cabe en el entero seguro de JavaScript y no usa hash, UUID,
attempt, contador en memoria ni orden de claim. El frontend construye mapas de
versión explícitos por vista y reemplaza estado local con el snapshot REST al
reconectar.

La migración RTM-002 sólo reemplaza la función pública de tracking y verifica
owner/grants de forma fail-closed. No crea tablas, columnas, índices, FK,
triggers ni secuencias. AI-18 amplía únicamente el SELECT column-level de
`orders.orders.version` para bootstrap.

## Observabilidad y health

El meter `Paquetenvia.Realtime.Outbox` registra batches, mensajes reclamados,
publicados, settle, requeue stale, leases perdidos, fallos de mapping, inflight,
edad, duración de batch y duración de publicación. Sus tags son de baja
cardinalidad: lane, event type, audience, outcome y error class. Logs y métricas
no incluyen payload, tokens, coordenadas, IDs, grupos ni PII.

Readiness abre una transacción corta, aplica
`SET LOCAL ROLE paqueteria_worker`, ejecuta sólo introspección y hace rollback.
Comprueba `current_user`, `NOBYPASSRLS`, existencia y privilegio `EXECUTE` de
las seis firmas canónicas: claim, settle y requeue stale para business y
location. No reclama filas, no crea leases y no ejecuta settle ni requeue.
Falta de cualquier firma/permiso o `BYPASSRLS` produce `unhealthy`; provider
Disabled permanece `degraded`.

## Pruebas

La cobertura incluye:

- parsers exactos, schema/topic poison, UTC-ms, retries y opciones;
- productores ORD-002/DSP-002, conteo de filas, IDs distintos y rollback;
- lifecycle, ownership, grants, NOBYPASSRLS, pooling y migraciones en
  PostgreSQL 18/PostGIS 3.6;
- Kestrel, PostgreSQL, ambos consumers y clientes SignalR reales con
  aislamiento cross-tenant y settle `PROCESSED`;
- contratos AI-05/06/12/18/24, arquitectura y ausencia de DML directo;
- deduplicación, versiones y resincronización web;
- reconexión real existente sin degradarla.

La aceptación final añade evidencia real, no llamadas directas al publisher:

- un failure injector de prueba interrumpe business después de publicar y antes
  de settle; el lease vence, `requeue_stale_outbox` recupera la misma fila, las
  dos entregas conservan el mismo `event_id`, el navegador aplica una sola vez
  y la fila termina `PROCESSED`;
- DriverHub recibe status y assignment para un usuario `DRIVER + VIEWER`;
  otro driver y un viewer sin DRIVER no reciben. Suspender la membresía o
  cancelar la assignment después de producir la fila omite Driver, registra
  `driver_audience_skipped`, mantiene Operations y termina `PROCESSED`;
- una posición confirmada con `publish_realtime=true` atraviesa el consumer de
  location y llega sólo a Operations del tenant, con payload exacto,
  `aggregate_id=driver_id` y cursor UTC-ms; otro tenant, DriverHub y TrackingHub
  no reciben, y logs/métricas no contienen coordenadas;
- poison business termina `DEAD` mientras location válido termina
  `PROCESSED`, y el escenario inverso obtiene el mismo aislamiento con métricas
  separadas por lane;
- el navegador carga inicialmente `/api/v1/orders`, observa un reinicio físico
  `Reconnecting -> Reconnected`, reemplaza por completo su mapa
  `order_id -> aggregate_version` con otro snapshot REST real de Operations y
  descarta después un outbox retrasado de versión anterior.

## Rollback y límites

Rollback operativo:

1. configurar `Realtime:OutboxDispatcher:Provider=Disabled`;
2. confirmar health degradado y que no se reclaman filas nuevas;
3. revertir commits RTM-002 en orden inverso;
4. si procede, aplicar una migración forward que restaure la versión anterior
   de la función pública y sus grants.

No se purgan ni eliminan filas. Los eventos ya `PROCESSED` conservan evidencia.
La migración tiene `Down` no destructivo para evitar reinstalar silenciosamente
un contrato normativo antiguo.

Los límites deliberados son una sola instancia, dispatcher co-localizado,
backplane InProcess, ausencia de broker durable y ausencia de renovación de
lease. GATE-007, GATE-010,
GATE-013, GATE-014, issue #5 y `RTM-001-CUSTOMER-SUPPORT-ROLE` permanecen
abiertos.
