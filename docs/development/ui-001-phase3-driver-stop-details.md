# UI-001 fase 3: datos de la parada, "Cómo llegar" y cobro en la puerta (repartidor)

Decisión: `UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11`. Literales del owner: fase 3 aprobada el 2026-10-09 ("avanza con
la fase 3") con el diseño del repartidor, y el 2026-10-11 "Sí, como el diseño (Recommended)" (datos que ve el
repartidor), "Google Maps (Recommended)" ("Cómo llegar"), "Solo confirma el monto (Recommended)" (cobro en la
puerta) y "Completo en recolección (Recommended)" (nombre del contacto en recolección). Las preguntas completas están en la fila de `decision-log.md`, que es el registro ADR de este cambio MAJOR
(AI-01 §7). Contratos: AI-05 `DriverStop` y `listMyStops`; AI-07 `/driver/stops/:id` y
`screen_contracts.driver_stop` (`details`, `navigation`, `cod_collection`).

## Qué cambia

- `GET /driver/me/stops` (`listMyStops`) agrega a cada parada `contact_short_name`, `contact_full_name`, `zone_name`,
  `service_window`, `cod` y `navigation`. Siguen siendo solo las asignaciones ACCEPTED o ACTIVE del repartidor que consulta; otro
  repartidor, otra organización u otro rol no reciben nada de esto.
