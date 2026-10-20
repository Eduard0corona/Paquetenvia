# DRV-002: cola offline de eventos y POD

## Alcance

DRV-002 extiende la PWA de repartidor de DRV-001 con una cola durable para las
cinco acciones operativas autorizadas:

| Operación local | Estado origen | Estado destino | Motivo |
| --- | --- | --- | --- |
| `CHECK_IN` | `ASSIGNED` | `AT_PICKUP` | `DRIVER_CHECK_IN` |
| `PICKUP_PROOF` | `AT_PICKUP` | `PICKED_UP` | `DRIVER_PICKUP_CONFIRMED` |
| `START_TRANSIT` | `PICKED_UP` | `IN_TRANSIT` | `DRIVER_TRANSIT_STARTED` |
| `START_DELIVERY` | `IN_TRANSIT` | `DELIVERING` | `DRIVER_DELIVERY_STARTED` |
| `DELIVERY_PROOF` | `DELIVERING` | `DELIVERED` | `DRIVER_DELIVERY_CONFIRMED` |
| `COD_COLLECTION` | `DELIVERING` | (sin transición) | `recordCodCollection`, UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11 |
| `FAILED_DELIVERY` | `DELIVERING` | `FAILED_ATTEMPT` | `DRIVER_DELIVERY_FAILED_REPORTED`, con prueba, incidencia y `metadata.incident_id` (UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12) |

Los únicos proof types son `PICKUP_PHOTO` y `DELIVERY_PHOTO`. La foto del lugar
de "No se pudo entregar" es una `DELIVERY_PHOTO`; deja de contar para
`DELIVERED` solo cuando la incidencia la cita como evidencia. Si el reporte se
detiene después de finalizar la foto y antes de abrir la incidencia, la foto
queda sin incidencia y el servidor podría contarla para `DELIVERED` en ese
intento: riesgo residual con pendiente de servidor (respuesta del owner
"Después, con ADR (Recommended)", ver
`ui-001-phase3-driver-failed-delivery.md`). No se agregan
endpoints, migraciones, paquetes, dependencias ni cambios productivos al
backend. La implementación consume los contratos existentes de ORD-002,
POD-001, FIN-001 e INC-001.

Quedan fuera de alcance ubicación, devolución, firma, código de entrega, un
intento fallido en recolección, reintentos desde el Service Worker, Background
Sync y cualquier transición distinta de la tabla. Navegación, contacto y cobro
llegaron con UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11; la incidencia y
`FAILED_ATTEMPT` de una entrega, solo como "No se pudo entregar"
(`FAILED_DELIVERY`), con UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12 (ver
`ui-001-phase3-driver-failed-delivery.md`). Ninguna otra salida de
`FAILED_ATTEMPT` se ofrece en el teléfono: despacho decide.

## Arquitectura

La frontera `apps/web/src/driver/` agrega:

- `offline/operation-contract.ts`: contrato cerrado, parser runtime, motivos e
  idempotency keys;
- `offline/operation-projection.ts`: proyección pura sobre estado confirmado;
- `offline/driver-offline-queue.ts`: repositorio IndexedDB particionado,
  transacciones atómicas, blobs y lease;
- `offline/driver-sync-api.ts`: cliente exacto de transiciones, sesiones,
  upload directo y finalización;
- `offline/driver-sync-scheduler.ts`: orden, reintento, confirmación REST y
  resolución segura de errores;
- `proofs/proof-file.ts`: validación de tipo, tamaño y SHA-256;
- `state/driver-operations-controller.ts`: coordinación con la UI.

Los componentes no abren IndexedDB ni construyen requests. REST continúa siendo
la autoridad: la proyección local sólo explica qué ocurrirá cuando se
sincronice.

## IndexedDB v2 y partición

Actualización `UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11`: la base sube a la
versión `3`, el snapshot al esquema `2` y las operaciones al esquema `2` (agregan
`cod` y `rejectionCode`, y el tipo `COD_COLLECTION`); las operaciones en cola se
migran sin perderse. Ver `ui-001-phase3-driver-stop-details.md`.

