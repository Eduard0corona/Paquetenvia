# TRK-001: seguimiento público mínimo

TRK-001 implementa el ciclo de vida interno del bearer token, el endpoint
`GET /api/v1/tracking/{token}` y la página mobile-first `/track/{token}`.
ORD-002, RTM-001 y RTM-002 ya estaban integrados. REST/PostgreSQL es siempre la
autoridad; SignalR sólo solicita una nueva lectura REST.

No existe API productiva para administrar tokens. El token no se agregó a
`OrderResponse`, no se genera durante ORD-001 y no se publica en notificaciones
u outbox. Un canal futuro, previsiblemente NTF-001 o un portal contratado,
deberá invocar el servicio interno y entregar el grant. Este alcance no afirma
que el destinatario ya recibe el enlace.

## Token, persistencia y ciclo de vida

Se reutiliza exclusivamente `orders.public_tracking_tokens`; no hay DDL,
migración, tabla ni columna nueva. Sólo se persisten los 32 bytes de
`token_hash`, nunca plaintext, URL, public ID, IP, user agent, referrer, canal o
PII.

`TrackingTokenHasher` genera 32 bytes con CSPRNG y los representa como 43
caracteres Base64URL sin padding. El hash es SHA-256 de los bytes UTF-8 exactos,
sin trim, normalización, cambio de mayúsculas ni doble hash:

```text
token:  AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA
sha256: eb9f16800c9029ffca85695763d23c3ace71011cf40e9354acd810205e250f87
```

`IPublicTrackingTokenService` expone `IssueAsync`, `RotateAsync` y
`RevokeAsync`. Sólo `PublicTrackingTokenGrant` contiene plaintext y se devuelve
después del commit. Un token perdido no puede recuperarse.

Cada operación usa `TenantTransactionContext<OrdersDbContext>`, RLS, bloqueo de
la orden y `pg_advisory_xact_lock` con namespace propio `TRK1` y clave estable
derivada del order UUID. El lock PostgreSQL serializa operaciones por orden:

- emisión rechaza si ya existe un token activo;
- rotación revoca todos los tokens no revocados e inserta el nuevo hash dentro
  de la misma transacción;
- revocación marca tokens activos, es repetible y sólo audita un cambio real.

Nunca se borran filas históricas. La restricción única de `token_hash` gobierna
colisiones. Cada insert usa savepoint y hasta tres candidatos nuevos; agotar
los tres intentos falla cerrado y revierte token, revocaciones y auditoría.

La vida default es 168 horas, con límites de cinco minutos y 30 días. Una
expiración solicitada debe ser UTC. El reloj se captura una vez y no existe
sliding expiration por lectura, refresh o SignalR.

La auditoría append-only participa en la misma transacción:

```text
TRACKING_TOKEN_ISSUED
TRACKING_TOKEN_ROTATED
TRACKING_TOKEN_REVOKED
```

El payload se limita a `order_id`, `token_id`, `expires_at`,
`previous_tokens_revoked_count` y `request_id`. Nunca incluye token, hash, URL,
public ID, bearer, contacto, dirección o datos de red.

## Endpoint público

El endpoint es anónimo y usa `IPublicTrackingProjectionReader`. Pasa el token
sin normalizar o pre-hashear; el reader conserva
`security.get_public_tracking_projection(@token)` como único plan de acceso.
El DTO 200 contiene exactamente `public_id`, `public_status`,
`aggregate_version`, `estimated_window` y `timeline[] { code, occurred_at }`.

El mapa de 17 estados reutiliza `PublicOrderStatusPolicy` y
`security.map_public_order_status`. Un estado no mapeado falla cerrado. La
timeline sólo contiene los once `public_event_code` contratados, sin payload,
actor, metadata, proof, assignment o incidente. `estimated_window` permanece
objeto/null; la UI sólo representa `from`/`to` si ambos son timestamps válidos.
No inventa ETA.

Tokens desconocidos, mutados, expirados, revocados, malformados o sin proyección
visible producen el mismo 404:

```json
{"type":"about:blank","title":"Not Found","status":404}
```

