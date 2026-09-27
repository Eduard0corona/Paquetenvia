# DRV-001: lista y detalle de paradas

## Alcance

DRV-001 agrega una experiencia PWA mobile-first y de solo lectura para que un
repartidor consulte sus paradas asignadas. Expone exclusivamente:

- `/driver/stops`;
- `/driver/stops/[id]`.

La lista muestra el identificador público, tipo, estado, `address_summary`,
sincronización y un enlace real al detalle. El detalle se resuelve desde el
snapshot de la lista; no consulta `GET /orders/{id}` ni agrega rutas backend.

DRV-001 no implementa acciones operativas, transiciones, POD, cola offline de
eventos, reintento de mutaciones, GPS, mapas, navegación, incidentes, failed
attempt ni contact relay token. DRV-002 deberá extender la persistencia sin
romper su partición.

## Dependencias y arquitectura

No se agregan dependencias. La implementación reutiliza React, Next.js,
`@microsoft/signalr`, Vitest y Microsoft.Playwright existentes, además de
IndexedDB y Cache API nativos.

La frontera `apps/web/src/driver/` separa:

- `contracts/`: DTO exacto, parser runtime y etiquetas;
- `api/`: URL pública y cliente REST;
- `session/`: puerto de sesión en memoria;
- `cache/`: snapshot IndexedDB y partición opaca;
- `state/`: controlador de carga, fallback y revocación;
- `realtime/`: adaptación del cliente DriverHub existente;
- `components/`: lista, detalle y estados accesibles;
- `telemetry/`: puerto de baja cardinalidad deshabilitado por defecto.
- `routing/`: parser estricto del pathname y selección del stop de la partición
  activa.

Los componentes no acceden directamente a `fetch`, IndexedDB ni SignalR. El
controlador coordina esos puertos y conserva REST como autoridad.

## DTO y validación fail-closed

`DriverStop` contiene exactamente:

```text
order_id
aggregate_version
order_public_id
stop_type
status
address_summary
```

El parser puro exige raíz array, objetos no nulos, propiedades exactas, UUID
canónico, versión agregada entera segura y mayor o igual a uno, strings no
vacíos y acotados, enums exactos, ausencia de UUID duplicados y un máximo
defensivo de 500 elementos. No modela `contact_token`.

Una respuesta inválida no se representa parcialmente, no reemplaza un snapshot
válido y no registra body, headers ni detalles técnicos.

## Sesión, token y organización activa

El puerto `DriverSession` requiere:

```typescript
interface DriverSession {
  readonly organizationId: string;
  readonly cacheNamespace: string;
  getAccessToken(): string | Promise<string>;
}
```

El adaptador del navegador sólo acepta una organización UUID y un namespace
opaco. El bearer token se obtiene en memoria para cada solicitud y no se guarda
en IndexedDB, Cache API, localStorage, sessionStorage ni variables públicas.
Sin sesión válida no se llama al backend ni se abre una partición offline.

La integración productiva de autenticación queda pendiente del adaptador de
identidad aprobado. Las pruebas inyectan una sesión sintética antes de cargar
la página; no existe un token mock en el bundle productivo.

Cuando cambia la organización, React desmonta el controlador anterior, cancela
su request, detiene DriverHub, descarta el estado y abre únicamente la nueva
partición antes de sincronizar por REST.

## Cliente REST

El cliente consulta únicamente `GET /api/v1/driver/me/stops` con:

```text
Authorization: Bearer <token actual>
X-Organization-Id: <organización activa>
Accept: application/json
```

Usa `cache: no-store`, timeout y `AbortController`; no envía
`Idempotency-Key`. Valida el content type antes del JSON y clasifica 401, 403,
5xx/red, timeout, cancelación y contrato inválido sin exponer respuestas.

`NEXT_PUBLIC_API_BASE_URL` es opcional y no secreta. Debe ser absoluta; HTTP
sólo se acepta fuera de Production y Production exige HTTPS. Sin configuración
se usa el mismo origen.

## IndexedDB, partición y detalle offline

La base es `paquetenvia-driver-stops-v1`, el object store es `snapshots` y el
schema es `1`. Cada registro contiene únicamente:

- `schemaVersion`;
- `synchronizedAt`;
- la lista de los seis campos permitidos de `DriverStop`;
- una key técnica de partición.