Actualización `UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12`: la base sube a la
versión `4` y las operaciones al esquema `3` (agregan `failedDelivery` y el tipo
`FAILED_DELIVERY`, con la llave `drv2-{operationId}-incident`); las operaciones
en cola de esquema 1 o 2 se migran en el mismo lugar y las instantáneas de
esquema 2 se conservan. Una app de una versión anterior no puede abrir la base.
El shell del Service Worker sube a `paquetenvia-driver-shell-v7`. Descartar una
operación pausa la sincronización. Para todas las acciones: una acción que el
servidor ya aceptó (`AWAITING_REST_CONFIRMATION`) nunca vuelve a quedar
bloqueada ni pendiente de mandar, y si REST nunca la muestra sale de la cola a
las 72 horas sin aviso; al descartar una acción en una parada que despacho movió
(sin estado operativo), la primera que queda de la orden pasa a atención, y una
acción bloqueada sin nada retenido antes se muestra como retenida, así que
siempre se puede descartar; reintentar la misma acción libera las que quedaron
bloqueadas detrás. Cada conexión a la base se cierra sola cuando una versión más
nueva la pide; si otra pestaña bloquea la subida, la pantalla pide cerrarla y
recargar, y la conexión tardía se cierra. Ver
`ui-001-phase3-driver-failed-delivery.md`.

Actualización `DRV-DISPATCH-CONTACT-2026-10-12`: la base sube a la versión `5` solo para crear el store
`dispatch_contacts` (la última respuesta de `getMyDispatchContact` por partición: el número de despacho o null). La
versión 4 ya había llegado a main, así que el store nuevo necesita su propia versión. La subida desde la 4 no toca
operaciones (siguen en esquema 3), fotos, leases, instantáneas ni roles guardados; una app de la versión 4 no puede
abrir la base. El shell del Service Worker sube a `paquetenvia-driver-shell-v8`. El store sale del teléfono con la
partición ante 403 (un 401 lo conserva desde `DRV-SESSION-EXPIRY-OFFLINE-2026-10-19`) y al cerrar sesión, como las
instantáneas. Ver `ui-001-phase3-driver-dispatch-contact.md`.

La base conserva el nombre `paquetenvia-driver-stops-v1` y sube su versión de
base de datos a `2`. El snapshot DRV-001 conserva `schemaVersion: 1`. La
actualización agrega:

- `operations`, indexado por partición, orden, estado, próximo intento y fecha;
- `proof_blobs`, enlazado por partición y operación;
- `sync_leases`, con un propietario y expiración por partición.

Cada operación validada contiene schema, partition key, UUID local, UUID de
orden, kind, estados origen/destino, versión esperada, motivo, timestamp UTC
del cliente, estado de sync, contadores, referencias no secretas de la sesión
de carga, las tres idempotency keys, metadata cerrada del proof y error seguro.
El parser runtime rechaza propiedades desconocidas, schema no reconocido,
UUID no canónico, timestamps no UTC y cualquier combinación parcial o
incompatible de operación/metadata.

La partition key sigue siendo un SHA-256 base64url del namespace opaco y la
organización activa. Todas las lecturas y escrituras usan esa key; no se
enumeran otras particiones. Un lease evita dos schedulers activos para la misma
partición; se renueva con heartbeat cancelable mientras existe trabajo activo y
una lease expirada puede recuperarse sin crear un lock global. Un sync pedido
mientras otra página tiene la lease vigente (por ejemplo, una página que se fue
a mitad de un pase) no se descarta: se vuelve a pedir tras una duración de
lease (ver `driver-offline-replay-flake.md`).

No se persisten bearer tokens, signed URLs, headers firmados, object keys,
teléfono, coordenadas ni respuestas técnicas. Las URLs y headers firmados sólo
existen en memoria durante el upload.

## Enqueue, archivos y límites

La operación sin proof se escribe en una transacción. Una operación con proof
escribe operación, metadatos seguros y `Blob` en la misma transacción; si una
parte falla no queda una operación huérfana. La UI deshabilita temporalmente la
acción para evitar doble click y además deduplica por orden, kind y versión.

