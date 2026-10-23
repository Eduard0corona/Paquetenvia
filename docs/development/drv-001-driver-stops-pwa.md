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

Actualización `UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11`: `DriverStop` agrega
`contact_short_name`, `zone_name`, `service_window`, `cod` y `navigation`; la
dirección completa con referencias y el nombre corto se muestran y se guardan en
el teléfono solo para las paradas asignadas vigentes (snapshot esquema 2, base
versión 3). Ver `ui-001-phase3-driver-stop-details.md`; lo que sigue describe
DRV-001 tal como se entregó.

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
estar asignadas. Actualización DRV-STALE-STOP-READ-2026-10-14: de una respuesta
que ya iba en camino cuando la app aceptó una versión más nueva de una parada,
esa parada no se toma (se queda la que la app tiene y una parada que ya salió
no vuelve); las respuestas se toman de una en una, en el orden en que llegan.
Ver `driver-photo-selection-refresh.md`. El orden REST sigue la ruta del conductor (RTE-001): primero las
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

`address_summary` es el único dato de ubicación visible o persistido (hasta
`UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11`, que agrega la dirección completa y el
nombre corto con su propia retención). La retención temporal del snapshot no se
presenta como una decisión legal.

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

“Ver detalle” y “Volver a mis paradas” son anchors reales sin `next/link`,
prefetch ni RSC remoto. Desde `DRV-PWA-INAPP-NAVIGATION` (2026-10-19; es el nombre
de este arreglo, no una decisión del decision-log: no cambia ningún contrato
normativo) un clic simple (o Enter) no carga página: `navigateWithinDriverStops`
(`src/driver/routing/driver-stops-route.ts`) hace `history.pushState`, avisa a
la pantalla con `paquetenvia:driver-stops-route-changed`, que vuelve a leer
`window.location.pathname` con el mismo parser, y empieza arriba; atrás y
adelante del navegador se leen con `popstate`. Es la integración con History
API que documenta el App Router de Next.js: no pide nada al servidor, así que
funciona igual sin conexión, y la sesión en memoria, los controladores y la
partición IndexedDB abierta siguen como estaban. Como cada carga de página
antes, cada movimiento vuelve a leer las paradas por REST; sin conexión esa
lectura cae al snapshot guardado y la pantalla dice «Mostrando última
información disponible». Un `<Link>` de Next no sirve
aquí: sin conexión falla la carga del RSC y Next recurre a una navegación de
documento completo. Un clic con modificador o un clic secundario mantienen el
comportamiento del navegador (nueva pestaña), y cualquier ruta que no sea la
lista o un detalle canónico se deja al navegador. Al volver a la lista el foco
pasa al título «Mis paradas», como en una carga de página; en el detalle sigue
yendo a su encabezado.

En modo BFF (`NEXT_PUBLIC_AUTH_MODE=bff`, el del piloto) la sesión del
repartidor vive sólo en memoria y `BffSessionBootstrap` la reconstruye con
`GET /auth/session` en cada carga de página, que es network-only. Antes de este
cambio cada “Ver detalle” sin conexión era una carga de página: el shell salía
del Service Worker, `/auth/session` fallaba y la pantalla mostraba «Sesión no
disponible». Ahora moverse entre lista y detalle nunca recarga. Recargar o
reabrir la app sin conexión sigue mostrando «Sesión no disponible»; lo atiende
el PR de `DRV-SESSION-EXPIRY-OFFLINE-2026-10-19`.

Los paneles «Sesión no disponible» y «Sesión no válida» ofrecen «Iniciar
sesión» (la app instalada no tiene barra de direcciones): va a `/login` con la
ruta de paradas actual como `return_url`, validada por `buildSignInPromptHref`
(sólo una ruta local distinta de `/login`) y otra vez por `/login` y por
`/auth/login`. Aparece cuando terminó la consulta de sesión de la página, nunca
mientras sigue en curso; sin conexión el botón queda deshabilitado con
«Conéctate a internet para iniciar sesión.», como «Cerrar sesión».

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
dentro de los stops actuales, la navegación dentro de la app (sólo un clic
simple, sólo rutas canónicas, un `pushState`, el aviso a la pantalla y el scroll
arriba), el `return_url` de «Iniciar sesión» y que ambas páginas usan el mismo
shell sin prop server-side.

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

`DriverStopsBffPwaPlaywrightTests` (misma categoría) corre el artefacto en modo
BFF, como la imagen del piloto: `DriverStopsNextServer.StartProductionBffAsync`
compila con `NEXT_PUBLIC_AUTH_MODE=bff` y sin `NEXT_PUBLIC_API_BASE_URL` (el
marcador de compilación lo distingue de la compilación Mock, así que cada una
reemplaza a la otra) y `DriverStopsBffNextServerFixture` lo sirve por
`LoopbackTlsTunnel`, porque el código productivo del repartidor sólo acepta un
mismo origen HTTPS. El navegador responde `/auth/session`, `/me/organization-contexts`
y la API en el mismo origen (`DriverBffSession`), igual que las suites Mock
responden la API; la mitad servidor del contrato (cookie, CSRF, OIDC con
`FakeAuthCenterServer`) la cubren las suites de AUTH-001, que corren en proceso
sin navegador. Prueba lista → detalle → lista, atrás y adelante sin conexión
sin ninguna carga de documento ni lectura de sesión (con el snapshot guardado a
la vista), y «Iniciar sesión» con su
`return_url` hasta `/auth/login`, deshabilitado sin conexión.

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