- La PWA del repartidor muestra en la lista posición y acción ("3 · Entregar"), "Siguiente"/"Pendiente", zona y
  contacto (completo en recolección, corto en entrega y devolución), ventana en hora de Mazatlán y "Cobrar $X", con un
  encabezado de paradas y cobros pendientes. El detalle abre con "Entregar a M. López" ("Recoger con Farmacia Las
  Quintas"), la guía y "Cómo llegar", y luego dirección completa con referencias, zona, ventana y el paso del cobro.
- En la puerta, el repartidor registra el cobro contra entrega con "Recibí $X" y una confirmación que nombra el monto.

## Servidor (Dispatch)

| Campo | Origen | Regla |
| --- | --- | --- |
| `contact_short_name` | `locations.locations.contact_name_ciphertext` | Solo DELIVERY y RETURN: inicial de la primera palabra, punto y última palabra (`DriverStopContactName.Shorten`, máx. 64); una palabra se deja igual; `null` en PICKUP o sin contacto |
| `contact_full_name` | `locations.locations.contact_name_ciphertext` | Solo PICKUP ("Completo en recolección"): el nombre completo con espacios normalizados, máx. 200 con "…"; `null` en DELIVERY, RETURN o sin contacto |
| `zone_name` | `locations.operating_zones.name` de la ubicación | `null` si la ubicación no tiene zona |
| `service_window` | `orders.orders.service_window_from/to` | Instantes UTC; `null` sin ventana |
| `cod` | `orders.orders.cod_expected_cents` y `finance.cod_transactions` | Solo en la parada DELIVERY con cobro > 0; `recorded` = RECORDED o RECONCILED por el monto esperado (la guarda de DELIVERED) |
| `navigation` | punto y `address_ciphertext` de la ubicación | Latitud y longitud exactas; dirección y referencias separadas de la forma que guarda Locations (`\nReferences: `), recortadas a 1000 y 500 con "…" |

- La ubicación es el origen para PICKUP y RETURN y el destino para DELIVERY (la misma regla de `DriverStopPolicy`).
- `DriverStopContactName.ForStop` elige la forma del contacto según el tipo de parada; la otra queda en `null`, así que
  el nombre completo del destinatario nunca sale del servidor. La PWA rechaza (contrato inválido) una respuesta con
  nombre completo fuera de PICKUP o con nombre corto en PICKUP.
- Todo se lee en la transacción del tenant del repartidor, con FORCE RLS (`set_config(..., true)` después de `BEGIN`).
  El descifrado ocurre después de cerrar la transacción, en un solo lote (`IDriverStopLocationReader`), para no llamar
  a Key Vault con una transacción abierta.
- `Locations:PiiProtector` decide cómo se leen (`DriverStopLocationProtectionSetting`):
  - `AzureKeyVault` (piloto): sobre ADP-001 ligado a la organización dueña, la fila, la columna y la versión de llave
    (`EnvelopeDriverStopLocationReader`, hasta 8 valores a la vez).
  - `Mock` (Development, Testing, DEV_SYNTHETIC): el mock guarda resúmenes SHA-256 que no se pueden descifrar, así
    que cada parada recibe textos sintéticos fijos ("Dirección sintética de prueba", "Contacto Sintético").
  - Cualquier otro valor: no hay forma de leerlos.
- Falla cerrada: si un valor no se puede leer (protector no disponible, valor de otra fila u organización, cifrado
  alterado) toda la respuesta es 503, nunca parcial. No se registra dirección, nombre ni cifrado en logs; los `ToString`
  de los resultados están redactados.
- Sin migraciones, roles, permisos ni grants nuevos. `recordCodCollection` no cambia: FIN-001 ya permite al repartidor
  de la asignación registrar exactamente el monto esperado y rechaza otro (`COD_AMOUNT_MISMATCH`).

## PWA del repartidor

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

- Al confirmar se encola una operación `COD_COLLECTION` (esquema de operación 2) que llama a `recordCodCollection` con
  `amount_cents` igual al esperado y la referencia `"<guía> <hora local del teléfono ISO-8601 con desfase>"`
  (máx. 200). La Idempotency-Key (`drv2-<id>-cod`) es una por cobro y se reutiliza en cada reintento.
- La cola es FIFO por orden, así que el cobro se sincroniza antes de la entrega. "Foto de entrega" y "Confirmar
  entrega" quedan deshabilitados hasta que el cobro esté registrado o guardado en el teléfono.
- El cobro se confirma por REST cuando la parada reporta `cod.recorded` con ese monto. Si el servidor responde
  `COD_ALREADY_RECORDED` y la parada ya lo reporta registrado, la operación sale de la cola.
- Mensajes en español para `COD_AMOUNT_MISMATCH`, `COD_NOT_EXPECTED`, `COD_ALREADY_RECORDED`, `COD_STATE_CONFLICT`,
  `ORDER_STATE_CONFLICT`, `INVALID_REQUEST`, `CONFLICT` y, en la entrega, `COD_NOT_RECORDED`; nunca se muestra el código.

### Retención en el teléfono

- IndexedDB `paquetenvia-driver-stops-v1` pasa a la versión 3: las instantáneas anteriores (esquema 1) se descartan y
  las operaciones en cola se migran en el mismo lugar al esquema 2 (`cod` y `rejectionCode` en `null`), sin perderse.
- La instantánea (esquema 2) se reemplaza completa con cada respuesta del servidor: una parada que deja de estar
  asignada sale del teléfono con la siguiente instantánea.
- Cerrar sesión detiene los controladores, cierra la sesión en el servidor y borra todas las instantáneas del
  teléfono antes de salir (`forgetDriverStopDetails`). 401 o 403 borra la instantánea de esa sesión.
- La cola sin conexión sobrevive al cierre de sesión y no guarda dirección ni contacto.
- Caché del Service Worker `paquetenvia-driver-shell-v6`.

## Pendiente (no se simula)

- "Hoy" y "2 hechas" del encabezado del diseño: `listMyStops` solo devuelve paradas abiertas.
- "Llamar" y "No pagó el cobro" (intento fallido) van en el cambio del intento fallido.
- Datos reales: solo datos de prueba hasta cerrar GATE-007.

## Validación

```bash
cd apps/web && pnpm run lint && pnpm run typecheck && pnpm exec vitest run && pnpm run build
dotnet test tests/Paqueteria.UnitTests --filter "FullyQualifiedName~DriverStopDetailsTests"
dotnet test tests/Paqueteria.ContractTests --filter "Category=PostgreSqlContract&FullyQualifiedName~Driver_stop_details"
dotnet test tests/Paqueteria.IntegrationTests --filter "Category=DriverStopsPwa|Category=DriverOfflineOperationsPwa"
python3 docs/normative/v0.6/tools/validate_contracts.py
```

## Rollback

Revertir el cambio. No hay esquema ni datos de servidor que deshacer. IndexedDB no baja de versión: un rollback de la
PWA debe conservar la versión 3 o superior de la base (y volver a subirla si cambia su forma), o el navegador
rechazará abrirla.
