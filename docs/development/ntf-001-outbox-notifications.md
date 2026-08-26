# NTF-001: outbox y notificaciones IN_APP

## Alcance implementado

NTF-001 agrega `Notifications.Domain`, `Notifications.Application` y
`Notifications.Infrastructure`, sin endpoints ni UI. El trigger funcional es
`orders.created`, la audiencia es `OWNER_ORG_DISPATCHERS` y el único canal es
`IN_APP`. La entrega termina en un sink sintético en memoria; no existe inbox,
provider externo, red, credenciales ni persistencia del cuerpo renderizado.

El flujo durable es:

`orders.created` → snapshot de audiencia → Notification PENDING →
`notifications.send-requested` → provider sintético → transición de estado →
`notifications.status-changed` → Realtime Operations.

Cada efecto tiene una fila outbox propia. No se comparte lifecycle, lease,
`attempts` ni `available_at` entre consumidores.

## Routing y ownership

`security.resolve_outbox_consumer(topic)` asigna exactamente un owner:

| Owner | Topics |
| --- | --- |
| `REALTIME` | `orders.status-changed`, `orders.timeline-event-added`, `dispatch.assignment-changed`, `notifications.status-changed` |
| `NOTIFICATIONS` | `orders.created`, `notifications.send-requested` |
| `UNROUTED` | cualquier otro topic |

Realtime usa `security.claim_realtime_outbox`; Notifications usa
`security.claim_notifications_outbox`; el dispatcher técnico de topics sin
owner usa `security.claim_unowned_outbox` y los terminaliza inmediatamente como
`DEAD/UNKNOWN_TOPIC`, sin parsear payload ni invocar providers. Location outbox
conserva su lane independiente. El worker no tiene `EXECUTE` sobre la claim o
recovery globales anteriores después del cutover.

## Persistencia y seguridad

La migración
`20260815000100_AddTenantSafeOutboxNotifications` evoluciona la tabla canónica
`notifications.notifications` y crea:

- `notifications.notification_templates`;
- `notifications.notification_status_events`;
- índices parciales y constraints de versionado, recipient e idempotencia;
- template tenant-local `orders.created.operations` v1/`IN_APP`;
- provisioning del mismo template para organizaciones futuras;
- funciones SECURITY DEFINER para audience, expansión, lectura, outcomes,
  claims y stale recovery.

Templates e historial son inmutables; las tres tablas Notifications tienen
FORCE RLS. No existe FK hacia Identity: `recipient_user_id` es un UUID bajo
contrato. El worker sólo recibe EXECUTE sobre funciones allowlisted y no recibe
acceso directo a tablas Notifications. El executor recibe columnas mínimas de
Identity y Organizations para leer UUID, estado, organización y rol; no puede
leer email, nombre, teléfono, ciphertext ni otros datos de contacto.

El cutover se evalúa bajo el executor BYPASSRLS para impedir que FORCE RLS
oculte filas activas. Falla si existe un `orders.created` histórico en
PENDING/RETRY/PROCESSING o si la tabla Notifications legada no está vacía. No
reproduce, elimina ni terminaliza datos históricos.

## Audience y templates

Organizations publica el reader de memberships `ACTIVE` con rol exacto
`DISPATCHER`. Identity publica el reader acotado de usuarios `ACTIVE`.
Notifications compone ambos UUID sets, elimina duplicados, ordena, exige que
Identity devuelva un subset y usa `limit + 1` con máximo 500. La audiencia se
resuelve una vez al expandir el source event; los retries de delivery sólo leen
el snapshot persistido.

Zero recipients termina el source como
`PROCESSED/NO_ELIGIBLE_RECIPIENT`. Lookup de template exige owner, key, versión
y channel exactos, sin fallback global. Sólo se permiten
`order_public_id`, `order_status` y `occurred_at`.

## Delivery, retry y stale recovery

El provider sintético renderiza sólo en memoria y calcula receipt e idempotency
key deterministas a partir de Notification/template estable. Soporta success,
transient, permanent y ambiguous timeout. Success produce SENT; permanent
produce FAILED/DEAD; transient y ambiguous conservan PENDING y programan RETRY
exclusivamente con `available_at`.

El backoff exponencial es determinista, configurable y acotado. Cada outcome
persistido incrementa `Notification.attempts` y `version` exactamente una vez,
agrega historial y emite una fila `notifications.status-changed`. CAS usa la
versión esperada y el lease actual. Una redelivery posterior a un outcome
terminal no vuelve a mutar la Notification.

`security.recover_stale_notifications_outbox` sólo toca filas Notifications.
Con intentos disponibles reprograma RETRY; agotados, entrega un lease nuevo de
finalización. Esa finalización no llama provider ni incrementa attempts, cambia
a FAILED, incrementa version, agrega historial/status outbox y deja el
send-request DEAD de forma atómica. Cancelación del host no fabrica settlements.

## Realtime y observabilidad

Realtime reutiliza `NotificationStatusChangedPayload` y
`PublishOperationsNotificationStatusChangedAsync`; no agrega Hub ni contratos
paralelos. La publicación usa exclusivamente el grupo Operations del tenant.

Logs y métricas usan owner, lane, channel, outcome, status y códigos
allowlisted. No incluyen tenant/user/order/notification IDs, topic desconocido
raw, payload, variables, recipient ni mensaje renderizado. Readiness valida la
configuración y acceso al resolver/funciones sin leer PII.

## Validación

Las suites cubren routing disjoint/exhaustivo, parsing y tenant mismatch,
render e idempotencia deterministas, audience sort/dedupe/subset/límite,
mappings del provider, backoff, CAS, attempts/version y max attempts.
PostgreSQL real cubre catálogo, FORCE RLS, grants, aislamiento cross-tenant,
unicidad, claims mutuamente exclusivas, SKIP LOCKED, lease perdido, stale
recovery, finalization lease, unknown topics y guardas de cutover/rollback.
La integración recorre el lifecycle completo, filtros reales de audiencia,
zero/invalid audience, retry sin re-resolver audience, max finalization y
publicación SignalR de NotificationStatusChanged a Operations sin fuga a otro
tenant.

## Rollback y limitaciones

El downgrade de schema EF y el rollback operativo de routing son operaciones
distintas. `Migration.Down()` no ejecuta el rollback operativo: falla cerrado
con `NTF001_SCHEMA_DOWNGRADE_NOT_SUPPORTED`. Esto impide que EF elimine la fila
de `platform.__ef_migrations_history_notifications` mientras el schema y los
datos NTF permanecen aplicados.

Rollback operativo de routing:

1. deshabilitar/detener el dispatcher Notifications;
2. drenar o terminalizar todas las filas Notifications-owned;
3. ejecutar `OperationalRollbackSql` bajo el rol autorizado sólo cuando no
   existan filas activas;
4. desplegar o revertir al worker anterior;
5. validar que se restauró EXECUTE de la claim/recovery global anterior y se
   revocaron las claims/recovery NTF al worker;
6. validar Realtime y location outbox.

La guarda del rollback operativo falla cerrado mientras exista una fila
Notifications en PENDING/RETRY/PROCESSING. El rollback conserva la entrada de
migration history, las tablas Notifications, templates, status history y sus
datos; no reconstruye eventos. Una futura retirada física del schema requiere
otro paquete y otra migración explícitamente autorizados.

NTF-001 no implementa inbox, UI, providers externos, email/SMS/push, EXT-001,
deployment, piloto ni producción.