Una caída técnica produce 503 genérico, nunca 404 ni información de SQL,
Npgsql, conexión, stack o token. Las respuestas 200/404/429/503 llevan
`Cache-Control: no-store, private`, `Pragma: no-cache`,
`Referrer-Policy: no-referrer`, `X-Content-Type-Options: nosniff` y
`X-Robots-Tag: noindex, nofollow, noarchive`, sin ETag o Last-Modified.

`PublicTrackingLookup` permite 60 requests por 60 segundos, queue cero, y
particiona por SHA-256 de la dirección de red normalizada. No usa o registra el
token/IP. Es por instancia; GATE-013 continúa abierto para coordinación
distribuida.

`PublicTracking:AllowedOrigins` es una allowlist HTTP/HTTPS exacta, GET y
`Accept`, sin wildcard o credentials. Vacía falla cerrada cross-origin;
same-origin funciona. Es distinta de `Realtime:AllowedOrigins`; una PWA
cross-origin exige mantenerlas coherentes. SignalR admite el header técnico
`X-SignalR-User-Agent`.

## Página, tiempo real y privacidad

Next mantiene `/track/{token}` y hace rewrite interno al shell `/track`. El
token se deriva de `window.location.pathname`; no es prop de Server Component,
dato RSC o metadata. El parser sólo acepta un segmento Base64URL canónico de 43
caracteres y slash final opcional. `/track` o cualquier otra forma muestra el
mismo not-found local.

El cliente usa GET, `Accept: application/json`, `cache: no-store`,
`credentials: omit`, `referrerPolicy: no-referrer`, timeout y AbortSignal. El
parser runtime exige propiedades exactas, enteros seguros, máximo ocho
propiedades de ventana, 200 eventos, códigos/timestamps válidos y orden
temporal. Una respuesta ampliada o inválida falla cerrado y no reemplaza el
último snapshot válido.

La página muestra marca de plataforma (`Paquetenvia`), public ID, estado,
timeline, ventana/fallback, actualización, conexión, soporte y botón Actualizar.
Sólo agrega `NEXT_PUBLIC_TRACKING_BRAND_NAME` y
`NEXT_PUBLIC_TRACKING_SUPPORT_URL`; soporte admite HTTPS o `mailto:`. Sin URL
muestra “Comunícate por el mismo canal donde recibiste este enlace.” No existe
branding o soporte tenant-specific.

Los timestamps llegan en UTC y todas las superficies públicas se muestran
mediante `Intl.DateTimeFormat` con `timeZone: "America/Mazatlan"`,
independientemente de la zona configurada en el dispositivo del destinatario.
Timeline, `estimated_window.from`, `estimated_window.to` y `lastUpdated`
comparten el formatter puro centralizado. La página indica una vez, de forma
visible, que los horarios se muestran en hora de Mazatlán.

Loading, not-found, rate limited, indisponible, reconectando y offline son
estados explícitos. Un 404 detiene TrackingHub y limpia la proyección. Un 503 o
error de red conserva el snapshot sólo en memoria. El polling cancelable corre
cada 30 segundos, pausa con `document.hidden`, refresca al volver visible y
mantiene un request activo.

`PublicOrderStatusChanged` y `PublicEtaChanged` se deduplican, validan versión y
public ID esperado, y aplican debounce de 250 ms. Sólo disparan REST.
`PublicEtaChanged` no adquiere productor. Reconectar vuelve a autorizar y
resincroniza.

`Realtime:TrackingMaximumConnectionLifetimeSeconds` vale 60 (rango 5–300).
Middleware aborta sólo TrackingHub; no afecta OperationsHub/DriverHub. Una
conexión establecida observa revocación/expiración de forma acotada al
reconectar, no instantánea.

No se usan analytics, recursos externos, cookies, local/session storage,
IndexedDB, Cache API, clipboard, console logs o telemetría externa. El bearer
permanece necesariamente en URL e historial. Next omite incoming-request logs
para `^/track/`; proxies, ingress y CDN también deben redactar esos paths.

Los headers de página incluyen no-store/private, no-referrer, noindex, nosniff,
Permissions-Policy sin geolocalización/cámara/micrófono/pago/USB y CSP cerrada
a self más orígenes HTTP/WS de API. El Service Worker usa
`paquetenvia-driver-shell-v4`: `/track`, `/api/v1/tracking`,
`/hubs/tracking`, `access_token` y Authorization son network-only. No almacena
tracking ni usa el shell Driver como fallback; DRV-001/DRV-002 no cambian.