Antes de persistir una foto se valida:

- MIME exacto `image/jpeg` o `image/png`;
- máximo técnico actual de 10 MiB por archivo;
- SHA-256 calculado con Web Crypto;
- máximo 100 operaciones por partición;
- máximo agregado de 25 MiB de proofs por partición;
- estimación de almacenamiento, cuando el navegador la ofrece.

El límite de 10 MiB está alineado con el default técnico actual de Custody; no
es una política legal inmutable. IndexedDB puede ser purgado por el navegador,
por lo que la UI nunca presenta una acción proyectada como confirmada.

Vista previa (UI-001 fase 3): al tomar la foto la PWA la muestra en “Vista
previa de la foto” desde un object URL en memoria, sin persistir nada nuevo;
sólo se previsualiza una foto JPEG/PNG de hasta 10 MiB (mismas reglas, sin leer
ni hashear el archivo). “Repetir” la descarta y reabre la cámara; sólo “Usar
esta foto” encola la acción con ese mismo `Blob`, con la validación, el SHA-256,
la escritura IndexedDB y la idempotencia de siempre. Si la cola no la guarda,
la misma foto vuelve a la vista previa. El object URL se revoca al repetir, al
usarla y al desmontar el paso.

## Proyección local

La proyección comienza en `status` y `aggregate_version` confirmados por REST.
Aplica en FIFO únicamente operaciones válidas cuya fuente y versión coinciden
con el siguiente estado esperado. Muestra por separado “Estado confirmado” y
“Proyección pendiente”.

Una transición que REST ya muestra aplicada (su estado destino en una versión
posterior) y que no está retenida para el repartidor no se proyecta ni se
muestra como conflicto: el scheduler la elimina sin reenviarla, y la pantalla
ya no la presenta en atención en el instante entre la lectura de confirmación y
esa eliminación, que reemplazaba el paso siguiente y perdía la foto en vista
previa (ver `driver-photo-selection-refresh.md`).

Una operación `NEEDS_ATTENTION` no se proyecta. Las operaciones posteriores de
esa orden quedan `BLOCKED`; otras órdenes continúan sincronizando. El usuario
puede, con confirmación visible:

- descartar la operación y su blob;
- reintentar la misma operación sólo si estado y versión confirmados no
  cambiaron;
- crear una nueva operación para la versión actual cuando el arco todavía es
  válido;
- crear una nueva sesión de upload cuando la anterior expiró.

## Idempotencia y timestamps

El UUID de operación y `clientOccurredAt` se generan antes de la escritura
IndexedDB y permanecen estables en todos los reintentos. El timestamp debe ser
UTC; el parser de respuestas acepta `Z` o `+00:00` y normaliza a `Z`.

Las keys son deterministas y no contienen PII:

```text
drv2-{operationId}-transition
drv2-{operationId}-session-{sessionAttempt}
drv2-{operationId}-finalize-{sessionAttempt}
```

El timestamp del cliente viaja como `client_occurred_at` en la transición y en
la creación de la sesión de carga, y como `captured_at` al finalizar POD
(AI-05 `x-offline-operation-age`, decisión OPS-003-PWA-CLIENT-OCCURRED-AT). Es
el mismo instante de captura en cada reintento y después de recargar; no altera
la fecha autoritativa del backend, que sólo lo usa para la regla de 72 h.

## Sincronización

El scheduler procesa FIFO por orden, una operación por vez. Pausa reintentos
automáticos cuando la pestaña está oculta o el navegador está offline; escucha
`online`, permite “Sincronizar ahora” y usa backoff de 1, 2, 5, 10 y 30
segundos. Nunca mantiene una transacción IndexedDB abierta durante red.

Antes de reenviar, y después de una respuesta exitosa, consulta
`GET /api/v1/driver/me/stops`. Si REST ya confirma el estado y la versión
esperados, elimina la operación sin repetir la mutación. Esto cubre respuestas
perdidas y replay después de recarga. Una operación que ya recibió transición
200 queda en `AWAITING_REST_CONFIRMATION`: sólo refresca REST con backoff y no
reenvía la transición. SignalR únicamente dispara el mismo refresh; nunca
confirma ni muta la cola por sí solo.

