# UI-001 fase 3: datos de la parada, "Cómo llegar" y cobro en la puerta (repartidor)

Decisión: `UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11`. Literales del owner: fase 3 aprobada el 2026-10-09 ("avanza con
la fase 3") con el diseño del repartidor, y el 2026-10-11 "Sí, como el diseño (Recommended)" (datos que ve el
repartidor), "Google Maps (Recommended)" ("Cómo llegar"), "Solo confirma el monto (Recommended)" (cobro en la
puerta), "Completo en recolección (Recommended)" (nombre del contacto en recolección), "Completo, como recolección
(Recommended)" (nombre del contacto en devolución) y "Solo el número (Recommended)" (paradas hechas hoy en el
encabezado). Las preguntas completas están en la fila de `decision-log.md`, que es el registro ADR de este cambio MAJOR
(AI-01 §7). Contratos: AI-05 `listMyStops`, `DriverStopList` y `DriverStop`; AI-07 `/driver/stops/:id` y
`screen_contracts.driver_stop` (`offline_cache`, `details`, `navigation`, `cod_collection`).

## Qué cambia

- `GET /driver/me/stops` (`listMyStops`) responde `{ "stops": [...], "completed_today": n }`. Cada parada agrega
  `details_available`, `contact_short_name`, `contact_full_name`, `zone_name`, `service_window`, `cod` y `navigation`.
  Siguen siendo solo las asignaciones ACCEPTED o ACTIVE del repartidor que consulta; otro repartidor, otra organización
  u otro rol no reciben nada de esto.
- La PWA del repartidor muestra en la lista posición y acción ("3 · Entregar"), "Siguiente"/"Pendiente", zona y
  contacto (completo en recolección y devolución, corto en entrega), ventana en hora de Mazatlán y "Cobrar $X", bajo el
  encabezado "Hoy · N paradas · K hechas" con los cobros pendientes. El detalle abre con "Entregar a M. López"
  ("Recoger con" o "Devolver a Farmacia Las Quintas"), la guía y "Cómo llegar", y luego dirección completa con
  referencias, zona, ventana y el paso del cobro.
- En la puerta, el repartidor registra el cobro contra entrega con "Recibí $X" y una confirmación que nombra el monto.

## Servidor (Dispatch y Locations)

| Campo | Origen | Regla |
| --- | --- | --- |
| `details_available` | lectura de los datos protegidos | `true` si se leyeron; `false` con `details=false` o si no se pudieron leer: nombres y `navigation` en `null` |
| `contact_short_name` | `locations.locations.contact_name_ciphertext` | Solo DELIVERY: inicial de la primera palabra, punto y última palabra (`DriverStopContactName.Shorten`, máx. 64); una palabra se deja igual; `null` en PICKUP, RETURN o sin contacto |
| `contact_full_name` | `locations.locations.contact_name_ciphertext` | Solo PICKUP y RETURN: el nombre completo normalizado, máx. 200 con "…"; `null` en DELIVERY o sin contacto |
| `zone_name` | `locations.operating_zones.name` de la ubicación | Normalizado y recortado a 200 con "…"; `null` si no hay zona |
| `service_window` | `orders.orders.service_window_from/to` | Instantes UTC; `null` sin ventana |
| `cod` | `orders.orders.cod_expected_cents` y `finance.cod_transactions` | Solo en la parada DELIVERY con cobro > 0; `recorded` = RECORDED o RECONCILED por el monto esperado (la guarda de DELIVERED) |
| `navigation` | punto y `address_ciphertext` de la ubicación | Latitud y longitud exactas; dirección y referencias separadas con `LocationAddressText.Split` (la misma forma con la que Locations las guarda), recortadas a 1000 y 500 con "…" |
| `completed_today` | `orders.order_events` | Ver abajo; solo el número |