La UI usa main, un h1, headings jerárquicos, lista/time semánticos, aria-live,
aria-busy, controles de 44 px, foco visible, contraste y reduced motion. Se
prueba a 320×568 y 430×932; el layout cubre también 360×800 y 390×844. No hay
mapa, GPS, QR o live tracking.

## Telemetría, configuración, pruebas y CI

`Paquetenvia.PublicTracking` emite contadores de baja cardinalidad para outcome,
contrato/provider y rate limit. Realtime conserva accepted/rejected/closed por
hub. El puerto web es no-op. Nunca incluyen bearer, hash, URL, public ID, IP,
order, tenant, timeline o estado por orden.

```json
{
  "PublicTracking": {
    "Provider": "PostgreSql",
    "TokenLifetimeHours": 168,
    "TokenCollisionRetryCount": 3,
    "AllowedOrigins": ["https://tracking.example"],
    "LookupPermitLimit": 60,
    "LookupWindowSeconds": 60
  },
  "Realtime": {
    "AllowedOrigins": ["https://tracking.example"],
    "TrackingMaximumConnectionLifetimeSeconds": 60
  }
}
```

`NEXT_PUBLIC_API_BASE_URL` debe ser absoluto y HTTPS en producción. No se
agregaron valores reales, secretos, paquetes npm/NuGet o lockfiles.

Vitest cubre ruta, parser, clasificación HTTP, etiquetas, CSP/headers y SW.
También ejecuta directamente el formatter para cruces de día, fecha de verano,
ventana, valores `Date`, independencia de zona e inputs inválidos.
`PublicTrackingPostgreSql` usa PostgreSQL/PostGIS real para lifecycle, RLS,
auditoría, colisiones, 25 rotaciones, 100 tokens aleatorios, HTTP/CORS y
expiración. `PublicTrackingPwa` usa Kestrel, Next, Chromium y TrackingHub reales,
viewports, storage/cache y log redaction. Incluye un contexto configurado en
`America/New_York` que demuestra que timeline, ventana y última actualización
se siguen renderizando en `America/Mazatlan`. Otra prueba revoca durante una
conexión, espera el corte y demuestra que la reconexión 404 no recupera grupo.
Los contratos y OutboxSignalRDelivery permanecen verdes.

CI conserva ocho jobs y agrega `Validate public tracking`: restore locked,
frozen install, Chromium, PostgreSQL/PostGIS, Kestrel, Next, Worker/outbox,
TrackingHub, `.trx` al fallar, diagnósticos redacted y limpieza.

## Riesgos, límites y rollback

- El bearer vive en URL/historial; ingress/CDN deben redactar paths.
- No existe distribución productiva. Rotar exige entregar el nuevo token por
  un canal futuro; el anterior no puede recuperarse.
- Invalidación REST es inmediata; en conexión activa está acotada a 60 s.
- Rate limiting es por instancia y Redis aún no participa.
- SignalR no sustituye REST. No hay GPS, ETA calculada, branding o soporte
  tenant-specific. Estados no mapeados fallan cerrado.
- Issue #5 sigue abierto. GATE-007, GATE-010, GATE-013, GATE-014 y GATE-011
  siguen abiertos. ADR-032/033 no se implementaron.
- `pnpm audit` puede conservar advisories; se reporta el resultado real.

Fuera de alcance: OBS-001, OPS-001/002, TEN-003, PRC-002, FIN-001, INC-001,
NTF-001, email/SMS, portal, GPS, ETA, mapa, QR, analytics, push, tracking
offline, WAF, CDN, IaC y deployment.

Rollback:

1. Retirar entrada pública `/track`, deshabilitar `PublicTracking:Provider` y
   retirar CORS público.
2. Desplegar shell anterior e incrementar el cache name del rollback.
3. Revertir commits TRK-001.
4. Conservar filas históricas; para seguridad, revocar activos sin borrarlos.
5. Conservar orders, order_events, outbox, TrackingHub y auditoría.
6. No ejecutar DDL, eliminar `orders.public_tracking_tokens`, borrar caches
   ajenas o modificar datos DRV-002.