Una transición envía exactamente:

```json
{
  "target_status": "<destino>",
  "reason": "<motivo DRIVER_*>",
  "expected_version": 1,
  "metadata": {},
  "client_occurred_at": "<instante de captura UTC>"
}
```

Para proof:

1. crea o reproduce la sesión con la misma key;
2. valida el grant y mantiene signed URL/headers sólo en memoria;
3. hace `PUT` directo con exclusivamente `required_headers`, credenciales
   omitidas, sin caché y sin referrer;
4. finaliza con `upload_session_id`, proof type, timestamp capturado y SHA-256;
5. espera validación cuando Custody responde `UPLOAD_SESSION_NOT_READY` o
   `PROOF_OBJECT_NOT_READY`;
6. elimina el blob local al recibir un proof válido;
7. ejecuta la transición y espera confirmación REST.

Una sesión expirada queda en atención. “Crear nueva sesión” incrementa
`sessionAttempt`, conserva el UUID de la operación, el blob y el timestamp, y
produce keys nuevas para sesión/finalización.

Si se pierde la respuesta del `PUT`, la sesión se reproduce con la misma key y
se vuelven a enviar exactamente el mismo Blob y headers del grant. Si se pierde
el 201 de finalización, se reproduce la misma key de finalize y el backend
devuelve el mismo proof. En ambos casos el Blob se conserva hasta que existe un
receipt válido; la prueba de duplicate upload demuestra un solo proof y una
sola transición funcional.

## Errores, revocación y privacidad

| Resultado | Tratamiento |
| --- | --- |
| red, timeout o 5xx | `RETRY_WAIT` con backoff |
| 401 (`session-ended`) | desde `DRV-SESSION-EXPIRY-OFFLINE-2026-10-19` no borra nada: la cola deja de enviar con esa sesión y la pantalla pide volver a entrar (ver abajo) |
| 403 | limpia snapshot, operaciones, blobs y lease de la partición activa |
| sin credencial usable (`unauthorized`: sin token CSRF, nada se envió) | mismo cierre fail-closed que 403 |
| 404 | elimina operación/blob, refresca REST y no consulta otra partición |
| 409 de transición | `NEEDS_ATTENTION`; bloquea las posteriores de esa orden |
| 409 POD “not ready” | `WAITING_VALIDATION` y reintento |
| otro 409 POD o respuesta inválida | atención fail-closed |
| 409 `OFFLINE_OPERATION_EXPIRED` (o captura con más de 72 h) | elimina operación y blob, nunca reintenta y avisa al conductor (OPS-003) |

La limpieza nunca toca otra organización ni otro IndexedDB. Offline no permite
detectar una revocación nueva; al siguiente 403 conocido se elimina el material
local activo.

## Sesión vencida y recarga sin señal (DRV-SESSION-EXPIRY-OFFLINE-2026-10-19)

Respuestas del owner (2026-10-19, literales en `decision-log.md`): E2 «Conservar y pedir entrar (Recommended)» y E3
«Sí, y envía al validar (Recommended)». Se apoya en el PR 1 (`fix/driver-pwa-inapp-navigation`), que hizo que «Ver
detalle» y «Volver a mis paradas» no recarguen la página y agregó «Iniciar sesión» a los paneles de sesión.

### 401: conservar y pedir entrar (E2)

- Los clientes del repartidor distinguen el 401 (`session-ended`: la sesión BFF terminó, por sus 480 minutos fijos
  sin renovación, porque AuthCenter la cerró o porque otro inicio de sesión la reemplazó) de la falta de credencial
  (`unauthorized`: sin token CSRF, nada se envió) y del 403 (`forbidden`): `driver-stops-api`,
  `organization-roles-api`, `dispatch-contact-api` y `driver-sync-api`.