La key es un SHA-256 base64url del namespace opaco y la organización. No contiene
el token, el tenant en claro ni PII. Un 200 válido reemplaza atómicamente el
snapshot completo, conserva el orden REST y elimina paradas que dejaron de
estar asignadas. El orden REST sigue la ruta del conductor (RTE-001): primero las
asignaciones ligadas a una ruta, por ruta y `sequence` de la parada del tipo al que
se dirige el conductor (`PICKUP` antes de la custodia, `DELIVERY` después, `RETURN`
al devolver; si la ruta no tiene parada de ese tipo, la primera de la orden); después las
no ruteadas, por antigüedad de la asignación. El tipo de parada usa la custodia
única de ORD-002: un `PICKED_UP` en el historial de la orden; una foto de
recolección usada como evidencia de incidencia no convierte la parada en entrega.

Fallas de red, timeout o 5xx pueden leer sólo el último snapshot válido de la
partición activa. Lista y detalle muestran el timestamp y el estado “Sin
conexión”. Un UUID no presente muestra el mismo estado de parada no disponible;
no consulta otros endpoints ni prueba otras particiones.

401 y 403 son revocaciones conocidas: detienen realtime, limpian memoria y la
partición activa, y nunca caen a datos offline. Sin conexión no es posible
detectar una revocación nueva.

`address_summary` es el único dato de ubicación visible o persistido. La
retención temporal del snapshot no se presenta como una decisión legal.

## Service Worker

### DRV-001-DEF-001

La prueba original sólo abortaba la API: el servidor Next continuaba accesible
y podía resolver una ruta dinámica nunca visitada. Eso demostraba fallback de
datos, pero no navegación con el navegador realmente offline.

La corrección usa un shell cliente genérico para `/driver/stops` y
`/driver/stops/[id]`. Ambas páginas montan el mismo componente sin pasar el ID
desde el servidor. Ya hidratado, el cliente interpreta exclusivamente
`window.location.pathname` mediante un parser puro que acepta la lista o un
UUID canónico, con slash final opcional. Segmentos adicionales, UUID no
canónico, encoded slash, traversal, query/hash como parte del ID y otras rutas
producen el mismo not-found sin consultar otra partición.

“Ver detalle” y “Volver a mis paradas” son anchors de documento completo. No
dependen de `next/link`, prefetch, estado en memoria del router ni RSC remoto.

El cache propio versionado es `paquetenvia-driver-shell-v2` y su key canónica
es `/driver/stops`.

- Navegaciones válidas bajo `/driver/stops`: network-first; ante fallo se busca
  primero la respuesta exacta y después el shell canónico de la lista.
- La respuesta online de la lista se guarda también bajo la key canónica. Una
  ruta de detalle exacta puede existir, pero no es requisito y no se precachean
  UUIDs.
- Manifest y assets estáticos versionados: cache-first.
- Datos: siempre IndexedDB, nunca Cache API.

El primer documento que registra el Service Worker no queda necesariamente
bajo su control. La validación espera `navigator.serviceWorker.ready`, recarga
online, confirma `navigator.serviceWorker.controller` y comprueba que los
chunks estáticos y el shell canónico están en Cache API antes de desconectar.
El shell es presentación genérica: no contiene public ID, UUID ni
`address_summary`. IndexedDB particionado continúa siendo la única fuente de
datos offline.

Son network-only las rutas `/api/v1/**`, `/hubs/**`, autenticación, requests con
`Authorization`, URLs firmadas, respuestas con `Set-Cookie`, proof objects,
MinIO/S3 y todo request cross-origin. La instalación es best-effort y ejecuta
`skipWaiting`; la activación reclama clientes y elimina sólo caches antiguos
con prefijo `paquetenvia-driver-shell-` o el prefijo Foundation heredado.

## REST, SignalR y reconnect

La carga inicial siempre consulta REST. Después construye el cursor mediante
`mapDriverAggregateVersions` e inicia `createDriverConnection`.
`AssignmentChanged` y `OrderStatusChanged` sólo programan un refresh REST con
debounce interno de 250 ms. No se usan `RouteChanged` ni
`ExternalOfferChanged`.

El payload SignalR nunca se convierte directamente en estado. Un evento
desconocido, duplicado o en ráfaga conserva REST como fuente de verdad. Tras
`onReconnected` se obliga una resincronización REST. Un fallo realtime deja
disponible la lectura REST y el desmontaje limpia conexión, timers y requests.

## UI, accesibilidad y privacidad

