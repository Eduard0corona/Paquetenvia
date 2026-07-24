# RTM-001: infraestructura SignalR segura y tenant-aware

RTM-001 agrega una frontera de distribución en tiempo real para una sola
instancia de API. PostgreSQL y REST conservan toda la autoridad; SignalR no
acepta comandos de negocio, no lee outbox y no confirma operaciones. RTM-002
será el único bloque autorizado para enlazar outbox confirmado con el puerto de
publicación.

## Arquitectura

El módulo `Realtime` sigue las cuatro capas del catálogo:

- `Realtime.Domain`: marcador de módulo sin entidades artificiales ni
  dependencias de framework.
- `Realtime.Application`: envelopes y payloads concretos, tipos de evento
  versionados, clientes tipados, audiencias, autorización estrecha,
  configuración, telemetría e `IRealtimePublisher`. No referencia SignalR ni
  Npgsql.
- `Realtime.Endpoints`: hubs tipados, selector de organización, transporte de
  access token privado, gate de conexión, JSON `snake_case`, CORS y rate
  limiting. No contiene reglas de negocio.
- `Realtime.Infrastructure`: autorización actual contra PostgreSQL, validación
  pública mediante el lector bootstrap existente, publisher basado en
  `IHubContext`, health y métricas.

`Paqueteria.Api` sólo registra middleware, módulo y rutas. Worker no fue
modificado funcionalmente. No existen tablas, migraciones, producers ni
consumidores de outbox nuevos.

## RTM-001-SERVER-DERIVED-CONNECTION-AUTHORIZATION

Esta decisión establece:

1. `organization_id` es un selector no confiable, singular y UUID `D`; no
   concede acceso.
2. La identidad activa se obtiene de la autenticación vigente, y la
   organización, membresía, rol, perfil y assignments se releen en PostgreSQL.
3. Los nombres de grupo son construidos exclusivamente por el servidor.
4. Los hubs no exponen métodos invocables por clientes; sólo overrides del
   ciclo de vida.
5. El tracking token se compara exactamente mediante la proyección bootstrap y
   todos los rechazos públicos son un mismo Problem Details `404`.
6. SignalR distribuye estado confirmado, pero nunca es autoridad.
7. El backplane es exclusivamente `InProcess` mientras GATE-013 siga abierto.
8. RTM-002 será responsable de conectar outbox confirmado con
   `IRealtimePublisher`.

## Rutas, autenticación y grupos

| Hub | Ruta | Autenticación | Grupos calculados por servidor |
| --- | --- | --- | --- |
| `OperationsHub` | `/hubs/operations` | OIDC, identidad/organización/membresía actuales y rol permitido | `org:{organization_id:D}` |
| `DriverHub` | `/hubs/driver` | OIDC, tenant autorizado, membresía DRIVER y perfil OWN activo | `driver:{driver_id:D}` y assignments activos |
| `TrackingHub` | `/hubs/tracking` | tracking token exacto, no JWT | `tracking:{public_order_id}` |

Los clientes privados envían solamente
`organization_id=<uuid>` y el callback estándar de access token. Se rechazan
selectores repetidos, vacíos, no canónicos y parámetros `group`,
`group_name` u `org_group`. El token privado en query se promueve a
`Authorization` únicamente para Operations y Driver; nunca para REST ni
Tracking.

Operations permite sólo los roles canónicos `PLATFORM_ADMIN` y `DISPATCHER`.
Platform admin requiere MFA y conserva el puerto de auditoría TEN-001.
AI-07 también menciona `customer_support` para `/ops/dashboard`, pero ese valor
no existe en el vocabulario canónico de AI-06/`OrganizationRole`. RTM-001 no
inventa el enum: esa porción queda bloqueada por contradicción normativa hasta
una decisión posterior.

Driver sólo resuelve el perfil OWN activo del usuario y tenant seleccionados.
La consulta devuelve únicamente driver ID y assignments `ACCEPTED`/`ACTIVE`;
está ordenada, limitada a `MaximumDriverAssignmentGroups + 1`, deduplicada por
la clave y falla cerrado si excede el máximo.

Tracking usa `IPublicTrackingProjectionReader`. No hace trim, normalización,
cambio de mayúsculas o interpretación JWT. Token ausente, inválido, inexistente,
expirado, revocado, mutado, cross-tenant o sin estado público mapeable produce
el mismo status, content type, título y propiedades `404`, sin delays.

## Contratos

El envelope interno tiene:

```text
event_id, event_type, occurred_at, aggregate_id, aggregate_version,
correlation_id?, payload
```

`event_id` y aggregate UUID no pueden ser vacíos, `occurred_at` debe ser UTC y
la versión no puede ser negativa. Tracking usa public order ID validado.
Payload siempre es un tipo concreto. La serialización SignalR es
`snake_case`, omite únicamente valores nulos y no agrega propiedades abiertas.

Los tipos centralizados usan `<EventName>.v1`:

- Operations: `OrderStatusChanged`, `OrderTimelineEventAdded`,
  `AssignmentChanged`, `RouteChanged`, `IncidentCreated`,
  `ExternalOfferChanged`, `NotificationStatusChanged` y
  `DriverLocationUpdated`.
- Driver: `AssignmentChanged`, `RouteChanged`, `OrderStatusChanged` y
  `ExternalOfferChanged`.
- Tracking: `PublicOrderStatusChanged` y `PublicEtaChanged`.