- `DriverSyncScheduler` ante `session-ended` no llama `clearPartition`: la instantánea, cada operación
  (DELIVERY_PROOF, COD_COLLECTION, FAILED_DELIVERY y las demás), cada blob y su avance se quedan; la operación en curso
  queda como se persistió por última vez, con sus llaves; el pase se aborta, se libera el lease y ese scheduler ya no
  envía nada (ni `online`, ni visibilidad, ni temporizadores). `DriverStopsController.revokeAccess("session-ended")`
  detiene lecturas y realtime sin borrar la instantánea; los controladores de roles y de teléfono de despacho conservan
  lo guardado y no vuelven a preguntar.
- La pantalla muestra «Tu sesión venció, entra de nuevo» con «Iniciar sesión» (`/login?return_url=<ruta de
  paradas>`). También lo muestra cuando la consulta de sesión de la página no encuentra sesión (401) y el teléfono
  recuerda a un repartidor.
- La llave de partición es SHA-256 de `sessionNamespace` (que la API deriva de SHA-256 del `sub` de AuthCenter) y de la
  organización, así que el mismo repartidor en la misma empresa reabre la misma partición al volver a entrar y su cola
  se envía sola con las mismas llaves de idempotencia. Otra persona, o la misma en otra empresa, abre su propia
  partición; la anterior queda intacta e invisible.
- Sin cambio: 403 (membresía o perfil de repartidor revocados) y la falta de credencial siguen borrando la partición.
  El código muestra que un vencimiento nunca llega así: la API responde 401 a una sesión vencida, cerrada o
  reemplazada (sin ticket, o `AuthCenterCookieEvents.ValidatePrincipal` rechaza el principal; `TenantContextMiddleware`
  deja pasar las peticiones anónimas y la autorización responde 401), 403 sólo a una persona autenticada sin acceso, y
  el token CSRF vive en la memoria de la página toda su vida.
- La regla de 72 horas (OPS-003-OFFLINE-72H) sigue contando desde `clientOccurredAt` mientras la acción espera el nuevo
  inicio de sesión: una acción más vieja se descarta, con su aviso, en el primer pase después de entrar.

### Recarga sin señal (E3)

- Marca (`src/driver/session/driver-session-marker.ts`): `localStorage["paquetenvia:driver-session-marker"]` con
  `{ v: 1, sessionNamespace, organizationId }`, exactamente lo que forma la llave de partición. Nunca token, cookie,
  token CSRF, contraseña, nombre, correo ni teléfono; nunca se registra en logs. Se valida estrictamente al leerla
  (miembros exactos, patrón del namespace, UUID canónico) y un valor distinto se borra.
- Por qué localStorage y no IndexedDB: un store nuevo obligaría a la versión 6 de la base del repartidor, cuya subida
  espera a que se cierre cualquier otra pestaña y que una build anterior ya no puede abrir (un rollback dejaría la cola
  ilegible); localStorage no tiene esquema, una build anterior ignora la llave y se lee al iniciar la página.
  Seguridad: AI-08 AUTH-001 prohíbe tokens en almacenamiento del navegador y la marca no lleva ninguno; AI-13 deja toda
  regla crítica en el servidor y la marca no autoriza nada, sólo elige qué partición local abrir mientras el servidor
  no responde. Ambos almacenamientos son del mismo origen; ninguno protege más contra un script del origen (la defensa
  sigue siendo la CSP con nonce).
- Ciclo: la escribe cada sesión de repartidor confirmada (`installBffSession`); la reemplaza otra persona u otra
  empresa; la borran un rol que no es de repartidor, no tener organización, «Cerrar sesión» cuando el servidor cerró
  la sesión (`endBffSession`), un 403, la falta de credencial y una identidad que el servidor no autoriza; un 401 y un
  servidor inalcanzable la conservan.
- En `/driver/...`, si `GET /auth/session` no responde (sin señal, 5xx o respuesta ilegible), o no se pueden listar las
  organizaciones de la misma persona, `bootstrapBffSession(..., { rememberDriver: true })` instala una sesión
  `remembered` sin token CSRF. La pantalla abre la partición («Sesión sin confirmar»): paradas y cola desde IndexedDB y
  acciones nuevas a la cola. Con esa sesión ningún cliente sale a la red (stops, roles, despacho, sync, ofertas,
  llamadas; el hub no se abre), el scheduler no arranca (`sync: false`), no hay ofertas externas, ni «Cuenta», y «Cerrar
  sesión» espera.