- La ubicación es el origen para PICKUP y RETURN y el destino para DELIVERY (la misma regla de `DriverStopPolicy`).
- `DriverStopContactName.ForStop` elige la forma del contacto según el tipo de parada; la otra queda en `null`, así que
  el nombre completo del destinatario de una entrega nunca sale del servidor.
- Texto: `DriverStopText` trata los espacios y los caracteres de formato Unicode (categoría Cf, como U+200B o U+FEFF)
  como espacio, deja `null` lo que queda vacío y recorta por elementos de texto completos, sin lanzar excepciones con
  nombres o zonas patológicos (una inicial que por sí sola no cabe se omite).
- Todo se lee en la transacción del tenant del repartidor, con FORCE RLS (`set_config(..., true)` después de `BEGIN`).
  El descifrado ocurre después de cerrar la transacción, en un solo lote y solo si se pidieron los datos, por el puerto
  `ILocationPiiReader` de `Locations.Application`, con el mismo modo de `Locations:PiiProtector` que usa Locations:
  - `AzureKeyVault` (piloto): sobre ADP-001 ligado a la organización dueña, la fila, la columna y la versión de llave
    (`EnvelopeLocationPiiReader`, hasta 8 valores a la vez; si el servicio de llaves no responde, deja de intentar el
    resto del lote).
  - `Mock` (Development, Testing, DEV_SYNTHETIC): el mock guarda resúmenes SHA-256 que no se pueden descifrar, así
    que cada parada recibe textos sintéticos fijos (`SyntheticLocationPiiReader`).
  - `Disabled`: no hay forma de leerlos (`DisabledLocationPiiReader`).
- Una parada cuyos datos no se pueden leer (servicio de llaves caído, valor de otra fila u organización, cifrado
  alterado, sin protector) se lista igual con `details_available: false`; la respuesta nunca falla ni es parcial por eso.
  Solo se cuenta en la métrica `dispatch.driver_stops.details_unavailable`; no se registra dirección, nombre ni cifrado
  y los `ToString` de los resultados están redactados.
- `details=false`: las mismas paradas sin leer ningún dato protegido y sin llamar al servicio de llaves. Cualquier otro
  valor, otro parámetro o uno repetido es 409 `INVALID_REQUEST`.
- Toda respuesta lleva `Cache-Control: no-store`.
- `completed_today`: cuántos cambios de estado a `PICKED_UP` (recolección hecha), `DELIVERED` (entrega hecha) o
  `RETURNED` (devolución hecha) hizo el repartidor que consulta (actor del evento), en órdenes de la organización
  seleccionada que están o estuvieron asignadas a ese repartidor, durante el día calendario de America/Mazatlan que
  contiene la hora de la solicitud. El inicio y el fin del día los calcula PostgreSQL
  (`date_trunc('day', now AT TIME ZONE 'America/Mazatlan') AT TIME ZONE 'America/Mazatlan'`), así que el cambio de
  horario o el reloj del teléfono no lo mueven. Un intento fallido, una reprogramación o un cambio hecho por otra
  persona no cuentan. `DriverStopsCompletedToday` guarda la zona y los estados.
- Sin migraciones, roles, permisos ni grants nuevos. `recordCodCollection` no cambia: FIN-001 ya permite al repartidor
  de la asignación registrar exactamente el monto esperado y rechaza otro (`COD_AMOUNT_MISMATCH`).

## PWA del repartidor

### Lectura de la respuesta