Los estados explícitos son: sesión no disponible, loading con skeleton,
lista, detalle, vacío, offline con/sin snapshot, 401, 403, error recuperable y
contrato inválido. Las etiquetas españolas cubren los tres tipos y los ocho
estados canónicos; un enum desconocido no llega a UI.

La interfaz usa `main`, headings jerárquicos, listas semánticas, links y
botones reales, `aria-busy`, `aria-live`, foco visible, targets de 44 px,
safe-area, wrap de dirección, landscape y `prefers-reduced-motion`. Se prueba
sin scroll horizontal en 320×568, 360×800, 390×844 y 430×932.

No muestra UUID interno, aggregate version, assignment/driver/organization ID,
teléfono, contacto, coordenadas, costo, paquete, pruebas, incidentes ni tokens.
No agrega analytics. La telemetría está deshabilitada y sólo admite categorías
seguras y buckets `0`, `1`, `2-5`, `6-20`, `21+`.

## Pruebas y CI

Vitest cubre parser, DTO/OpenAPI, cliente REST, URL pública, sesión, partición,
cache, controlador, realtime, etiquetas, telemetría y reglas estructurales del
Service Worker. También cubre el parser de path, selección exclusivamente
dentro de los stops actuales, anchors de documento y que ambas páginas usan el
mismo shell sin prop server-side.

La categoría .NET `DriverStopsPwa` ejecuta Chromium real contra un servidor
Next real. Cubre la matriz móvil, teclado, detalle, vacío, cambio de
organización, 401/403 y contrato inválido. Para DRV-001-DEF-001 espera control
del Service Worker y persistencia IndexedDB, demuestra que el detalle nunca se
visitó y no tiene entrada exacta, pone el browser context offline y prueba:

- inaccesibilidad real de Next/origin y API;
- apertura del detalle desde el shell canónico e IndexedDB;
- recarga del detalle todavía offline;
- historial del navegador y enlace de regreso todavía offline;
- not-found para un UUID ausente;
- snapshots A y B aislados y una nueva sesión/organización sin datos.

Un caso adicional usa PostgreSQL, API Kestrel, outbox y DriverHub reales para
probar señal, debounce y segunda lectura REST.

Ejecución local:

```powershell
pnpm --dir apps/web install --frozen-lockfile
pnpm --dir apps/web test
pnpm --dir apps/web build
dotnet build .\tests\Paqueteria.IntegrationTests\Paqueteria.IntegrationTests.csproj
pwsh .\tests\Paqueteria.IntegrationTests\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet test .\tests\Paqueteria.IntegrationTests\Paqueteria.IntegrationTests.csproj --filter "Category=DriverStopsPwa"
```

El job aislado `Validate driver stops PWA` instala el lockfile congelado y
Chromium, y ejecuta toda la categoría. El aislamiento evita mezclar el proceso
Next y sus recursos Docker con el job .NET general.

## Límites y riesgos

- La sesión productiva depende todavía del adaptador de identidad aprobado.
- La caché contiene `address_summary`; partición y limpieza son críticas.
- Un Service Worker obsoleto puede conservar un shell anterior.
- SignalR no sustituye REST.
- Offline no puede comprobar revocaciones nuevas; 401/403 conocidas limpian.
- No existen cola offline de mutaciones, contact relay token, mapa ni navegación.
- Issue #5 permanece abierto. La corrida de aceptación respondió con 11
  vulnerabilidades heredadas (6 high y 5 moderate) en Next 16.2.10,
  sharp/libvips y brace-expansion; DRV-001 no cambia versiones ni overrides.
- GATE-007, GATE-010, GATE-013 y GATE-014 continúan abiertos.

## Rollback

1. Deshabilitar el enlace o entrada hacia `/driver/stops`.
2. Desplegar el shell anterior.
3. Revertir los tres commits de DRV-001.
4. Incrementar el nombre de cache del Service Worker de rollback.
5. Eliminar únicamente caches `paquetenvia-driver-shell-*` obsoletos.
6. Eliminar únicamente IndexedDB `paquetenvia-driver-stops-v1` durante la
   activación controlada del rollback.
7. Conservar backend, assignments, orders y eventos.
8. No modificar datos PostgreSQL ni ejecutar DDL.
9. No deshabilitar DSP-002 ni RTM-002.
10. No eliminar almacenamiento ajeno.

El rollback del frontend no requiere migraciones ni cambios backend.