- `watchDriverSessionConfirmation` vuelve a consultar la sesión en `online`, al mostrarse la app, con «Sincronizar
  ahora» o «Reintentar» y cada 30 s mientras espera con señal. Sólo `GET /auth/session` del mismo `sessionNamespace` con
  un contexto de la misma organización la reemplaza por la sesión confirmada, cuyo scheduler envía la cola. Otra persona
  u organización instala su propia sesión y partición y reemplaza la marca; un 401 pide volver a entrar.

### Pruebas

Vitest: clientes (401 → `session-ended`, sin CSRF → `unauthorized` sin enviar nada, sesión recordada sin `fetch`),
scheduler (401 conserva instantánea, operaciones, fotos y lease liberado y no envía más; un 401 de la lectura de
confirmación también; el mismo repartidor reanuda con las mismas llaves; 403 y sin credencial siguen borrando),
controladores de paradas, roles, despacho y operaciones, la marca (sólo namespace y organización, ningún secreto,
valores inválidos descartados, almacenamiento bloqueado), el bootstrap (recarga sin señal, misma persona, otra
persona, otra empresa, rol de operaciones, 401, no autorizado, sin organizaciones, logout) y el vigilante de
confirmación.

Playwright en modo BFF (`DriverSessionExpiryBffPwaPlaywrightTests`, colección BFF de
`docs/development/drv-001-driver-stops-pwa.md`, categoría `DriverOfflineOperationsPwa`): la sesión vence con una
acción en cola, la pantalla pide entrar, «Iniciar sesión» pasa por `/login` y `/auth/login` (que el navegador
responde como AuthCenter) y vuelve a la parada; el mismo repartidor envía la acción con la misma llave y su token
CSRF, y otra persona no ve ni envía nada y la cola queda intacta. Una recarga sin señal muestra las paradas guardadas
(«Sesión sin confirmar»), guarda una acción sin ninguna petición a la API ni al hub, y al volver la señal sólo el
mismo repartidor la envía; con otra persona nada se envía y la marca cambia. Un 403 sigue borrando la partición y
olvida al repartidor.

### Rollback

Revertir el PR. Las páginas vuelven a borrar la partición ante un 401 y a mostrar «Sesión no disponible» al recargar
sin señal; la llave `paquetenvia:driver-session-marker` queda ignorada (se puede borrar sin riesgo: no guarda datos de
la cola). No hay cambio de base IndexedDB, Service Worker, API, SQL ni paquetes, así que la cola sigue legible para
la build anterior. Para revertir sólo la parte normativa, revertir también la fila del decision-log, la entrada del
CHANGELOG y AI-07 y regenerar la integridad.

La telemetría está deshabilitada por defecto. Su puerto sólo admite kind,
fase, categorías de resultado, buckets de cantidad `0`, `1`, `2-5`, `6-20`,
`21+` y buckets de tamaño `0-256KB`, `256KB-1MB`, `1-5MB`, `5-10MB`. No
admite UUID, public ID, dirección, timestamps, SHA-256, MIME, tamaños exactos,
signed URLs, object keys, tokens, headers ni bodies, y no agrega proveedor
externo.

## Service Worker

El shell sube a `paquetenvia-driver-shell-v3`. Continúan network-only:

- `/api/v1/**`, `/hubs/**` y autenticación;
- requests con `Authorization`;
- URLs firmadas, S3/MinIO y proof objects;
- todo request cross-origin.

El Service Worker no abre IndexedDB, no procesa mutaciones, no guarda responses
POD y no registra Background Sync. La página controlada es la única dueña del
scheduler.

## DRV-002-DEF-001: CORS real para el upload firmado

El pipeline de aceptación anterior lanzaba Chromium con
`--disable-web-security`. Aunque demostraba la lógica de la cola, Custody y el
almacenamiento, no demostraba que un navegador con same-origin policy pudiera
ejercer el grant firmado. Un `PUT` cross-origin con `Content-Type` y metadata
firmada `x-amz-meta-*` requiere un preflight CORS real.