- El sobre es exacto (`stops` y `completed_today`); si no, toda la respuesta es inválida ("La respuesta no pudo
  validarse de forma segura") y la instantánea guardada no se toca.
- Cada parada se revisa por separado: una parada con identidad, estado, resumen, ventana, zona o cobro inválidos se deja
  fuera; una parada cuyos datos protegidos rompen el contrato se conserva sin ellos. Solo se reporta el número de
  paradas dejadas fuera o sin datos (`stopsRejected`), nunca un valor.
- La instantánea guardada se valida estricta: cualquier error la descarta completa.
- Sin datos de una parada (y sin datos guardados de esa misma parada): "Datos de entrega no disponibles por ahora" en
  lugar del contacto y de "Cómo llegar", el resumen de la dirección y solo el verbo ("Entregar").
- Cambios aditivos futuros del contrato: el sobre y cada parada aceptan solo las propiedades conocidas, así que en una
  pestaña vieja un miembro nuevo del sobre invalida toda la respuesta y uno nuevo de una parada la deja fuera. Antes de
  agregar una propiedad, publicar primero una PWA que la tolere (o versionar la respuesta), y solo después enviarla
  desde el servidor.

### Encabezado

"Hoy · N paradas · K hechas · 1 cobro pendiente $1,250.00": K es `completed_today` y N son las paradas abiertas de la
lista más K. K se muestra solo mientras la respuesta que lo trajo sea del día actual en America/Mazatlan; si no, el
encabezado dice "Hoy · N paradas" con las abiertas. Sin paradas abiertas, el estado vacío muestra el encabezado solo si
K es mayor que cero.

### "Cómo llegar"

Enlace a `https://www.google.com/maps/search/?api=1&query=<lat>,<lng>` (coordenadas con hasta 7 decimales, codificadas)
en una pestaña nueva con `rel="noopener noreferrer"`. Abre la app de Google Maps si está instalada o su página web. No
hay solicitud a nuestro servidor ni a Directions/Routes, ni cálculo de ruta, distancia o tiempo (GATE-003).

### Cobro en la puerta

| Situación | Pantalla |
| --- | --- |
| Parada DELIVERY en DELIVERING con cobro pendiente | "Cobro $X", botón "Recibí $X" y confirmación "¿Recibiste $X?" |
| Cobro guardado en el teléfono | "Cobro guardado en el teléfono; se registrará al sincronizar." |
| Cobro registrado en el servidor | "El cobro ya está registrado." |
| Antes de llegar a la puerta | "Cobra este monto en efectivo al entregar." |
| Entrega guardada sin cobro guardado | "Recibí $X" otra vez y "La entrega ya está guardada en el teléfono. Se enviará después de registrar el cobro." |

- Al confirmar se encola una operación `COD_COLLECTION` (esquema de operación 2) que llama a `recordCodCollection` con
  `amount_cents` igual al esperado y la referencia `"<guía> <hora local del teléfono ISO-8601 con desfase>"`
  (máx. 200). La Idempotency-Key (`drv2-<id>-cod`) es una por cobro y se reutiliza en cada reintento.
- La cola sigue el orden de dependencia de cada orden, no FIFO simple: llegada, recolección, traslado, inicio de la
  entrega, cobro y entrega (`orderDriverOperations`); entre órdenes manda la acción guardada más antigua. Así el cobro
  va antes de la entrega aunque se haya guardado después. "Foto de entrega" y "Confirmar entrega" quedan deshabilitados
  hasta que el cobro esté registrado o guardado en el teléfono.
- Entrega esperando el cobro (`driverDeliveryAwaitsCash`): si hay una entrega guardada, la parada tiene cobro
  pendiente y no hay cobro guardado (el repartidor descartó un cobro retenido, o la entrega se guardó antes de esta
  versión y se migró en su lugar), la entrega no se envía ni se aplica en la vista: la parada sigue "En entrega",
  "Recibí $X" se ofrece otra vez y el nuevo cobro se sincroniza primero. Si el servidor rechaza una entrega con
  `COD_NOT_RECORDED` y el cobro sigue pendiente sin cobro guardado, la entrega vuelve a esperar en lugar de pedir
  atención.
- El cobro se confirma por REST cuando la parada reporta `cod.recorded` con ese monto. Si el servidor responde
  `COD_ALREADY_RECORDED` y la parada ya lo reporta registrado, la operación sale de la cola.
- Mensajes en español para `COD_AMOUNT_MISMATCH`, `COD_NOT_EXPECTED`, `COD_ALREADY_RECORDED`, `COD_STATE_CONFLICT`,
  `ORDER_STATE_CONFLICT`, `INVALID_REQUEST`, `CONFLICT` y, en la entrega, `COD_NOT_RECORDED`; nunca se muestra el código.

### Sincronización

- Las lecturas que confirman acciones en cola usan `details=false`: nunca dependen del servicio de llaves y se mezclan
  con la instantánea guardada sin borrar los datos de una parada que sigue siendo la misma (misma orden y tipo). Una
  parada que llega sin datos provoca una sola lectura completa; una lectura completa sin datos tampoco borra los
  guardados ni pide otra. Antes de mostrar nada (la app recién abierta), la base de la mezcla es la instantánea
  guardada de la sesión, así que una primera lectura sin datos tampoco borra los del teléfono.
- Un controlador cerrado (cierre de sesión) o un pase abortado nunca responde una lista vacía: lanza "cancelled", el
  pase termina y la acción queda como estaba. El planificador revisa la señal de aborto antes de cada paso que borra.
  Antes de que el controlador de paradas arranque, la lectura lanza "recoverable" y la acción se reintenta.
- Una parada ausente de una respuesta que dejó fuera alguna parada nunca se toma como desaparecida: la acción no se
  borra.

### Retención en el teléfono

- IndexedDB `paquetenvia-driver-stops-v1` pasa a la versión 3: las instantáneas anteriores (esquema 1) se descartan y
  las operaciones en cola se migran en el mismo lugar al esquema 2 (`cod` y `rejectionCode` en `null`), sin perderse.
- La instantánea (esquema 2) guarda las paradas y `completedToday`, y se reemplaza con cada respuesta del servidor: una
  parada que deja de estar asignada sale del teléfono con la siguiente instantánea. Guarda el nombre del contacto y la
  dirección de las paradas asignadas en este momento (AI-07 `driver_stop.offline_cache`).
- Cerrar sesión detiene los controladores, cierra la sesión en el servidor y borra todas las instantáneas del
  teléfono antes de salir (`forgetDriverStopDetails`). 401 o 403 borra la instantánea de esa sesión.
- La cola sin conexión sobrevive al cierre de sesión y no guarda dirección ni contacto.
- Caché del Service Worker `paquetenvia-driver-shell-v6`.

## No se hizo (a propósito)

- Un validador compartido de propiedades exactas entre `driver-stop.ts`, `external-offer.ts` y `operation-contract.ts`:
  cada uno conserva el suyo para no cambiar contratos fuera de esta tarea.
- "No pagó el cobro" va en el cambio del intento fallido. "Llamar" es VOICE-001.
- Datos reales: solo datos de prueba hasta cerrar GATE-007.

## Validación

```bash
cd apps/web && pnpm run lint && pnpm run typecheck && pnpm exec vitest run && pnpm run build
dotnet test tests/Paqueteria.UnitTests --filter "FullyQualifiedName~DriverStopDetailsTests|FullyQualifiedName~LocationPiiReaderTests"
dotnet test tests/Paqueteria.ContractTests --filter "Category=PostgreSqlContract&FullyQualifiedName~DispatchPostgreSqlContractTests"
dotnet test tests/Paqueteria.IntegrationTests --filter "FullyQualifiedName~DispatchHttpTests|Category=DriverStopsPwa|Category=DriverOfflineOperationsPwa"
python3 docs/normative/v0.6/tools/validate_contracts.py
```

## Rollback

Revertir el cambio. No hay esquema ni datos de servidor que deshacer. IndexedDB no baja de versión: un rollback de la
PWA debe conservar la versión 3 o superior de la base (y volver a subirla si cambia su forma), o el navegador
rechazará abrirla. Una PWA anterior no entiende el sobre `DriverStopList`: servidor y PWA se revierten juntos.