`IOperationsClient`, `IDriverClient` e `ITrackingClient` reflejan exactamente
esa superficie. `IRealtimePublisher` sólo acepta audiencias y envelopes
tipados; no acepta strings de grupo, métodos, tokens, `object`, JSON abierto ni
conexiones. Su adaptador usa los tres `IHubContext<THub,TClient>`.

Los payloads públicos no exponen estado interno, driver, coordenadas, PII,
hashes, documentos, object keys ni tokens. `DriverLocationUpdated` sólo existe
en Operations y `NotificationStatusChanged` no existe en Driver.

## PostgreSQL, RLS y ciclo de conexión

Cada autorización privada abre conexión y transacción explícitas, aplica
`app.current_user_id`, `app.current_org_ids` como `uuid[]`, ejecuta
`SET LOCAL ROLE paqueteria_app` y luego relee estado actual. Un retry transitorio
descarta conexión/transacción y repite el ciclo completo. El contexto
transaction-local no sobrevive al commit, rollback, cancelación ni pooling.

Operations relee usuario, organización y membresía activa antes de permitir
Dispatcher o Platform admin con MFA. Driver además relee perfil OWN activo y
assignments visibles. Tracking conserva los grants y aislamiento del lector
bootstrap existente.

El middleware deposita sólo un request estrecho de identidad ya autenticada en
el `HttpContext` de la conexión. El hub vuelve a consultar el authorizer,
calcula grupos, los agrega y registra una métrica general. Ante fallo aborta sin
grupo, estado local o causa sensible. SignalR elimina membresías al desconectar;
no hay diccionarios singleton de conexiones, tenants o drivers.

## Cliente web, reconexión y deduplicación

`apps/web/src/realtime` usa `@microsoft/signalr` fijado exactamente en el
lockfile. Cada factory crea su propia conexión:

- Operations y Driver usan token callback OIDC más selector de organización.
- Tracking usa sólo el callback efímero de tracking token.

Ninguna implementación persiste tokens en localStorage, sessionStorage,
IndexedDB, cookies o claims nuevos. La política reversible de reconexión es
`[0, 2000, 10000, 30000]` milisegundos. Después de `onreconnected` siempre se
ejecuta el callback REST obligatorio y se reemplazan las versiones locales por
el snapshot autoritativo. Si el sync falla, la conexión se detiene.

`RealtimeEventGuard` deduplica por `event_id` con LRU/TTL acotados y mantiene un
mapa de versiones de aggregates también acotado. Ignora versiones menores a la
última aplicada. Un evento SignalR nunca representa aceptación de un comando.
No se agregan pantallas ni integración productiva.

## CORS, rate limiting y configuración

CORS tiene policy exclusiva para los hubs, credenciales habilitadas sólo con
orígenes HTTP(S) exactos configurados y nunca usa wildcard. Desarrollo declara
`https://web.synthetic.local`; el provider permanece `Disabled` por defecto.

La ventana fija limita negotiate, transportes e intentos de reconexión. Usa el
subject autenticado cuando existe y un hash de la IP como fallback; nunca token,
tenant, driver, order o grupo. La cola es cero y el rechazo es `429`. Los
valores base `20/60s` son configuración sintética reversible, no SLA.

Ejemplo local:

```powershell
$env:Realtime__Provider = "SignalR"
$env:Realtime__Backplane = "InProcess"
$env:Realtime__AllowedOrigins__0 = "https://web.synthetic.local"
$env:PublicTracking__Provider = "PostgreSql"
$env:ConnectionStrings__Paqueteria = "<synthetic-local-connection-string>"
```

SignalR habilitado exige connection string, origen seguro y, fuera de Testing,
tracking PostgreSQL productivo. Valores inválidos, wildcard, Redis o lista vacía
fallan al inicio. Disabled devuelve `503`, health queda degradado y el publisher
lanza; nunca simula publicación exitosa.

## Observabilidad

El meter registra conexiones activas/aceptadas/rechazadas, desconexiones,
duración de autorización, publicaciones, errores y duración de envío. Sólo usa
`hub`, `outcome`, `auth kind` y `event type` versionado.

Logs de fallo usan mensajes generales:
`realtime_connection_authorization_failed` y
`realtime_connection_authorization_retry`. No incluyen query string,
Authorization, tokens, claims, payload, IP completa, IDs, group names ni PII.

## Pruebas y límites

La cobertura incluye contratos AI-12, reflexión de payloads/envelopes y ausencia
de client methods, autorización y aislamiento con cliente SignalR real,
audiencias separadas, matriz uniforme de tracking, privacidad, CORS, rate
limiting, configuración cerrada, reconexión/sync REST/deduplicación web y
PostgreSQL 18/PostGIS con tenant correcto, cross-tenant, estados suspendidos,
pooling, retry y cancelación.

RTM-001 no valida entrega desde outbox. No implementa RTM-002, TRK-001,
DRV-003, ubicación, ETA, routing, incidents, notifications, external offers,
pantallas, emisión de tokens ni múltiples nodos. Antes de desplegar más de una
API debe resolverse GATE-013 y validarse Redis backplane o un servicio
administrado.

## Rollback

1. Configurar `Realtime:Provider=Disabled` y verificar `503`/health degradado.
2. Revertir commits RTM-001 en orden inverso.
3. No ejecutar DDL inverso ni eliminar datos.

RTM-001 no persiste estado de negocio, no crea esquema y no requiere migración
de datos para rollback.