MinIO Community devolvió `NotImplemented` al intentar configurar CORS por
bucket mediante `PutBucketCors`. Ese resultado no se ocultó ni se sustituyó por
una simulación de `OPTIONS`, un fallback global o CORS abierto. MinIO continúa
siendo el backend local y de pruebas de POD-001, `SecureProofUpload`, Custody y
el Compose existente.

DRV-002 usa exclusivamente en su harness CORS un Ceph RADOS Gateway separado,
fijado por versión y digest. La prueba de capacidad automatizada crea un bucket
privado y comprueba `PutBucketCors`, lectura exacta, `DeleteBucketCors`, URL
firmada generada con AWSSDK.S3, `PUT` directo, lectura posterior y conservación
exacta de toda la metadata. No se adquirió MinIO AIStor ni otra dependencia
comercial, y Ceph RGW no se declara como proveedor productivo elegido.

La política temporal del bucket contiene únicamente:

- el origen Next exacto, con esquema y puerto;
- método `PUT`;
- headers `content-type`, `x-amz-meta-session-id`,
  `x-amz-meta-order-id`, `x-amz-meta-owner-org-id`,
  `x-amz-meta-requested-by`, `x-amz-meta-proof-type`,
  `x-amz-meta-size-bytes` y `x-amz-meta-sha256`;
- `MaxAgeSeconds` de 300.

No contiene wildcard, credenciales, `GET`, `POST`, `DELETE`, lectura pública ni
ACL pública. El fixture vuelve a leer y compara la regla exacta antes de abrir
Chromium. Al terminar elimina CORS, objetos, bucket y contenedor, y restaura las
variables AWS previas aun si el escenario falla.

Chromium se lanza únicamente con `Headless = true`. La aceptación observa por
CDP metadatos seguros del `OPTIONS` y del `PUT` reales, sin registrar la query
firmada, object key, headers, token, hash ni bytes, y no intercepta ni simula
las solicitudes. Una aserción estructural impide argumentos que deshabiliten
CORS, same-origin policy, site isolation o mixed-content protection en los
escenarios Driver.

La URL firmada concede autorización sobre un objeto; CORS determina si un
navegador de un origen concreto puede ejercer esa autorización. Una firma
válida no reemplaza CORS. En producción, la infraestructura externa debe
configurar en su proveedor real cada origen PWA aprobado de forma exacta,
únicamente `PUT` y los headers firmados necesarios, sin wildcard. DRV-002 no
configura automáticamente infraestructura ni almacenamiento productivos.
GATE-007 continúa bloqueando evidencia real de producción.

## Pruebas y CI

Vitest cubre contrato/runtime parser, idempotencia y timestamps, proyección,
archivo/hash, contrato API, upload seguro, scheduler, backoff, confirmación REST,
política del Service Worker, schema IndexedDB y OpenAPI heredado.

La categoría `DriverOfflineOperationsPwa` ejecuta Chromium real y cubre:

- upgrade real de IndexedDB 1→2 que conserva el snapshot DRV-001 y crea los
  tres stores nuevos;
- las cinco acciones y dos blobs completamente offline;
- recarga offline y reconstrucción de la proyección;
- sincronización FIFO;
- aislamiento entre organizaciones y sesión nueva;
- conflicto visible, descarte y reconstrucción explícita;
- limpieza fail-closed para 403 y 404; un 401 conserva instantánea, operación y foto, y el mismo repartidor las
  envía al volver a entrar (`DRV-SESSION-EXPIRY-OFFLINE-2026-10-19`);
- respuesta perdida después de PUT, Proof 201 y Transition 200, con las mismas
  keys y sin duplicados;
- un pipeline real con PostgreSQL/PostGIS, API Kestrel, Next, Ceph RGW aislado
  para CORS, Worker, DriverHub y Chromium con seguridad normal que observa
  preflight y upload 2xx, y termina en `DELIVERED`, dos proofs, dos sesiones
  consumidas, cinco eventos, nueve registros idempotentes y cero duplicados.

