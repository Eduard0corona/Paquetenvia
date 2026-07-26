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

Los únicos proof types son `PICKUP_PHOTO` y `DELIVERY_PHOTO`. No se agregan
endpoints, migraciones, paquetes, dependencias ni cambios productivos al
backend. La implementación consume los contratos existentes de ORD-002 y
POD-001.

Quedan fuera de alcance ubicación, navegación, contacto, incidentes,
`FAILED_ATTEMPT`, devolución, firma, código de entrega, reintentos desde el
Service Worker, Background Sync y cualquier transición distinta de la tabla.

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
una lease expirada puede recuperarse sin crear un lock global.

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

## Proyección local

La proyección comienza en `status` y `aggregate_version` confirmados por REST.
Aplica en FIFO únicamente operaciones válidas cuya fuente y versión coinciden
con el siguiente estado esperado. Muestra por separado “Estado confirmado” y
“Proyección pendiente”.

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

El timestamp del cliente sólo se envía como `captured_at` al finalizar POD. No
se agrega al body de transición ni altera la fecha autoritativa del backend.

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
  "metadata": {}
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
| 401 | limpia snapshot, operaciones, blobs y lease de la partición activa |
| 403 | mismo cierre fail-closed que 401 |
| 404 | elimina operación/blob, refresca REST y no consulta otra partición |
| 409 de transición | `NEEDS_ATTENTION`; bloquea las posteriores de esa orden |
| 409 POD “not ready” | `WAITING_VALIDATION` y reintento |
| otro 409 POD o respuesta inválida | atención fail-closed |

La limpieza nunca toca otra organización ni otro IndexedDB. Offline no permite
detectar una revocación nueva; al siguiente 401/403 conocido se elimina el
material local activo.

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
- limpieza fail-closed para 401, 403 y 404;
- respuesta perdida después de PUT, Proof 201 y Transition 200, con las mismas
  keys y sin duplicados;
- un pipeline real con PostgreSQL/PostGIS, API Kestrel, Next, MinIO, Worker y
  Chromium que termina en `DELIVERED`, dos proofs consumidos, cinco eventos y
  nueve registros idempotentes.

Son once escenarios de navegador en la categoría. El harness serializa el
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
- Las transiciones no aceptan el timestamp del cliente.
- Los 409/503 de POD conservan el drift normativo heredado; DRV-002 no cambia
  OpenAPI ni backend para resolverlo.
- No existe failed attempt, workflow de incidentes ni frontend COD.
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