Las suites que usan el runtime de desarrollo (`DriverStopsNextServer.StartAsync`:
el caso de DriverHub real y `OperationsDashboardPwa`) arrancan `next dev`, que
reescribe el archivo versionado `apps/web/next-env.d.ts` para importar
`./.next/dev/types/...` en lugar de `./.next/types/...`. Next 16.3 no tiene opción
para evitarlo: escribe el archivo en cada arranque de desarrollo y su `distDir`
de desarrollo es siempre `.next/dev`. Por eso `DriverStopsNextServer` guarda los
bytes exactos del archivo bajo el lease entre procesos y los restaura cuando su
proceso Next se detiene, también si el arranque falla; correr estas categorías
deja `git status` limpio. Copiar la app a otro directorio también lo evitaría,
pero obliga a duplicar `node_modules` de pnpm por corrida. Solo un proceso de
prueba terminado a la fuerza (sin dispose) deja el cambio; se recupera con
`git checkout -- apps/web/next-env.d.ts`.

Puertos. Una suite que debe conocer un puerto antes de que su servidor escuche
(el origen web que la API y el bucket permiten, el origen de la API que la
compilación web incrusta, el puerto en el que la API se reinicia) lo arrienda con
`LoopbackPortLease`: en Linux, un socket enlazado a `127.0.0.1:puerto` con
`SO_REUSEADDR` que nunca escucha. El kernel no da ese puerto a ningún socket que
pida uno cualquiera (ni a un `bind` al puerto 0 ni como puerto local de un
`connect`), y Next.js y Kestrel, que enlazan con `SO_REUSEADDR`, sí escuchan en
él. El arriendo dura mientras el servidor corre y entre sus reinicios, y se suelta
al terminar. También lo arriendan las pruebas de reconexión de tiempo real
(`RealtimeReconnectKestrelTests` y `RealtimeOutboxPlaywrightTests`), cuyo host
se reinicia en el puerto en el que arrancó.
Antes se reservaba escuchando en el puerto 0 y cerrando, y el puerto
quedaba libre mientras duraban la compilación, los contenedores o el arranque (de
segundos a minutos) y entre los reinicios de la API: así fallaron
`Validate public tracking` en #307 (`EADDRINUSE` de Next) y `Validate driver PWA`
en la corrida 39443348695 (la API de OPS-005). `LoopbackPortLeaseTests` lo prueba
con sockets confinados a ese único puerto (`IP_LOCAL_PORT_RANGE`, Linux 6.3 o
posterior). En Windows y macOS el arriendo se suelta justo antes de que el servidor
enlace: la ventana se reduce a su arranque, pero no se cierra.

Trazas. Las suites BFF (`DriverStopsBffPwaPlaywrightTests` y
`DriverSessionExpiryBffPwaPlaywrightTests`) graban una traza de Playwright
(capturas, snapshots del DOM y red) y sólo la guardan si la prueba falla, en
`TestResults/driver-stops-pwa/playwright-traces/<clase>.<prueba>.zip` bajo la raíz
del repositorio; el job `Validate driver PWA` sube ese directorio en el artefacto
`driver-stops-pwa-results` cuando falla. Se abre con
`pwsh tests/Paqueteria.IntegrationTests/bin/Release/net10.0/playwright.ps1 show-trace <zip>`.
Todo lo que esas páginas muestran o intercambian es sintético: el navegador
responde la sesión, su token CSRF de prueba y la API, no se fija ninguna cookie y
la traza no guarda fuentes.

El job aislado `Validate driver stops PWA` instala el lockfile congelado y
Chromium, y ejecuta toda la categoría. El aislamiento evita mezclar el proceso
Next y sus recursos Docker con el job .NET general.

## Límites y riesgos

- La sesión productiva depende todavía del adaptador de identidad aprobado.
- La caché contiene `address_summary` (y, desde
  `UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11`, la dirección completa y el nombre
  corto); partición y limpieza son críticas.
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

### Rollback de DRV-PWA-INAPP-NAVIGATION

Revertir el commit de `fix/driver-pwa-inapp-navigation`: “Ver detalle” y
“Volver a mis paradas” vuelven a ser cargas de documento y los paneles de sesión
pierden «Iniciar sesión». No toca el Service Worker (su cache sigue en
`paquetenvia-driver-shell-v8`), IndexedDB, la cola offline, la API ni datos; no
hay migración. Las suites BFF se van con el mismo revert.