- una operación que el servidor rechaza con `OFFLINE_OPERATION_EXPIRED` se
  elimina una sola vez, conserva su instante de captura tras recargar y muestra
  el aviso al conductor.

Son doce escenarios de navegador en la categoría. El harness serializa el
servidor Next entre procesos de test y libera el lock al terminar; esto no
modifica comportamiento productivo.

`DriverStopsPwa`, `SecureProofUpload`, `OutboxSignalRDelivery` y
`PostgreSqlContract` permanecen como regresiones aisladas. El job existente
conserva su ID y se presenta como `Validate driver PWA`; instala el lockfile y
Chromium y ejecuta juntas las categorías DRV-001 y DRV-002.

Ejecución local:

```powershell
pnpm --dir apps/web install --frozen-lockfile
pnpm --dir apps/web lint
pnpm --dir apps/web typecheck
pnpm --dir apps/web test
pnpm --dir apps/web build
dotnet build .\tests\Paqueteria.IntegrationTests\Paqueteria.IntegrationTests.csproj
pwsh .\tests\Paqueteria.IntegrationTests\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet test .\tests\Paqueteria.IntegrationTests\Paqueteria.IntegrationTests.csproj --filter "Category=DriverOfflineOperationsPwa"
```

## Riesgos y gaps heredados

- IndexedDB y Cache API son almacenamiento best-effort del navegador.
- Los blobs pueden contener información visual sensible y las cuotas disponibles
  varían entre dispositivos.
- Una interrupción entre pasos conserva suficiente estado para reanudar, pero
  requiere conectividad y REST autoritativo.
- El adaptador productivo de identidad sigue fuera de este ticket.
- Offline no puede comprobar revocaciones nuevas y no existe Background Sync
  autenticado seguro; la sincronización requiere una página activa.
- Los grants expiran y una respuesta puede perderse después de que el servidor
  confirmó el paso.
- Un proof puede quedar confirmado aunque su transición posterior termine en
  conflicto; REST sigue siendo autoridad y SignalR sólo solicita un refresh.
- Los 409/503 de POD conservan el drift normativo heredado; DRV-002 no cambia
  OpenAPI ni backend para resolverlo.
- El cobro en la puerta (UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11) y "No se pudo
  entregar" (UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12) ya existen; no hay
  intento fallido en recolección ni otro flujo de incidencias en el teléfono.
- GATE-007 bloquea el uso productivo de evidencia real.
- Las vulnerabilidades de paquetes heredadas no se corrigen porque el alcance
  prohíbe cambios de versiones, lockfile u overrides.
- Issue #5 y GATE-007, GATE-010, GATE-013 y GATE-014 permanecen abiertos.

## Rollback

1. Deshabilitar la entrada hacia las acciones operativas del driver.
2. Detener el coordinador offline y desplegar el shell DRV-001 anterior.
3. Incrementar el cache name del shell de rollback y eliminar únicamente
   `paquetenvia-driver-shell-v3` cuando el nuevo shell ya controle la página.
4. Conservar las operaciones locales hasta que el usuario confirme su
   eliminación o una exportación técnica estrictamente sintética.
5. Para rollback de seguridad, limpiar únicamente operaciones, Blobs y lease de
   la partición activa.
6. Revertir, en orden inverso, los tres commits de DRV-002.
7. Conservar el store `snapshots` y sus registros DRV-001 compatibles.
8. Si es indispensable retirar schema v2, hacerlo mediante un upgrade
   controlado que elimine sólo `operations`, `proof_blobs` y `sync_leases`; no
   borrar la base `paquetenvia-driver-stops-v1` completa.
9. No enumerar ni eliminar otros IndexedDB, caches, particiones u
   organizaciones.
10. No ejecutar DDL ni modificar PostgreSQL, órdenes, proofs ya finalizados,
    sesiones, objetos confirmados, transiciones o idempotency rows del backend.
11. Mantener DRV-001, DSP-002, ORD-002, POD-001 y RTM-002 activos.

El rollback del frontend no requiere migraciones ni cambios productivos al
backend.
